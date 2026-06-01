# Design: M4 — Top-K em uma passada O(n)

**Data:** 2026-06-01
**Marco:** M4 (Rinha de Backend 2026)
**Stack:** C# .NET 9, xUnit
**Metodologia:** Evolutiva — uma otimização por marco, medida contra baseline

---

## Contexto

O benchmark oficial (k6, 1→900 req/s em 120s) do **M3** deu **score −6000**: p99 = 2002ms (teto), 100% de falha, 16.789 timeouts, zero respostas 200. O M3 resolveu RAM/OOM (o sistema sobe e cabe em 175 MB), mas **não tocou em latência**.

A causa raiz está no `KnnSearch.Search` (`src/Api/Detection/KnnSearch.cs`), que a cada request:

```csharp
var distances = new float[count];                          // aloca ~12 MB POR REQUEST
// ... preenche distances ...
distances.Select((d,i)=>(d,i)).OrderBy(x=>x.dist).Take(5)  // ordena 3M itens O(n log n) POR REQUEST
```

Medição: **uma request sozinha, sem concorrência, já leva ~1.0–1.1s**. A 900 req/s isso satura instantaneamente. O `.OrderBy()` de 3 milhões de itens e a alocação de 12 MB dominam o tempo.

> Nota de sequência: no design original (`2026-05-31-rinha-backend-2026-design.md`) essa técnica era o "M2 = Top-K heap". Ela foi pulada quando o trabalho de dataset binário ocupou o slot do M2. Chega agora como M4. O `stackalloc float[14]` + `Span<T>` que o design listava como M4 (GC pressure do vetor de query) é re-sequenciado para M5 — é uma otimização menor que não move o gargalo atual.

---

## Objetivo

Eliminar, no hot-path do KNN, as duas fontes de custo dominante por request:
1. A alocação de `new float[count]` (~12 MB).
2. A ordenação `.OrderBy()` de 3M itens (O(n log n)).

Substituir por uma **única passada O(n)** que mantém os 5 vizinhos mais próximos, sem alocação no heap e sem sort.

**Não-objetivos (mantém o marco isolado):**
- Não otimizar a normalização nem o vetor de query (`stackalloc float[14]` + `Span<T>` → M5).
- Não introduzir SIMD (→ M5/M6).
- Não mudar o algoritmo de exato para aproximado (HNSW → M7+).
- Não mudar a assinatura pública `Search(float[] query, ReferenceDataset ds)`.

---

## Algoritmo: inserção limitada em buffer de 5

Como **k = 5** é minúsculo e fixo, a estrutura ótima não é um heap binário nem um sort parcial — é uma **inserção limitada** num buffer de 5 slots, varrido linearmente. Custo O(n·k) = O(5n), cache-friendly, zero alocação no heap.

```
buffer: 5 slots de (float dist, int idx), em stackalloc  (40 bytes na stack)
worstSlot: índice do slot com a maior distância entre os 5
filled: quantos slots já preenchidos (0..5)

para cada i em 0..count-1:
    dist = distância²(query, GetVector(i))     // sem sqrt — monotônica, não muda o ranking
    se filled < 5:
        buffer[filled] = (dist, i); filled++
        se filled == 5: recalcula worstSlot
    senão se dist < buffer[worstSlot].dist:     // comparação ESTRITA (ver empates)
        buffer[worstSlot] = (dist, i)
        recalcula worstSlot (varre os 5)

fraudCount = soma de GetLabel(buffer[j].idx) para j em 0..filled-1
retorna fraudCount / 5f
```

**Por que distância²:** a raiz quadrada é monotônica, então comparar `dist²` dá exatamente o mesmo ranking que `dist`. O código atual já usa `sum` (sem sqrt) — comportamento preservado.

**Recalcular worstSlot varrendo 5 elementos** é O(5) constante; o total continua O(5n). Para k=5 isso é mais simples e rápido que manter um heap.

---

## Equivalência de comportamento (crítico)

O `.OrderBy(...).Take(5)` atual é um sort **estável**: entre distâncias iguais, o índice original menor vem primeiro. Para os testes existentes continuarem válidos e o resultado ser idêntico, a inserção usa **comparação estrita (`<`)**:

- Iteramos `i` em ordem crescente.
- Só substituímos o pior slot quando a nova distância é **estritamente menor**.
- Logo, em caso de empate, o índice **menor** (visto antes) permanece — igual ao sort estável.

Isso garante que `fraud_score` seja bit-a-bit idêntico ao da implementação atual para qualquer entrada.

---

## Testes (TDD — rede de regressão + equivalência)

Os 4 testes atuais de `tests/Api.Tests/KnnSearchTests.cs` são a rede de regressão e **devem continuar passando sem alteração**:
- `Search_AllLegit_ReturnsZero`
- `Search_AllFraud_ReturnsOne`
- `Search_ThreeOfFiveFraud_ReturnsPointSix`
- `Search_ApprovalThreshold_BelowPointSixApproved`

**Novo teste a adicionar:**
- `Search_TiedDistances_PicksLowestIndices` — dataset onde mais de 5 vetores empatam na mesma distância da query, com labels arranjados de modo que o resultado dependa de **quais** 5 são escolhidos. Verifica que os 5 de menor índice são selecionados (mesma semântica do `OrderBy` estável). Trava a equivalência contra regressões futuras.

Fluxo TDD: adiciona o teste de empate (vermelho ou já coberto), reescreve `Search` com a passada O(n), confirma os 5 testes verdes.

---

## Arquivos

| Arquivo | Ação |
|---------|------|
| `src/Api/Detection/KnnSearch.cs` | Reescreve o corpo de `Search` (assinatura intacta). Requer `unsafe`? Não — `stackalloc Span<T>` de struct não exige `unsafe`. `AllowUnsafeBlocks` já está ligado (M3), mas não é necessário aqui. |
| `tests/Api.Tests/KnnSearchTests.cs` | Adiciona `Search_TiedDistances_PicksLowestIndices`. |

Sem mudança em `Program.cs`, `ReferenceDataset.cs`, csproj ou infra.

---

## Verificação e medição

1. `dotnet test` — todos os testes verdes (5 em KnnSearch + os demais).
2. `docker compose up` na infra, esperar `/ready`.
3. Rodar o k6 oficial (`test/test.js`, 120s → 900 req/s) → `test/results.json`.
4. Registrar no Obsidian (`M4 - Top-K.md` novo + atualizar índice e o callout de baseline do M3): novo p99, score, taxa de falha, **comparado contra o baseline M3 = −6000 / p99 2002ms / 100% falha**.

**Expectativa:** a latência por request cai de ~1s para a ordem de poucos ms (a distância de 3M floats × 14 dims em uma passada, sem sort nem alloc). Não há garantia de zerar a fila a 900 req/s com 0.475 CPU, mas é o salto que tira o p99 do teto. O número real define se o próximo gargalo é CPU da distância (→ SIMD) ou alocação do vetor de query (→ stackalloc).

---

## Conceito para o Obsidian

Nova nota de conceito em `TEORIA/Performance e Sistemas/`: **Top-K (Selection) vs Full Sort** — por que para k pequeno e fixo a inserção limitada O(n·k) bate o sort O(n log n) e o heap O(n log k), e por que distância² evita o sqrt.
