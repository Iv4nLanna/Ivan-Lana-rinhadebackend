# Design: M7 — Índice por Partição ("gavetas")

**Data:** 2026-06-01
**Marco:** M7 (Rinha de Backend 2026)
**Stack:** C# .NET 9, `System.IO.MemoryMappedFiles`, `Span<T>`/`stackalloc`, xUnit
**Metodologia:** Evolutiva — atacar o gargalo medido (memory-bound), começando pela versão mais simples e medindo

---

## Contexto

O **M5** (brute-force SIMD) marcou −6000 com 317 respostas corretas; provou-se *memory-bound* (lê os 168 MB do dataset por request). O **M6** (KD-Tree exata sobre vetores quantizados) **regrediu** (15 respostas corretas, ~3x mais lento) e foi **revertido** — a poda por mediana genérica não funciona em D=14 (maldição da dimensionalidade); o acesso aleatório da árvore é pior que o scan sequencial do M5. O `master` está de volta no M5.

A lição do M6 e a análise da referência `fksegundo/rinha-dotnet` (que cravou p99 0.437ms / score 6000) apontam o caminho certo: **particionar os 3M em "gavetas" usando as dimensões categóricas** (que separam os dados de verdade), e só comparar dentro da gaveta do query. É uma abordagem clássica (IVF / inverted file). A quantização int8 do M6, que funcionou (RAM 168→45 MB), é mantida.

---

## Objetivo

Reduzir o nº de vetores comparados por request de 3M para a ordem de dezenas de milhares, particionando o dataset por dimensões discriminativas e escaneando só a partição do query. Quantização int8 mantida.

**Versão deste marco: SIMPLES (aproximada).** Escaneia apenas a partição do query (não as vizinhas). Mede-se velocidade e recall (fp/fn); se o recall cair demais, um marco futuro adiciona "espiar partições vizinhas".

**Não-objetivos (futuro):**
- Espiar partições vizinhas (rumo a exato) — só se o recall medido exigir.
- int16 + SIMD `MultiplyAddAdjacent` no scan — refinamento de velocidade depois.
- mlock/pretouch, AOT, load balancer custom — marcos posteriores.
- Mudar o contrato HTTP ou as 14 fórmulas de normalização.

---

## A chave de partição ("número da gaveta")

Calculada de **4 dimensões** do vetor normalizado (float), escolhidas por serem discriminativas:

- **dim 9** (`is_online`): bit 0
- **dim 10** (`card_present`): bit 1
- **dim 11** (`merchant desconhecido`): bit 2
- **dim 0** (`amount`): bucket de 0..3 por 3 cortes

Essas 3 dims são categóricas (valem 0 ou 1) → separadores limpos. O `amount` é contínuo, então dividimos em 4 faixas.

```
flags = (dim9>0.5 ? 1:0) | (dim10>0.5 ? 2:0) | (dim11>0.5 ? 4:0)   // 0..7
amountBucket = nº de cortes que amount ultrapassa                   // 0..3
key = amountBucket * 8 + flags                                      // 0..31
```

→ **32 partições.**

**Cortes do amount (data-driven):** calculados offline como os **quartis** de `dim 0` sobre os 3M (percentis 25/50/75). Assim as 4 faixas de valor ficam **equilibradas** (~igual nº de vetores por faixa), evitando uma gaveta gigante. Os 3 cortes (floats) são gravados no índice e relidos no runtime, pra build e busca usarem exatamente os mesmos.

> **Risco conhecido:** as 3 dims categóricas podem ser desbalanceadas (ex.: a maioria das transações é `card_present` e `known merchant`). Se uma gaveta ficar gigante mesmo com os quartis de valor, mede-se e, num passo futuro, adicionam-se mais dims de split (ex.: dim 8 `tx_count`, dim 2 `amount/avg`), como faz o competidor. Começamos com 32 e medimos.

---

## Formato do índice (`index.bin`)

```
[int32 count]
[int32 numPartitions]               (= 32)
[3 × float32 amountCuts]            (quartis de dim 0)
[(numPartitions+1) × int32 offsets] (prefix-sum: partição p ocupa [offsets[p], offsets[p+1]))
[count × 14 bytes]                  vetores quantizados, AGRUPADOS por partição
[count × 1 byte]                    labels, na mesma ordem agrupada
```

Tamanho ≈ `4 + 4 + 12 + 33×4 + count×15` ≈ 45 MB (igual ao M6 + cabeçalho pequeno). mmap (file-backed, evictável — truque do M3).

---

## Componentes

| Arquivo | Responsabilidade |
|---|---|
| `src/Api/Detection/Quantizer.cs` | int8 afim uniforme `[-1,1]→[0,255]` (ressuscitado do M6, que funcionou) |
| `src/Api/Detection/PartitionKey.cs` | `ComputeKey(ReadOnlySpan<float> vec, ReadOnlySpan<float> cuts) → int` (0..31). Compartilhado build+runtime. |
| `src/Api/Detection/PartitionedIndex.cs` | busca: dado os spans do índice + query quantizado + key, escaneia a partição e devolve `fraud_score` (reusa o buffer top-5 do M4) |
| `src/Api/Data/IndexBuilder.cs` | calcula quartis, atribui keys, agrupa, quantiza, serializa `index.bin`; e `BuildInMemory` p/ testes |
| `src/IndexGen/` | console que gera `data/index.bin` a partir do `references.bin` (float) |
| `src/Api/Data/ReferenceDataset.cs` | carrega `index.bin` (mmap) ou monta em memória do JSON (testes); expõe os spans + offsets + cuts |
| `src/Api/Program.cs` | `ComputeScore`: normaliza → calcula key → quantiza query → `PartitionedIndex.Search` |

---

## Fluxo

**Offline (uma vez):** lê 3M floats → calcula os 3 quartis de `dim 0` → para cada vetor calcula a key → agrupa por key (ordena por key) → quantiza → grava `index.bin` (offsets + cuts + vetores + labels agrupados).

**Runtime (por request):**
1. Normaliza o request → `float[14]` (igual hoje).
2. `key = PartitionKey.ComputeKey(vetor, cuts)`.
3. Quantiza o vetor → `byte[14]` (stackalloc).
4. `range = [offsets[key], offsets[key+1])`.
5. Scan da partição: distância² (int) contra cada vetor do range, mantendo os 5 menores (buffer top-5 do M4) → `fraud_score = fraudes/5`.

Se a partição estiver vazia (nenhum vetor com aquela combinação), `filled<5` → divide por 5 mesmo (score baixo). É um caso de borda raro; aceitável na versão simples e medido no benchmark.

---

## Tratamento de erros

| Situação | Resposta |
|---|---|
| `index.bin` ausente | fallback: monta o índice em memória a partir do JSON (caminho de teste) |
| Partição vazia | retorna score com `filled<5` (divide por 5) — sem crash |
| query fora de faixa | `clamp` na quantização (já previsto) |
| Contrato HTTP | inalterado (400/503/200) |

---

## Testes

- **PartitionKey (unit):** combinações de online/card/merchant → bits certos; amount em cada faixa → bucket certo; bordas dos cortes.
- **PartitionedIndex (unit):** dataset pequeno com partições conhecidas; a busca dentro da partição bate com um brute-force restrito àquela partição (top-5 com mesmo desempate). Inclui partição vazia e `count<5`.
- **IndexBuilder (unit):** round-trip do `index.bin` (offsets corretos, cuts gravados, busca pelo arquivo == busca em memória).
- **DatasetTests:** carrega via fallback JSON; confere count/offsets/cuts.
- **EndpointTests:** contrato HTTP (hermético, dataset pequeno próprio).
- **Recall (medição, não unit):** no benchmark, conferir fp/fn vs o test-data — é aqui que se vê o custo da aproximação (só a gaveta do query). Comparar com M5 (fp/fn=0).

---

## Verificação e medição (vs M5)

1. `dotnet test` verde.
2. Gera `data/index.bin`; confere tamanho (~45 MB) e **distribuição das gavetas** (imprimir nº de vetores por partição — detecta gaveta gigante).
3. `docker compose up` → `/ready`.
4. Latência de 1 request (vs ~20ms do M5) — esperado cair bastante (escaneia ~1 gaveta).
5. k6 oficial → p99, score, failure_rate, **fp/fn** (o custo da aproximação). Comparar com **M5 (p99 2002ms, score −6000, 317 corretas, fp/fn=0)**.

**Expectativa honesta:** é o primeiro marco com chance real de **sair do −6000** (escaneia ~33x menos dados, de forma cache-friendly — ao contrário da árvore). Dois riscos medidos no fim: (a) gaveta desbalanceada (uma partição gigante mantém o custo alto p/ alguns queries → adicionar mais dims de split depois); (b) recall (só a gaveta do query pode perder vizinhos de borda → fp/fn sobe → adicionar "espiar vizinhas" depois). A versão simples primeiro, e os números dizem o próximo passo.

---

## Conceito para o Obsidian (na documentação do marco)

Nota de conceito **IVF / Índice por Partição** (complementa `[[KNN Exato vs ANN]]`): a ideia de gavetas, por que particionar pelas dims categóricas funciona onde a KD-Tree falhou (explorar a estrutura dos dados vs cortes genéricos), e o trade-off aproximado (só a gaveta) vs exato (espiar vizinhas). Mais a nota de marco M7 com a medição real e o veredito sobre balanceamento/recall.
