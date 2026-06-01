# M4 — Top-K em uma passada O(n): Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminar, no hot-path do KNN, a alocação de `new float[3M]` (~12 MB/req) e a ordenação `.OrderBy()` de 3M itens (O(n log n)/req), substituindo por uma única passada O(n·k) que mantém os 5 vizinhos mais próximos num buffer `stackalloc` de 5 — sem alocação no heap e sem sort.

**Architecture:** `KnnSearch.Search` faz um `for` linear sobre os 3M vetores. Mantém os K=5 menores num par de `Span` em `stackalloc` (distância² + índice). Para cada vetor: calcula a distância²; se o buffer não está cheio, insere; senão, se a distância é estritamente menor que o pior dos 5, substitui o pior. O "pior" desempata pelo **maior índice original** — replicando exatamente a semântica do `OrderBy().Take(5)` estável atual (entre distâncias iguais, o índice menor é mantido). No fim, soma os labels dos ≤5 índices. Assinatura pública inalterada.

**Tech Stack:** C# .NET 9, `Span<T>` + `stackalloc` (safe, sem `unsafe`), xUnit.

---

## Estrutura de arquivos

```
rinha-2026/
├── src/Api/Detection/
│   └── KnnSearch.cs                  ← MODIFY (Task 1): reescreve o corpo de Search
└── tests/Api.Tests/
    └── KnnSearchTests.cs             ← MODIFY (Task 1): adiciona teste de empate
```

Nenhum outro arquivo muda (`Program.cs`, `ReferenceDataset.cs`, csproj e infra ficam intactos).

---

## Task 1: Top-K em uma passada O(n) (TDD)

**Files:**
- Modify: `src/Api/Detection/KnnSearch.cs`
- Modify: `tests/Api.Tests/KnnSearchTests.cs`

**Nota sobre TDD aqui:** esta é uma **refatoração que preserva comportamento** — o `fraud_score` de saída deve ser idêntico ao da implementação atual para qualquer entrada. Por isso o teste novo de empate é um **teste de caracterização**: ele passa tanto na implementação atual (`OrderBy`) quanto na nova (Top-K). Rodá-lo contra o código atual (Step 2) confirma que entendemos corretamente a semântica de desempate que precisamos replicar. A garantia comportamental é "os 5 testes verdes depois da troca". A melhora de performance em si é medida na Task 2 (benchmark).

- [ ] **Step 1: Adicionar o teste de empate em KnnSearchTests.cs**

Adicionar este `[Fact]` dentro da classe `KnnSearchTests` (depois do último teste existente, antes do fechamento da classe). Ele usa o helper `BuildDatasetFromRowsAsync` já existente no arquivo.

```csharp
    [Fact]
    public async Task Search_TiedDistances_PicksLowestIndices()
    {
        // 6 vetores idênticos → todos empatam na distância à query (zero).
        // OrderBy().Take(5) estável mantém os 5 de MENOR índice (0..4) e descarta o índice 5.
        // Só o índice 5 (descartado) é fraude → o score correto é 0.
        // Uma seleção Top-K que desempata errado manteria o índice 5 e daria 0.2.
        float[][] rows =
        [
            Enumerable.Repeat(0.1f, 14).ToArray(),
            Enumerable.Repeat(0.1f, 14).ToArray(),
            Enumerable.Repeat(0.1f, 14).ToArray(),
            Enumerable.Repeat(0.1f, 14).ToArray(),
            Enumerable.Repeat(0.1f, 14).ToArray(),
            Enumerable.Repeat(0.1f, 14).ToArray(),
        ];
        bool[] frauds = [false, false, false, false, false, true];
        var (ds, dir) = await BuildDatasetFromRowsAsync(rows, frauds);
        try
        {
            float score = KnnSearch.Search(new float[14], ds);
            Assert.Equal(0f, score);
        }
        finally
        {
            ds.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }
```

- [ ] **Step 2: Rodar os testes de KnnSearch — devem passar contra a implementação ATUAL**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "KnnSearch" -v minimal 2>&1 | tail -8
```

Esperado: todos passam (4 existentes + 1 novo). Isso confirma o alvo de equivalência: o `OrderBy` estável atual mantém os índices 0..4 no empate. Se o teste novo falhar aqui, a premissa de desempate está errada — pare e reavalie antes de trocar a implementação.

- [ ] **Step 3: Reescrever KnnSearch.cs com a passada O(n)**

Substituir o conteúdo completo de `src/Api/Detection/KnnSearch.cs`:

```csharp
using RinhaBackend.Data;

namespace RinhaBackend.Detection;

public static class KnnSearch
{
    private const int K = 5;

    // M4: Top-K em uma passada O(n·k). Mantém os K vizinhos mais próximos num
    // buffer stackalloc de K — sem `new float[count]` (~12 MB/req) e sem `.OrderBy()`
    // de 3M itens (O(n log n)). Para k=5 fixo, inserção limitada bate heap e sort.
    public static float Search(float[] query, ReferenceDataset ds)
    {
        int count = ds.Count;

        // Os K menores: distância² e índice original. stackalloc → zero heap.
        Span<float> bestDist = stackalloc float[K];
        Span<int> bestIdx = stackalloc int[K];
        int filled = 0;
        int worst = 0; // slot a descartar: maior distância (empate → maior índice)

        for (int i = 0; i < count; i++)
        {
            var vec = ds.GetVector(i);

            // distância² (sem sqrt — monotônica, mesmo ranking que a distância)
            float sum = 0f;
            for (int d = 0; d < 14; d++)
            {
                float diff = query[d] - vec[d];
                sum += diff * diff;
            }

            if (filled < K)
            {
                bestDist[filled] = sum;
                bestIdx[filled] = i;
                filled++;
                if (filled == K)
                    worst = FindWorst(bestDist, bestIdx);
            }
            // Comparação ESTRITA: empate não substitui → mantém o índice menor,
            // igual ao OrderBy().Take(5) estável.
            else if (sum < bestDist[worst])
            {
                bestDist[worst] = sum;
                bestIdx[worst] = i;
                worst = FindWorst(bestDist, bestIdx);
            }
        }

        int fraudCount = 0;
        for (int j = 0; j < filled; j++)
            if (ds.GetLabel(bestIdx[j]))
                fraudCount++;

        return fraudCount / (float)K;
    }

    // Slot a descartar entre os K: o de maior distância. Em empate de distância,
    // o de MAIOR índice original — replica o desempate estável do OrderBy (mantém
    // os índices menores quando vários vetores empatam na distância de corte).
    private static int FindWorst(ReadOnlySpan<float> dist, ReadOnlySpan<int> idx)
    {
        int worst = 0;
        for (int j = 1; j < dist.Length; j++)
        {
            if (dist[j] > dist[worst] ||
                (dist[j] == dist[worst] && idx[j] > idx[worst]))
                worst = j;
        }
        return worst;
    }
}
```

Notas para o implementador:
- `stackalloc float[K]` atribuído a `Span<float>` é **código seguro** (não requer `unsafe`). `K` é constante (5), então o tamanho é fixo e pequeno.
- A divisão por `K` (=5) preserva o `/ 5f` original — inclusive quando `count < 5` (`filled` vira `count`, mas dividimos por 5, igual ao `Take(5)` + `/5f` atual).
- `Span<int>` converte implicitamente para `ReadOnlySpan<int>` na chamada de `FindWorst`.

- [ ] **Step 4: Rodar TODOS os testes — devem passar**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj -v minimal 2>&1 | tail -6
```

Esperado: `Com falha: 0` — todos passam (os 4 testes antigos de KnnSearch + o novo de empate + os demais). Se o teste de empate falhar agora, o `FindWorst` não está desempatando pelo maior índice — revise.

- [ ] **Step 5: Commit**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Detection/KnnSearch.cs tests/Api.Tests/KnnSearchTests.cs
git commit -m "feat: M4 — Top-K em uma passada O(n), elimina OrderBy de 3M e alloc de 12MB/req

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Medir e documentar (benchmark oficial vs baseline M3)

**Files:** atualiza Obsidian (vault `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/`); sem código.

O harness k6 oficial já está em `/mnt/c/Users/Ivan/rinha-2026/test/` (`test.js`, `k6-summary.js`, `test-data.json`), baixado durante a medição do M3.

- [ ] **Step 1: Rebuild e subir os containers**

```bash
cd /mnt/c/Users/Ivan/rinha-2026/infra
docker compose build --progress plain 2>&1 | tail -3
docker compose up -d 2>&1 | tail -4
```

- [ ] **Step 2: Esperar /ready**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
START=$(date +%s)
until curl -sf http://localhost:9999/ready >/dev/null 2>&1; do
  sleep 2
  [ $(( $(date +%s) - START )) -gt 120 ] && { echo "TIMEOUT"; break; }
done
echo "/ready OK em $(( $(date +%s) - START ))s"
```

- [ ] **Step 3: Latência de uma request (sanity antes da carga)**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
PAYLOAD=$(python3 -c "import json; d=json.load(open('test/test-data.json')); print(json.dumps(d['entries'][0]['request']))")
for i in 1 2 3; do
  curl -s -o /dev/null -w "HTTP %{http_code}  total=%{time_total}s\n" \
    -X POST http://localhost:9999/fraud-score -H "Content-Type: application/json" -d "$PAYLOAD"
done
```

Esperado: queda grande vs o baseline M3 (~1.0s/req). Anotar o valor.

- [ ] **Step 4: Rodar o k6 oficial (120s → 900 req/s)**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
export K6_NO_USAGE_REPORT=true
k6 run test/test.js 2>&1 | tail -6
```

- [ ] **Step 5: Ler o resultado oficial**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
python3 -m json.tool test/results.json
```

Anotar: `p99`, `scoring.final_score`, `scoring.failure_rate`, e o breakdown (`tp/tn/fp/fn/http_errors`). Comparar contra o baseline **M3: score −6000, p99 2002ms, 100% falha**. Atenção: se agora houver respostas 200, `tp/tn/fp/fn` deixam de ser zero — é aí que o `references_checksum_sha256` do dataset passa a importar para o score de detecção.

- [ ] **Step 6: Parar os containers**

```bash
cd /mnt/c/Users/Ivan/rinha-2026/infra
docker compose down 2>&1 | tail -2
```

- [ ] **Step 7: Documentar no Obsidian**

Criar a nota de conceito `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/TEORIA/Performance e Sistemas/Top-K vs Full Sort.md`:

```markdown
# Top-K (Selection) vs Full Sort

Quando você só precisa dos **K menores** (ou maiores) de N itens, ordenar tudo é desperdício.

---

## O custo de ordenar tudo

`OrderBy(...).Take(5)` sobre N itens ordena os N — O(N log N) — e em LINQ ainda materializa um array intermediário. Para N = 3 milhões por request, isso domina a latência (medido: ~1s/req no KNN brute-force).

Mas o ranking completo é jogado fora: só os 5 primeiros importam.

---

## Top-K para k pequeno e fixo: inserção limitada

Com k = 5 fixo, mantenha um buffer de 5 e varra os N uma vez:
- buffer não cheio → insere
- senão, se o item é menor que o **pior** dos 5 → substitui o pior

Custo: O(N·k). Para k=5 → O(5N), uma constante pequena, **sem alocação** (cabe em `stackalloc`), cache-friendly.

| Abordagem | Custo | Alocação |
|-----------|-------|----------|
| `OrderBy().Take(5)` | O(N log N) | array de N |
| min-heap de tamanho k | O(N log k) | heap de k |
| inserção limitada (k pequeno) | O(N·k) | nenhuma (stackalloc) |

Para k=5, `log k ≈ 2.3` ≈ k, então o heap não compensa a complexidade extra — a inserção limitada ganha em simplicidade e cache.

---

## Distância²: evite o sqrt

A raiz quadrada é monotônica: `a < b ⟺ a² < b²`. Para **comparar/ranquear** distâncias euclidianas, compare a soma dos quadrados e nunca chame `sqrt`. Só calcule a raiz se precisar do valor absoluto da distância — no KNN não precisa.

---

## Desempate (estabilidade)

`OrderBy` é estável: em distâncias iguais, o índice menor vem primeiro. Para um Top-K dar o **mesmo conjunto** que `OrderBy().Take(k)`, ao descartar o "pior" desempate pelo **maior índice original** — assim os índices menores sobrevivem quando vários itens empatam na distância de corte.

---

→ Ver [[Stack vs Heap]] · [[Distancia Euclidiana]] · [[KNN - K Nearest Neighbors]] · [[SIMD]]
```

Criar a nota de marco `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/RINHA DE BACKEND/M4 - Top-K.md` com a estrutura dos outros marcos (em primeira pessoa): o problema herdado do M3 (−6000, gargalo = `.OrderBy()` de 3M + alloc de 12 MB), a solução (inserção limitada O(n·k) em stackalloc), o cuidado de equivalência (desempate por maior índice, distância²), e a **medição real** (preencher com os números do Step 5: latência de 1 request, p99, score, taxa de falha, breakdown — sempre comparando contra o baseline M3 −6000). Linkar `[[Top-K vs Full Sort]]`, `[[M3 - Memory Mapped File]]`, `[[KNN - K Nearest Neighbors]]`.

Atualizar `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/RINHA DE BACKEND/RINHA DE BACKEND.md`:
- na lista de marcos, adicionar a linha do `[[M4 - Top-K]]` com o resultado medido;
- no callout/menu de técnicas, marcar "Top-K sem buffer total" como ✓ feito.

Atualizar o callout de baseline no fim de `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/RINHA DE BACKEND/M3 - Memory Mapped File.md` para apontar o delta M3 → M4 (de −6000 para o novo score).

- [ ] **Step 8: Commit dos resultados (apenas docs do repo, se houver)**

As notas do Obsidian ficam fora do repo git (vault em `Documents/Estudo`), então não entram no commit. Se algum doc dentro de `rinha-2026/docs/` for atualizado com os números, commitar:

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add docs/
git commit -m "chore: registra resultado do benchmark M4 (Top-K) vs baseline M3" 2>&1 | tail -2 || echo "nada a commitar em docs/"
```

---

## Self-Review

**Spec coverage:**
- ✅ Elimina `new float[count]` (~12 MB/req) — não há mais array de distâncias (Task 1, Step 3).
- ✅ Elimina `.OrderBy()` de 3M — substituído por passada O(n·k) (Task 1, Step 3).
- ✅ Buffer Top-K em `stackalloc`, zero heap (Task 1, Step 3).
- ✅ Equivalência com `OrderBy().Take(5)` estável — comparação estrita + desempate por maior índice (Task 1, Step 3) + teste de empate (Step 1).
- ✅ Assinatura `Search(float[] query, ReferenceDataset ds)` inalterada.
- ✅ Não toca normalização/query (stackalloc float[14] fica M5), SIMD, nem HNSW — escopo isolado.
- ✅ 4 testes de regressão preservados; +1 teste de empate.
- ✅ Medição com k6 oficial vs baseline M3, documentada no Obsidian (Task 2).

**Placeholders:** nenhum no código (Task 1 tem o arquivo completo). A nota de marco M4 (Task 2, Step 7) é preenchida com números que só existem após rodar o benchmark — isso é inerente a uma task de medição, não um placeholder de implementação.

**Type/consistência:**
- `K` (const int = 5) — usado em `Search` e implícito em `FindWorst` via `dist.Length` ✓
- `FindWorst(ReadOnlySpan<float> dist, ReadOnlySpan<int> idx)` → `int` — definido e chamado 2× em `Search` com `Span<float>`/`Span<int>` (conversão implícita) ✓
- `Search(float[] query, ReferenceDataset ds)` → `float` — assinatura idêntica à atual; `Program.cs` não muda ✓
- `BuildDatasetFromRowsAsync(float[][], bool[])` — helper já existente em KnnSearchTests.cs, reutilizado pelo teste novo ✓
