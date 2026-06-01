# M5 — stackalloc (query) + SIMD (distância): Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Atacar o gargalo de CPU do M4 (loop de distância sobre 3M × 14 floats/req) com duas otimizações de hot-path: alocar o vetor de query na stack (`stackalloc`, eliminando `new float[14]`/req) e calcular a distância² com SIMD (`Vector256<float>`, 8 floats por instrução, com fallback escalar).

**Architecture:** Task 1 é plumbing que preserva comportamento: `Normalizer.Normalize` passa a escrever num `Span<float>` fornecido pelo chamador; o handler `/fraud-score` (async) delega para um `ComputeScore` síncrono onde o `stackalloc` é legal; `KnnSearch.Search` passa a receber `ReadOnlySpan<float>`. Task 2 troca só o cálculo da distância individual por um helper SIMD com sobra escalar (dims 8-13) e fallback, mantendo o Top-K do M4 intacto. Task 3 mede com o k6 oficial e documenta vs M4.

**Tech Stack:** C# .NET 9, `System.Runtime.Intrinsics` (`Vector256<float>`), `Span<T>`/`stackalloc`, xUnit.

---

## Estrutura de arquivos

```
rinha-2026/
├── src/Api/
│   ├── Program.cs                       ← MODIFY (Task 1): extrai ComputeScore síncrono + stackalloc
│   └── Detection/
│       ├── Normalizer.cs                ← MODIFY (Task 1): Normalize escreve em Span<float>
│       └── KnnSearch.cs                 ← MODIFY (Task 1: assinatura; Task 2: SIMD)
└── tests/Api.Tests/
    ├── NormalizerTests.cs               ← MODIFY (Task 1): só o corpo do helper privado
    └── KnnSearchTests.cs                ← MODIFY (Task 2): teste-guarda da sobra (dims 8-13)
```

`EndpointTests.cs`, `DatasetTests.cs`, csproj e infra **não mudam**.

---

## Task 1: stackalloc do vetor de query (refatoração que preserva comportamento)

**Files:**
- Modify: `src/Api/Detection/Normalizer.cs`
- Modify: `tests/Api.Tests/NormalizerTests.cs`
- Modify: `src/Api/Detection/KnnSearch.cs`
- Modify: `src/Api/Program.cs`

**Nota TDD:** esta task é uma refatoração — o resultado de saída é idêntico (mesma matemática escalar). Não há "teste que falha primeiro"; as mudanças de assinatura são interdependentes e dirigidas pelo compilador. A rede de regressão são os testes existentes (Normalizer, KnnSearch, Endpoint), que devem continuar verdes. A melhora (alocação eliminada) é qualitativa aqui; o número vem na Task 3.

- [ ] **Step 1: Reescrever `src/Api/Detection/Normalizer.cs` para escrever num `Span<float>`**

Conteúdo completo:

```csharp
namespace RinhaBackend.Detection;

public static class Normalizer
{
    private static float Clamp(float x) => MathF.Max(0f, MathF.Min(1f, x));

    // M5: escreve no buffer fornecido pelo chamador (stackalloc) — sem `new float[14]` por request.
    public static void Normalize(
        TransactionRequest req,
        Dictionary<string, float> mccRisk,
        HashSet<string> knownMerchants,
        Span<float> dest)
    {
        dest[0] = Clamp((float)req.Transaction.Amount / 10_000f);

        dest[1] = Clamp(req.Transaction.Installments / 12f);

        dest[2] = req.Customer.AvgAmount == 0m
            ? 0f
            : Clamp((float)(req.Transaction.Amount / req.Customer.AvgAmount) / 10f);

        dest[3] = req.Transaction.RequestedAt.Hour / 23f;

        int dotNetDow = (int)req.Transaction.RequestedAt.DayOfWeek;
        int mondayBasedDow = (dotNetDow + 6) % 7;
        dest[4] = mondayBasedDow / 6f;

        if (req.LastTransaction is null)
        {
            dest[5] = -1f;
            dest[6] = -1f;
        }
        else
        {
            float minutes = (float)(req.Transaction.RequestedAt - req.LastTransaction.Timestamp).TotalMinutes;
            dest[5] = Clamp(minutes / 1_440f);
            dest[6] = Clamp(req.LastTransaction.KmFromCurrent / 1_000f);
        }

        dest[7] = Clamp(req.Terminal.KmFromHome / 1_000f);

        dest[8] = Clamp(req.Customer.TxCount24h / 20f);

        dest[9] = req.Terminal.IsOnline ? 1f : 0f;

        dest[10] = req.Terminal.CardPresent ? 1f : 0f;

        dest[11] = knownMerchants.Contains(req.Merchant.Id) ? 0f : 1f;

        dest[12] = mccRisk.TryGetValue(req.Merchant.Mcc, out float risk) ? risk : 0.5f;

        dest[13] = Clamp((float)req.Merchant.AvgAmount / 10_000f);
    }
}
```

- [ ] **Step 2: Ajustar o helper privado em `tests/Api.Tests/NormalizerTests.cs`**

Os ~30 testes chamam um helper `Normalize(...)` que retorna `float[]`. Trocar APENAS esse helper (linhas 32-35) para alocar um buffer e chamar a nova assinatura. Substituir:

```csharp
    private static float[] Normalize(TransactionRequest req,
        Dictionary<string, float>? mcc = null,
        HashSet<string>? known = null)
        => Normalizer.Normalize(req, mcc ?? [], known ?? new HashSet<string>(req.Customer.KnownMerchants));
```

por:

```csharp
    private static float[] Normalize(TransactionRequest req,
        Dictionary<string, float>? mcc = null,
        HashSet<string>? known = null)
    {
        var vec = new float[14];
        Normalizer.Normalize(req, mcc ?? [], known ?? new HashSet<string>(req.Customer.KnownMerchants), vec);
        return vec;
    }
```

Nenhum dos `[Fact]` muda — todos continuam fazendo `Normalize(...)[d]`.

- [ ] **Step 3: Mudar a assinatura de `Search` em `src/Api/Detection/KnnSearch.cs`**

Trocar SOMENTE a linha da assinatura (o corpo funciona igual — `query[d]` já opera sobre `ReadOnlySpan<float>`). De:

```csharp
    public static float Search(float[] query, ReferenceDataset ds)
```

para:

```csharp
    public static float Search(ReadOnlySpan<float> query, ReferenceDataset ds)
```

(Os testes que chamam `KnnSearch.Search(new float[14], ds)` continuam compilando: `float[]` converte implicitamente para `ReadOnlySpan<float>`.)

- [ ] **Step 4: Reescrever `src/Api/Program.cs` extraindo `ComputeScore` síncrono**

Conteúdo completo:

```csharp
using RinhaBackend;
using RinhaBackend.Data;
using RinhaBackend.Detection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ReferenceDataset>();

var app = builder.Build();

var dataset = app.Services.GetRequiredService<ReferenceDataset>();
var dataPath = builder.Configuration["DataPath"]
               ?? Environment.GetEnvironmentVariable("DATA_PATH")
               ?? "/app/data";

_ = Task.Run(async () =>
{
    try { await dataset.LoadAsync(dataPath); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[ERRO] Falha ao carregar dataset: {ex.Message}");
    }
});

app.MapGet("/ready", (ReferenceDataset ds) =>
    ds.IsReady ? Results.Ok("ready") : Results.StatusCode(503));

app.MapPost("/fraud-score", async (HttpRequest request, ReferenceDataset ds) =>
{
    TransactionRequest? req;
    try
    {
        req = await request.ReadFromJsonAsync<TransactionRequest>();
        if (req is null) return Results.BadRequest("body vazio");
    }
    catch (Exception)
    {
        return Results.BadRequest("JSON inválido");
    }

    if (!ds.IsReady)
        return Results.StatusCode(503);

    // M5: stackalloc não pode existir num método async (Span não sobrevive ao await),
    // então o trabalho com o vetor na stack acontece no método síncrono ComputeScore.
    return ComputeScore(req, ds);
});

app.Run();

// M5: síncrono de propósito — aqui o vetor de query vive na stack (stackalloc).
static IResult ComputeScore(TransactionRequest req, ReferenceDataset ds)
{
    var knownMerchants = new HashSet<string>(
        req.Customer.KnownMerchants,
        StringComparer.OrdinalIgnoreCase
    );

    Span<float> vector = stackalloc float[14];
    Normalizer.Normalize(req, ds.MccRisk, knownMerchants, vector);
    float fraudScore = KnnSearch.Search(vector, ds);

    return Results.Ok(new FraudScoreResponse
    {
        Approved = fraudScore < 0.6f,
        FraudScore = fraudScore
    });
}

public partial class Program { }
```

Nota: `ComputeScore` é uma *local function* estática de topo. Em top-level programs ela pode ser declarada após `app.Run();` e antes da declaração de tipo `public partial class Program {}`; é visível para o lambda do `MapPost` (local functions são hoisted).

- [ ] **Step 5: Rodar TODOS os testes — devem passar**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj -v minimal 2>&1 | tail -6
```

Esperado: `Com falha: 0` (todos passam — Normalizer, KnnSearch, Endpoint, Dataset). Se houver erro de compilação sobre `stackalloc`/`Span` em método async, o trabalho não foi movido para `ComputeScore` — revise o Step 4.

- [ ] **Step 6: Commit**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Detection/Normalizer.cs tests/Api.Tests/NormalizerTests.cs src/Api/Detection/KnnSearch.cs src/Api/Program.cs
git commit -m "feat: M5 (1/2) — stackalloc do vetor de query, normalize escreve em Span

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: SIMD no cálculo de distância (TDD)

**Files:**
- Modify: `tests/Api.Tests/KnnSearchTests.cs`
- Modify: `src/Api/Detection/KnnSearch.cs`

O Top-K do M4 fica intacto; só o cálculo da distância² individual sai do loop inline e vira um helper SIMD com sobra escalar (dims 8-13) e fallback.

**Risco específico que o teste-guarda cobre:** o `Vector256<float>` cobre só as **dims 0-7** (8 floats); as dims **8-13** precisam de uma "sobra" escalar. Os testes atuais usam vetores onde TODAS as 14 dims escalam juntas, então um bug que esquecesse a sobra **não mudaria o ranking** e passaria despercebido. O novo teste cria um caso onde só contar as 14 dims dá o resultado certo.

- [ ] **Step 1: Adicionar o teste-guarda da sobra em `tests/Api.Tests/KnnSearchTests.cs`**

Adicionar este `[Fact]` dentro da classe (após o último teste existente):

```csharp
    [Fact]
    public async Task Search_DistanceCoversAllFourteenDims()
    {
        // Guarda contra esquecer a "sobra" (dims 8-13) no cálculo SIMD.
        // 5 legítimos próximos em TODAS as dims (0.1 em todas) → dist² = 14·0.01 = 0.14.
        // 1 fraude próximo só nas dims 0-7 (=0), longe nas 8-13 (=0.5) → dist² = 6·0.25 = 1.5.
        // Query = zeros. Contando as 14 dims, os 5 legítimos são os mais próximos → score 0.
        // Se ignorasse as dims 8-13, o fraude (dist 0 nas dims 0-7) entraria no top-5 → score 0.2.
        float[] legit = Enumerable.Repeat(0.1f, 14).ToArray();
        float[] fraudNearOnlyInHead = new float[14];
        for (int d = 8; d < 14; d++) fraudNearOnlyInHead[d] = 0.5f;

        float[][] rows = [legit, legit, legit, legit, legit, fraudNearOnlyInHead];
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

- [ ] **Step 2: Rodar os testes de KnnSearch — devem passar contra a implementação ATUAL (escalar)**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "KnnSearch" -v minimal 2>&1 | tail -8
```

Esperado: todos passam (o cálculo escalar atual já conta as 14 dims). Isso confirma que o teste-guarda está correto e fica de sentinela quando o SIMD entrar.

- [ ] **Step 3: Reescrever `src/Api/Detection/KnnSearch.cs` com o helper SIMD**

Conteúdo completo:

```csharp
using System.Runtime.Intrinsics;
using RinhaBackend.Data;

namespace RinhaBackend.Detection;

public static class KnnSearch
{
    private const int K = 5;

    // M4: Top-K em uma passada O(n·k). Mantém os K vizinhos mais próximos num
    // buffer stackalloc de K — sem `new float[count]` (~12 MB/req) e sem `.OrderBy()`
    // de 3M itens (O(n log n)). Para k=5 fixo, a inserção limitada supera tanto um heap quanto o sort completo.
    // M5: a distância² individual é calculada com SIMD (ver SquaredDistance).
    public static float Search(ReadOnlySpan<float> query, ReferenceDataset ds)
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
            float sum = SquaredDistance(query, vec);

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

    // M5: distância² (sem sqrt). SIMD cobre as dims 0-7 (8 floats por instrução);
    // as dims 8-13 ("sobra") são escalares. Fallback escalar puro quando não há AVX.
    // Nota: a soma SIMD pode diferir do escalar em 1 ULP (soma de float não é associativa) —
    // mesma distância matemática, ranking dos 5 vizinhos inalterado.
    private static float SquaredDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            // dims 0-7 (Create lê os 8 primeiros; a e b têm 14 ≥ 8)
            var va = Vector256.Create(a);
            var vb = Vector256.Create(b);
            var diff = va - vb;
            float sum = Vector256.Sum(diff * diff);

            // sobra: dims 8-13 escalar
            for (int d = 8; d < 14; d++)
            {
                float df = a[d] - b[d];
                sum += df * df;
            }
            return sum;
        }

        // fallback escalar (CPU sem AVX)
        float s = 0f;
        for (int d = 0; d < 14; d++)
        {
            float df = a[d] - b[d];
            s += df * df;
        }
        return s;
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

- [ ] **Step 4: Rodar TODOS os testes — devem passar**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj -v minimal 2>&1 | tail -6
```

Esperado: `Com falha: 0`. Se `Search_DistanceCoversAllFourteenDims` falhar, a sobra (dims 8-13) não está sendo somada — revise o `for (int d = 8; ...)` em `SquaredDistance`.

- [ ] **Step 5: Commit**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Detection/KnnSearch.cs tests/Api.Tests/KnnSearchTests.cs
git commit -m "feat: M5 (2/2) — distância² com SIMD Vector256 + sobra escalar e fallback

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Medir e documentar (benchmark oficial vs M4)

**Files:** atualiza Obsidian (vault `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/`); sem código.

O harness k6 está em `/mnt/c/Users/Ivan/rinha-2026/test/`.

- [ ] **Step 1: Rebuild e subir**

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

- [ ] **Step 3: Latência de uma request (vs ~20ms do M4)**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
PAYLOAD=$(python3 -c "import json; d=json.load(open('test/test-data.json')); print(json.dumps(d['entries'][0]['request']))")
for i in 1 2 3 4 5; do
  curl -s -o /dev/null -w "HTTP %{http_code}  total=%{time_total}s\n" \
    -X POST http://localhost:9999/fraud-score -H "Content-Type: application/json" -d "$PAYLOAD"
done
```

Anotar a latência quente.

- [ ] **Step 4: k6 oficial (120s → 900 req/s)**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
export K6_NO_USAGE_REPORT=true
k6 run test/test.js 2>&1 | tail -6
```

- [ ] **Step 5: Ler o resultado**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
python3 -m json.tool test/results.json
```

Anotar `p99`, `scoring.final_score`, `scoring.failure_rate`, breakdown (`tp/tn/fp/fn/http_errors`). Comparar com **M4: p99 2002ms, score −6000, failure_rate 98.3%, 238 respostas corretas (tp=113, tn=125, fp/fn=0)**.

- [ ] **Step 6: Parar**

```bash
cd /mnt/c/Users/Ivan/rinha-2026/infra
docker compose down 2>&1 | tail -2
```

- [ ] **Step 7: Documentar no Obsidian**

Atualizar/criar a nota de conceito `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/TEORIA/Performance e Sistemas/SIMD.md` (se já existir, complementar; se não, criar) cobrindo, de forma simples: "uma instrução, vários dados"; `Vector256<float>` = 8 floats; a soma horizontal (`Vector256.Sum`); por que para vetores curtos (14 dims) o ganho é parcial (sobra de 6 dims + custo da soma horizontal); a não-associatividade da soma float (resultado muda no último ULP); e a importância do fallback `Vector256.IsHardwareAccelerated`. Linkar `[[Stack vs Heap]]`, `[[Distancia Euclidiana]]`, `[[Top-K vs Full Sort]]`.

Criar a nota de marco `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/RINHA DE BACKEND/M5 - stackalloc e SIMD.md` com a estrutura dos outros marcos (primeira pessoa): o gargalo herdado do M4 (CPU no loop de distância), as duas otimizações (stackalloc do query + a restrição do async; SIMD com sobra e fallback), os cuidados (teste-guarda da sobra; ULP da soma float), e a **medição real** (latência de 1 request, p99, score, failure_rate, breakdown) sempre comparando contra o M4. Linkar `[[SIMD]]`, `[[Stack vs Heap]]`, `[[M4 - Top-K]]`, e — conforme o número — apontar se o próximo passo é HNSW.

Atualizar `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/RINHA DE BACKEND/RINHA DE BACKEND.md`:
- adicionar a linha do `[[M5 - stackalloc e SIMD]]` com o resultado medido;
- no menu de técnicas, marcar "SIMD `Vector256<float>`" e "stackalloc float[14]" como ✓ feitos.

- [ ] **Step 8: Commit (apenas se algo em `rinha-2026/docs/` mudar)**

As notas do Obsidian ficam fora do repo git. Se nenhum doc do repo mudar, não há commit nesta task.

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add docs/ && git commit -m "chore: registra resultado do benchmark M5 (stackalloc + SIMD) vs M4" 2>&1 | tail -2 || echo "nada a commitar em docs/"
```

---

## Self-Review

**Spec coverage:**
- ✅ stackalloc do vetor de query — `Normalize` escreve em `Span<float>` (Task 1 Step 1) + `ComputeScore` síncrono com `stackalloc` (Task 1 Step 4).
- ✅ Restrição do async resolvida — handler delega para método síncrono (Task 1 Step 4).
- ✅ `Search` recebe `ReadOnlySpan<float>` (Task 1 Step 3).
- ✅ SIMD na distância com `Vector256<float>` + sobra escalar dims 8-13 + fallback `IsHardwareAccelerated` (Task 2 Step 3).
- ✅ Top-K do M4 intacto (mesmo corpo, só o cálculo de distância vira helper).
- ✅ Caveat do ULP documentado no código e no marco.
- ✅ Testes: helper do Normalizer ajustado (Task 1 Step 2); teste-guarda da sobra (Task 2 Step 1); KnnSearchTests/EndpointTests/DatasetTests intactos.
- ✅ Medição k6 vs M4 documentada no Obsidian (Task 3).

**Placeholders:** nenhum no código — Tasks 1 e 2 têm os arquivos completos. A nota de marco (Task 3 Step 7) é preenchida com números que só existem após o benchmark (inerente à task de medição).

**Type/consistência:**
- `Normalize(TransactionRequest, Dictionary<string,float>, HashSet<string>, Span<float>)` → `void` — definida na Task 1 Step 1; chamada no helper de teste (Step 2) e em `ComputeScore` (Step 4) com os 4 args ✓
- `Search(ReadOnlySpan<float>, ReferenceDataset)` → `float` — assinatura na Task 1 Step 3; corpo final na Task 2 Step 3; chamada em `ComputeScore` (passa `Span<float>`, converte para `ReadOnlySpan`) e nos testes (`new float[14]` converte) ✓
- `ComputeScore(TransactionRequest, ReferenceDataset)` → `IResult` — definida e chamada no Program.cs (Task 1 Step 4) ✓
- `SquaredDistance(ReadOnlySpan<float>, ReadOnlySpan<float>)` → `float` — definida e chamada em `Search` (Task 2 Step 3); recebe `query` (ReadOnlySpan) e `vec` (ReadOnlySpan de `GetVector`) ✓
- `FindWorst(ReadOnlySpan<float>, ReadOnlySpan<int>)` → `int` — inalterada do M4 ✓
- `Vector256.Create(ReadOnlySpan<float>)`, `Vector256.Sum`, `Vector256.IsHardwareAccelerated` — APIs de `System.Runtime.Intrinsics` (using adicionado na Task 2 Step 3) ✓
