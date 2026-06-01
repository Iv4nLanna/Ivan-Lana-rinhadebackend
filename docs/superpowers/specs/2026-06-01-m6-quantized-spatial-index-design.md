# Design: M6 — Índice espacial exato + quantização int8

**Data:** 2026-06-01
**Marco:** M6 (Rinha de Backend 2026)
**Stack:** C# .NET 9, `System.IO.MemoryMappedFiles`, `Span<T>`/`stackalloc`, xUnit
**Metodologia:** Evolutiva — atacar o gargalo medido (memory-bound), com duas alavancas combinadas

---

## Contexto

O **M5** provou empiricamente que o KNN é **memory-bound**, não compute-bound: o custo dominante é *ler* os 168 MB do dataset a cada request (3M vetores × 14 floats), não a aritmética. SIMD não moveu o score (continua −6000). O lever real é **ler menos dados por request**.

A referência `fksegundo/rinha-dotnet` (competidor) confirma o caminho: **busca exata** (não aproximada) com um **índice espacial sobre vetores quantizados**, validando que com D=14 (baixa dimensão) dá pra ser exato e rápido. O M6 aplica duas alavancas independentes que atacam o memory-bound por eixos diferentes:

1. **Quantização int8** — cada vetor passa de 56 bytes (14×float32) para 14 bytes (14×int8). Lê **4x menos bytes** por vetor tocado.
2. **Índice espacial exato (KD-Tree)** — em vez de varrer os 3M, poda a árvore e toca só ~milhares de vetores. Lê **muito menos vetores**.

Combinadas, o dado lido por request cai de `3M × 56 B = 168 MB` para a ordem de `~milhares × 14 B ≈ dezenas/centenas de KB`.

> **Numeração:** o design original (`2026-05-31-...-design.md`) listava ANN como M7 (HNSW) e quantização dentro de "IVF-PQ". Como o profiler apontou ANN exato como o passo necessário e o competidor validou árvore exata + quantização, isso chega agora como **M6**, substituindo o "AOT + Utf8JsonReader" que era o M6 do plano original (re-sequenciado pra depois).

---

## Objetivo

Substituir a busca brute-force (varre 3M) por uma busca **exata** que toca uma fração dos vetores, sobre vetores **quantizados em int8**, mantendo o recall altíssimo (fp/fn ≈ 0, como hoje).

**Não-objetivos (fora do M6):**
- Não usar busca aproximada (HNSW/IVF) — mantemos exato.
- Não fazer AOT, `Utf8JsonReader`, mlock/pretouch, nem trocar o nginx (marcos futuros).
- Não mudar o contrato HTTP nem as 14 fórmulas de normalização.

---

## Peça 1 — Quantização int8 (metric-preserving)

### O esquema

Quase todas as 14 dims vivem em `[0,1]`; as dims **5 e 6** podem ser **−1** (sentinela quando não há transação anterior). O intervalo global de qualquer dim é, portanto, `[−1, 1]`.

Quantização **afim uniforme** (mesma escala para todas as dims) mapeando `[−1, 1] → [0, 255]`:

```
q(x) = round( (clamp(x, -1, 1) + 1) / 2 * 255 )      // float → byte
```

Exemplos: `x=-1 → 0`, `x=0 → 128`, `x=1 → 255`.

### Por que uniforme (e não por-dimensão)

Distância euclidiana sobre quantização **por-dimensão** (cada dim com sua escala) viraria uma distância *ponderada* — mudaria o ranking do KNN. Com **uma escala global** para todas as dims, vale:

```
q(a) − q(b) = (a − b) · 255/2
⇒ Σ (q(a_d) − q(b_d))²  =  (255/2)² · Σ (a_d − b_d)²
```

Ou seja, a distância² quantizada é a distância² real **multiplicada por uma constante**. Como a constante é a mesma para todos os pares, o **ranking dos vizinhos é idêntico** ao dos floats — a única diferença vem do arredondamento.

### Distância em inteiros

Como tudo é byte, a distância² é calculada com inteiros: `Σ (qq[d] − qr[d])²`. Faixa: `diff ∈ [−255,255] → diff² ≤ 65025 → ×14 ≤ 910.350`, cabe folgado em `int32`. Sem `float`, sem `sqrt`. (Abre espaço pra SIMD de inteiros depois, se valer.)

### Custo de recall

Dims em `[0,1]` usam metade da faixa `[−1,1]` → resolução efetiva ~128 níveis (~0.0078). É fino o suficiente pro ranking dos 5 vizinhos quase nunca mudar. **Validamos contra os labels do `test-data.json`** (fp/fn devem ficar ~0, como no M5).

---

## Peça 2 — Índice espacial exato (KD-Tree)

### Construção (offline)

Uma árvore binária. Em cada nó: escolhe uma dimensão de corte e um valor de corte (mediana dos pontos naquele nó, pra balancear), separa os pontos em "< corte" (esquerda) e "≥ corte" (direita), recursivamente até folhas pequenas. A dimensão de corte cicla pelas 14 dims (ou escolhe a de maior variância — decisão do plano).

### Busca k-NN com poda (exata)

1. Desce até a região (folha) onde o query cai → acha 5 candidatos próximos rápido.
2. Sobe de volta; só entra no "outro lado" de um corte **se o plano de corte estiver mais perto que o 5º vizinho atual**. Senão, **poda** o galho inteiro (pula sem olhar).

É **exato**: a poda nunca descarta um ponto que poderia entrar no top-5. O resultado é idêntico ao brute-force *sobre os vetores quantizados*.

O "top-5 atual" durante a travessia reusa a ideia do **buffer de 5 do M4** (inserção limitada, stackalloc, desempate por índice).

### Risco principal (a ser medido): poda em D=14

KD-Tree poda muito bem em baixa dimensão (2-3), mas a eficácia **cai conforme a dimensão sobe**. Em D=14, a poda ajuda, mas pode visitar uma fração não-trivial dos nós (não os ~log N ideais). **Mitigação:** medir quantos nós a busca visita; se a poda for fraca, trocar por **Ball-Tree** (hiperesferas, poda melhor nessa faixa de dimensão) — a interface de busca é a mesma, só muda a estrutura interna. Esse é o gate de medição do marco.

---

## Como as duas peças conversam

Atacam o **mesmo gargalo (ler dados)** por eixos **independentes**, então **multiplicam**:

| | Reduz | Ganho |
|---|---|---|
| KD-Tree | *quantos* vetores você toca | 3M → ~milhares |
| Quantização | *quantos bytes* cada vetor custa | 56 → 14 B |

**Sinergia de cache:** a travessia da árvore faz acesso aleatório à memória (pula de nó em nó), o ponto fraco dela. Mas vetores de 14 bytes fazem caber ~4x mais vetores por linha de cache/página → a quantização **suaviza justamente o ponto fraco da árvore**.

**Representação única:** a árvore é construída **sobre os vetores já quantizados**; cortes, travessia e distância operam todos no espaço int8. Um formato só, do build à busca.

**"Exato" honesto:** o combo é exato *em relação aos dados quantizados*. A árvore não erra (poda nunca descarta candidato válido); a **única** fonte de imprecisão é o arredondamento da quantização (~0.0078). Validado contra os labels.

---

## Arquitetura

### Fase offline (roda uma vez, antes/no build da imagem)

Um passo de **preprocessing** que:
1. Lê os vetores de referência (de `references.json.gz` ou do `references.bin` atual).
2. Quantiza cada vetor para 14 bytes.
3. Constrói a KD-Tree.
4. Grava `data/index.bin`: header + estrutura da árvore + vetores quantizados (em ordem da árvore) + labels.

A lógica de quantização e build da árvore mora na assembly `Api` (reusada por testes e runtime); o entrypoint do build é um modo de console / projeto de preprocessing (decisão do plano).

### Fase runtime (cada request)

1. `mmap` do `data/index.bin` (truque do M3 — file-backed, evictável, sem heap).
2. Normaliza o request → vetor float[14] (igual hoje).
3. **Quantiza o query** para `Span<byte>` de 14 (stackalloc).
4. Percorre a KD-Tree com poda, mantendo os 5 mais próximos.
5. Mapeia os 5 índices para labels → `fraud_score = fraudes/5`.

`ReferenceDataset` passa a carregar `index.bin` (mmap) em vez do `references.bin` plano; `KnnSearch` brute-force é substituído pela busca na árvore.

### Orçamento de RAM

```
vetores quantizados: 3M × 14 B   = 42 MB
labels:              3M × 1 B    =  3 MB
nós da árvore:       ~poucos B/nó ≈ 12–24 MB (depende do layout)
──────────────────────────────────
total index.bin     ≈ 45–70 MB   (mmap, file-backed, evictável)
```

Bem dentro dos 175 MB/instância — e **muito** menor que o grafo de ~192 MB do HNSW (porque KD-Tree guarda só cortes, não listas de vizinhos). É por isso que a árvore exata cabe onde o HNSW não cabia.

---

## Tratamento de erros

| Situação | Resposta |
|---|---|
| `index.bin` ausente em runtime | fallback: build em memória a partir do JSON (caminho de teste) ou erro claro no startup |
| Query fora de `[−1,1]` numa dim | `clamp` antes de quantizar (já previsto na fórmula) |
| Contrato HTTP | inalterado (400/503/200 como hoje) |

---

## Estratégia de testes

- **Quantizer (unit):** `q(-1)=0`, `q(0)=128`, `q(1)=255`, `clamp` fora da faixa, round-trip de faixa.
- **KD-Tree exatidão (unit, o teste central):** em datasets pequenos aleatórios, para muitas queries aleatórias, o top-5 da árvore **deve ser igual** ao top-5 do brute-force *sobre os mesmos vetores quantizados*. Prova que a poda não descarta candidato válido. Inclui empates e `count < 5`.
- **Busca ponta a ponta:** os testes de comportamento do KNN atuais (all-legit→0, all-fraud→1, 3/5→0.6, etc.) adaptados para o novo caminho — devem continuar válidos (o resultado quantizado bate em valores bem distintos).
- **EndpointTests:** contrato HTTP inalterado.
- **Validação de recall (medição, não unit):** na Task de benchmark, rodar contra o `test-data.json` e conferir que fp/fn continuam ~0 e a taxa de falha não piora por causa da quantização.

---

## Verificação e medição (vs M5)

1. `dotnet test` — tudo verde.
2. Build offline do `index.bin`; conferir tamanho (~45–70 MB).
3. `docker compose up` → `/ready`.
4. Latência de uma request (vs ~20ms do M5) — esperado cair bastante.
5. **Quantos nós a busca visita** (instrumentação temporária ou log) — o gate do KD-Tree vs Ball-Tree.
6. k6 oficial (120s → 900 req/s) → comparar p99, score, failure_rate, fp/fn contra **M5 (p99 2002ms, score −6000, 317 corretas, fp/fn=0)**.

**Expectativa honesta:** é o primeiro marco com chance real de **sair do −6000** (lê ~1000x menos dados). O risco é a poda do KD-Tree em D=14 ser fraca — por isso o gate de medição e o fallback Ball-Tree. Mesmo que o KD-Tree não atinja o ideal, a quantização sozinha já dá um ganho garantido de 4x.

---

## Decomposição (para o plano)

Marco grande; o plano deve quebrar em tasks isoladas e testáveis:
1. **Quantizer** (float→byte) + testes.
2. **KD-Tree build + busca exata** (em memória, sobre vetores quantizados) + teste de exatidão vs brute-force.
3. **Formato `index.bin` + build offline** (serialização da árvore + vetores + labels).
4. **Runtime:** `ReferenceDataset` carrega `index.bin` via mmap; `KnnSearch`/Program usam a busca na árvore com query quantizado.
5. **Benchmark + documentação** (Obsidian: notas de conceito Quantização e KD-Tree/Ball-Tree; marco M6; medição vs M5).

---

## Conceitos para o Obsidian

- **Quantização (vetorial)** em `TEORIA/Performance e Sistemas/`: float32→int8, escala afim uniforme, por que preserva o ranking, distância em inteiros, trade-off de resolução vs recall.
- **KD-Tree / busca espacial** (complementa `[[KNN Exato vs ANN]]`): cortes, poda, por que a eficácia cai com a dimensão, Ball-Tree como alternativa em D médio.
