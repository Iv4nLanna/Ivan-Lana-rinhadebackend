# M6 — Índice espacial exato (KD-Tree) + quantização int8: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Substituir a busca brute-force (varre 3M vetores float) por uma busca **exata** numa **KD-Tree** sobre vetores **quantizados em int8**, lendo ~milhares de vetores de 14 bytes por request em vez de 3M de 56 bytes — atacando o gargalo memory-bound provado no M5.

**Architecture:** Quantização afim uniforme `[-1,1]→[0,255]` (preserva o ranking euclidiano até uma constante). KD-Tree **implícita** (sem ponteiros de nó): os vetores são reordenados em ordem de mediana recursiva; a estrutura é o range `[lo,hi)` + `axis = depth % 14`. Busca com poda exata, mantendo os 5 mais próximos num buffer stackalloc com desempate **lexicográfico (dist, pos)** — o que torna o resultado independente da ordem de visita e igual ao brute-force sobre os mesmos dados quantizados. Build offline gera `index.bin`; runtime faz mmap (truque do M3).

**Tech Stack:** C# .NET 9, `System.IO.MemoryMappedFiles`, `Span<T>`/`stackalloc`, xUnit. Sem libs externas.

---

## Estrutura de arquivos

```
rinha-2026/
├── src/Api/
│   ├── Detection/
│   │   ├── Quantizer.cs          ← CREATE (Task 1): float→byte
│   │   ├── KdTree.cs             ← CREATE (Task 2): build + busca exata sobre bytes
│   │   ├── Normalizer.cs         ← (inalterado)
│   │   └── KnnSearch.cs          ← DELETE (Task 4): substituído por KdTree
│   ├── Data/
│   │   ├── IndexBuilder.cs       ← CREATE (Task 3): quantiza+constrói+(de)serializa index.bin
│   │   └── ReferenceDataset.cs   ← REWRITE (Task 4): carrega index.bin (mmap) / fallback JSON
│   └── Program.cs                ← MODIFY (Task 4): ComputeScore quantiza o query e chama KdTree
├── src/IndexGen/                 ← CREATE (Task 3): console que gera data/index.bin offline
│   ├── IndexGen.csproj
│   └── Program.cs
├── tests/Api.Tests/
│   ├── QuantizerTests.cs         ← CREATE (Task 1)
│   ├── KdTreeTests.cs            ← CREATE (Task 2): exatidão vs brute-force + comportamento
│   ├── IndexBuilderTests.cs      ← CREATE (Task 3): round-trip index.bin
│   ├── KnnSearchTests.cs         ← DELETE (Task 4): comportamento migrado p/ KdTreeTests
│   ├── DatasetTests.cs           ← MODIFY (Task 4): usa index.bin / fallback
│   └── EndpointTests.cs          ← (inalterado; usa fallback JSON)
├── infra/Dockerfile              ← MODIFY (Task 4): roda IndexGen no build → data/index.bin
└── data/index.bin                ← gerado (Task 4/5), git-ignored
```

**Convenções compartilhadas (todas as tasks):** `Dims = 14`, `K = 5`. Vetor quantizado = 14 bytes contíguos. `index.bin`: `[int32 count][count×14 bytes vetores reordenados][count×1 byte labels reordenados]`.

---

## Task 1: Quantizer (TDD)

**Files:**
- Create: `src/Api/Detection/Quantizer.cs`
- Create: `tests/Api.Tests/QuantizerTests.cs`

Quantização afim uniforme: `[-1,1] → [0,255]`. A mesma escala para todas as dims preserva o ranking euclidiano (a distância² quantizada é a real × constante).

- [ ] **Step 1: Escrever os testes em `tests/Api.Tests/QuantizerTests.cs`**

```csharp
using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class QuantizerTests
{
    [Theory]
    [InlineData(-1f, 0)]
    [InlineData(0f, 128)]
    [InlineData(1f, 255)]
    [InlineData(0.5f, 191)]   // (0.5+1)/2*255 = 191.25 → 191
    public void Quantize_MapsRangeToByte(float input, int expected)
    {
        Assert.Equal((byte)expected, Quantizer.Quantize(input));
    }

    [Theory]
    [InlineData(-5f, 0)]   // clamp abaixo de -1
    [InlineData(5f, 255)]  // clamp acima de 1
    public void Quantize_ClampsOutOfRange(float input, int expected)
    {
        Assert.Equal((byte)expected, Quantizer.Quantize(input));
    }

    [Fact]
    public void QuantizeInto_FillsAllDims()
    {
        float[] src = [-1f, 0f, 1f, 0.5f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f];
        Span<byte> dest = stackalloc byte[14];
        Quantizer.Quantize(src, dest);
        Assert.Equal((byte)0, dest[0]);
        Assert.Equal((byte)128, dest[1]);
        Assert.Equal((byte)255, dest[2]);
        Assert.Equal((byte)191, dest[3]);
        Assert.Equal((byte)128, dest[13]);
    }
}
```

- [ ] **Step 2: Rodar — deve falhar (Quantizer não existe)**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "Quantizer" -v minimal 2>&1 | tail -8
```
Esperado: erro de compilação — `Quantizer` não existe.

- [ ] **Step 3: Criar `src/Api/Detection/Quantizer.cs`**

```csharp
namespace RinhaBackend.Detection;

// M6: quantização afim uniforme [-1,1] → [0,255] (int8).
// Mesma escala em todas as dims ⇒ Σ(q(a)-q(b))² = (255/2)² · Σ(a-b)² ⇒ ranking euclidiano preservado.
// As dims 5 e 6 podem ser -1 (sentinela); -1 mapeia para 0 naturalmente.
public static class Quantizer
{
    public const int Dims = 14;

    public static byte Quantize(float x)
    {
        float c = MathF.Max(-1f, MathF.Min(1f, x));
        return (byte)MathF.Round((c + 1f) / 2f * 255f);
    }

    public static void Quantize(ReadOnlySpan<float> src, Span<byte> dest)
    {
        for (int d = 0; d < Dims; d++)
            dest[d] = Quantize(src[d]);
    }
}
```

- [ ] **Step 4: Rodar — deve passar**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "Quantizer" -v minimal 2>&1 | tail -5
```
Esperado: todos passam.

- [ ] **Step 5: Commit**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Detection/Quantizer.cs tests/Api.Tests/QuantizerTests.cs
git commit -m "feat: M6 — Quantizer int8 (escala afim uniforme [-1,1]→[0,255])

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: KD-Tree — build + busca exata (TDD)

**Files:**
- Create: `src/Api/Detection/KdTree.cs`
- Create: `tests/Api.Tests/KdTreeTests.cs`

Esta é a peça central. A KD-Tree é **implícita**: `Build` reordena os vetores+labels em ordem de mediana recursiva; a estrutura é o range `[lo,hi)` com `axis = depth % 14` e nó = `mid = (lo+hi)/2`. A busca poda e mantém os 5 mais próximos com desempate **lexicográfico (dist, pos)** — tornando o resultado independente da ordem de visita e igual ao brute-force sobre os mesmos bytes.

**Exatidão (o ponto sutil):** com desempate lexicográfico `(dist, pos)` e poda `diff² ≤ pior`, o conjunto dos 5 retornados é exatamente "os 5 de menor `(dist, pos)`" — independente da ordem de processamento. O teste central compara a árvore contra um brute-force que usa a MESMA regra de seleção; se a poda descartar indevidamente um candidato, os conjuntos divergem.

- [ ] **Step 1: Escrever `tests/Api.Tests/KdTreeTests.cs`**

```csharp
using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class KdTreeTests
{
    // brute-force de referência: os 5 de menor (dist, pos) sobre os MESMOS bytes.
    private static int[] BruteForceTop5(byte[] vec, int count, byte[] query)
    {
        var all = new (int dist, int pos)[count];
        for (int i = 0; i < count; i++)
        {
            int sum = 0;
            for (int d = 0; d < 14; d++) { int df = query[d] - vec[i * 14 + d]; sum += df * df; }
            all[i] = (sum, i);
        }
        Array.Sort(all, (a, b) => a.dist != b.dist ? a.dist.CompareTo(b.dist) : a.pos.CompareTo(b.pos));
        int k = Math.Min(5, count);
        var res = new int[k];
        for (int i = 0; i < k; i++) res[i] = all[i].pos;
        Array.Sort(res);
        return res;
    }

    [Fact]
    public void Search_MatchesBruteForce_OnManyRandomDatasets()
    {
        var rng = new Random(42);
        for (int trial = 0; trial < 200; trial++)
        {
            int count = rng.Next(1, 120);
            var vec = new byte[count * 14];
            var lab = new byte[count];
            rng.NextBytes(vec);
            for (int i = 0; i < count; i++) lab[i] = (byte)rng.Next(0, 2);

            // valores pequenos forçam empates (testa o desempate)
            if (trial % 2 == 0)
                for (int i = 0; i < vec.Length; i++) vec[i] = (byte)rng.Next(0, 4);

            var (rvec, rlab) = KdTree.Build(vec, lab, count);

            for (int q = 0; q < 10; q++)
            {
                var query = new byte[14];
                rng.NextBytes(query);
                if (trial % 2 == 0)
                    for (int d = 0; d < 14; d++) query[d] = (byte)rng.Next(0, 4);

                int[] treeTop = KdTree.SearchTopK(rvec, count, query);
                int[] bf = BruteForceTop5(rvec, count, query);
                Assert.Equal(bf, treeTop);
            }
        }
    }

    // Helper: constrói dataset a partir de linhas float, quantiza, constrói a árvore,
    // e retorna o fraud_score de uma query (também quantizada).
    private static float Score(float[][] rows, bool[] frauds, float[] query)
    {
        int count = rows.Length;
        var vec = new byte[count * 14];
        var lab = new byte[count];
        for (int i = 0; i < count; i++)
        {
            for (int d = 0; d < 14; d++) vec[i * 14 + d] = Quantizer.Quantize(rows[i][d]);
            lab[i] = frauds[i] ? (byte)1 : (byte)0;
        }
        var (rvec, rlab) = KdTree.Build(vec, lab, count);
        var q = new byte[14];
        Quantizer.Quantize(query, q);
        return KdTree.Search(rvec, rlab, count, q);
    }

    private static float[][] Rows(params float[] vals)
        => vals.Select(v => Enumerable.Repeat(v, 14).ToArray()).ToArray();

    [Fact]
    public void Search_AllLegit_ReturnsZero()
        => Assert.Equal(0f, Score(Rows(0.1f, 0.2f, 0.3f, 0.4f, 0.5f),
                                  [false, false, false, false, false], new float[14]));

    [Fact]
    public void Search_AllFraud_ReturnsOne()
        => Assert.Equal(1f, Score(Rows(0.1f, 0.2f, 0.3f, 0.4f, 0.5f),
                                  [true, true, true, true, true], new float[14]));

    [Fact]
    public void Search_ThreeOfFiveFraud_ReturnsPointSix()
    {
        float[][] rows = Rows(0.01f, 0.02f, 0.03f, 0.5f, 0.6f, 0.7f, 0.8f);
        bool[] frauds = [true, true, true, false, false, true, false];
        Assert.Equal(0.6f, Score(rows, frauds, new float[14]), 4);
    }

    [Fact]
    public void Search_CoversAllFourteenDims()
    {
        // 5 legítimos próximos em TODAS as dims; 1 fraude próximo só nas dims 0-7.
        float[] legit = Enumerable.Repeat(0.1f, 14).ToArray();
        float[] fraud = new float[14];
        for (int d = 8; d < 14; d++) fraud[d] = 0.5f;
        float[][] rows = [legit, legit, legit, legit, legit, fraud];
        bool[] frauds = [false, false, false, false, false, true];
        Assert.Equal(0f, Score(rows, frauds, new float[14]));
    }

    [Fact]
    public void Search_FewerThanFive_DividesByFive()
    {
        float[][] rows = Rows(0.1f, 0.2f, 0.3f);
        bool[] frauds = [true, true, false];
        Assert.Equal(0.4f, Score(rows, frauds, new float[14]), 4); // 2 fraudes / 5
    }
}
```

- [ ] **Step 2: Rodar — deve falhar (KdTree não existe)**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "KdTree" -v minimal 2>&1 | tail -8
```
Esperado: erro de compilação — `KdTree` não existe.

- [ ] **Step 3: Criar `src/Api/Detection/KdTree.cs`**

```csharp
namespace RinhaBackend.Detection;

// M6: KD-Tree implícita sobre vetores quantizados (byte). A estrutura é o range [lo,hi)
// com axis = depth % Dims e o nó na mediana (mid). Build reordena os vetores+labels;
// a busca poda e mantém os K mais próximos com desempate lexicográfico (dist, pos),
// o que torna o resultado independente da ordem de visita = igual ao brute-force.
public static class KdTree
{
    public const int Dims = 14;
    public const int K = 5;

    // Reordena (vectors, labels) em ordem de mediana recursiva. Retorna cópias reordenadas.
    public static (byte[] vectors, byte[] labels) Build(
        ReadOnlySpan<byte> vectors, ReadOnlySpan<byte> labels, int count)
    {
        var perm = new int[count];
        for (int i = 0; i < count; i++) perm[i] = i;
        BuildRange(vectors, perm, 0, count, 0);

        var outV = new byte[count * Dims];
        var outL = new byte[count];
        for (int i = 0; i < count; i++)
        {
            vectors.Slice(perm[i] * Dims, Dims).CopyTo(outV.AsSpan(i * Dims, Dims));
            outL[i] = labels[perm[i]];
        }
        return (outV, outL);
    }

    private static void BuildRange(ReadOnlySpan<byte> vectors, int[] perm, int lo, int hi, int depth)
    {
        if (hi - lo <= 1) return;
        int axis = depth % Dims;
        int mid = (lo + hi) / 2;
        QuickSelect(vectors, perm, lo, hi, mid, axis);
        BuildRange(vectors, perm, lo, mid, depth + 1);
        BuildRange(vectors, perm, mid + 1, hi, depth + 1);
    }

    // Coloca em perm[k] o elemento de mediana sobre `axis` no range [lo,hi) (Hoare).
    private static void QuickSelect(ReadOnlySpan<byte> vectors, int[] perm, int lo, int hi, int k, int axis)
    {
        int l = lo, r = hi - 1;
        while (l < r)
        {
            byte pivot = vectors[perm[(l + r) / 2] * Dims + axis];
            int i = l, j = r;
            while (i <= j)
            {
                while (vectors[perm[i] * Dims + axis] < pivot) i++;
                while (vectors[perm[j] * Dims + axis] > pivot) j--;
                if (i <= j) { (perm[i], perm[j]) = (perm[j], perm[i]); i++; j--; }
            }
            if (k <= j) r = j;
            else if (k >= i) l = i;
            else break;
        }
    }

    // Busca pública: retorna fraud_score = (fraudes entre os K mais próximos) / K.
    public static float Search(ReadOnlySpan<byte> vectors, ReadOnlySpan<byte> labels, int count, ReadOnlySpan<byte> query)
    {
        Span<int> pos = stackalloc int[K];
        int filled = SearchTopKInto(vectors, count, query, pos);
        int frauds = 0;
        for (int j = 0; j < filled; j++)
            if (labels[pos[j]] != 0) frauds++;
        return frauds / (float)K;
    }

    // Para testes: retorna as posições (ordenadas) dos K mais próximos.
    public static int[] SearchTopK(ReadOnlySpan<byte> vectors, int count, ReadOnlySpan<byte> query)
    {
        Span<int> pos = stackalloc int[K];
        int filled = SearchTopKInto(vectors, count, query, pos);
        var res = pos.Slice(0, filled).ToArray();
        Array.Sort(res);
        return res;
    }

    private static int SearchTopKInto(ReadOnlySpan<byte> vectors, int count, ReadOnlySpan<byte> query, Span<int> outPos)
    {
        Span<int> bestDist = stackalloc int[K];
        Span<int> bestPos = stackalloc int[K];
        int filled = 0, worst = 0;
        SearchRange(vectors, query, 0, count, 0, bestDist, bestPos, ref filled, ref worst);
        for (int j = 0; j < filled; j++) outPos[j] = bestPos[j];
        return filled;
    }

    private static void SearchRange(
        ReadOnlySpan<byte> vectors, ReadOnlySpan<byte> query, int lo, int hi, int depth,
        Span<int> bestDist, Span<int> bestPos, ref int filled, ref int worst)
    {
        if (hi - lo <= 0) return;
        int axis = depth % Dims;
        int mid = (lo + hi) / 2;

        int d2 = SquaredDistance(query, vectors.Slice(mid * Dims, Dims));
        Consider(mid, d2, bestDist, bestPos, ref filled, ref worst);

        int diff = query[axis] - vectors[mid * Dims + axis];
        int nearLo, nearHi, farLo, farHi;
        if (diff < 0) { nearLo = lo; nearHi = mid; farLo = mid + 1; farHi = hi; }
        else { nearLo = mid + 1; nearHi = hi; farLo = lo; farHi = mid; }

        SearchRange(vectors, query, nearLo, nearHi, depth + 1, bestDist, bestPos, ref filled, ref worst);

        // Poda: só visita o lado distante se o plano de corte puder conter algo ≤ o pior atual.
        // `<=` (não `<`) para não pular empates que poderiam vencer no desempate por posição.
        if (filled < K || diff * diff <= bestDist[worst])
            SearchRange(vectors, query, farLo, farHi, depth + 1, bestDist, bestPos, ref filled, ref worst);
    }

    // Inserção limitada com desempate LEXICOGRÁFICO (dist, pos): mantém os K de menor (dist, pos).
    // Independente da ordem de visita ⇒ idêntico ao brute-force sobre os mesmos bytes.
    private static void Consider(int pos, int dist, Span<int> bestDist, Span<int> bestPos, ref int filled, ref int worst)
    {
        if (filled < K)
        {
            bestDist[filled] = dist; bestPos[filled] = pos; filled++;
            if (filled == K) worst = FindWorst(bestDist, bestPos);
        }
        else if (dist < bestDist[worst] || (dist == bestDist[worst] && pos < bestPos[worst]))
        {
            bestDist[worst] = dist; bestPos[worst] = pos; worst = FindWorst(bestDist, bestPos);
        }
    }

    // Pior slot = o de MAIOR (dist, pos) — o que deve ser descartado primeiro.
    private static int FindWorst(ReadOnlySpan<int> dist, ReadOnlySpan<int> pos)
    {
        int w = 0;
        for (int j = 1; j < dist.Length; j++)
            if (dist[j] > dist[w] || (dist[j] == dist[w] && pos[j] > pos[w])) w = j;
        return w;
    }

    private static int SquaredDistance(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int sum = 0;
        for (int d = 0; d < Dims; d++) { int df = a[d] - b[d]; sum += df * df; }
        return sum;
    }
}
```

- [ ] **Step 4: Rodar — todos devem passar**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "KdTree" -v minimal 2>&1 | tail -6
```
Esperado: todos passam. Se `Search_MatchesBruteForce...` falhar, a poda está descartando candidatos válidos — revise a condição `diff*diff <= bestDist[worst]` e o desempate lexicográfico em `Consider`.

- [ ] **Step 5: Commit**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Detection/KdTree.cs tests/Api.Tests/KdTreeTests.cs
git commit -m "feat: M6 — KD-Tree implícita com busca exata sobre vetores quantizados

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: index.bin — serialização + gerador offline (TDD)

**Files:**
- Create: `src/Api/Data/IndexBuilder.cs`
- Create: `tests/Api.Tests/IndexBuilderTests.cs`
- Create: `src/IndexGen/IndexGen.csproj`
- Create: `src/IndexGen/Program.cs`

`IndexBuilder` quantiza vetores float, constrói a árvore (via `KdTree.Build`) e (de)serializa o `index.bin`. O `IndexGen` é um console que gera o `data/index.bin` de produção a partir do `data/references.bin` (formato float do M2/M3).

**Formato `index.bin`:** `[int32 count][count×14 bytes vetores reordenados][count×1 byte labels reordenados]`.

- [ ] **Step 1: Escrever `tests/Api.Tests/IndexBuilderTests.cs`**

```csharp
using RinhaBackend.Data;
using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class IndexBuilderTests
{
    [Fact]
    public void WriteThenRead_RoundTripsVectorsAndLabels()
    {
        int count = 50;
        var rng = new Random(7);
        var floats = new float[count * 14];
        var labels = new byte[count];
        for (int i = 0; i < count * 14; i++) floats[i] = (float)rng.NextDouble();
        for (int i = 0; i < count; i++) labels[i] = (byte)rng.Next(0, 2);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            IndexBuilder.WriteToFile(floats, labels, count, path);

            // Lê de volta os bytes e confere o cabeçalho + tamanho.
            byte[] raw = File.ReadAllBytes(path);
            int readCount = BitConverter.ToInt32(raw, 0);
            Assert.Equal(count, readCount);
            Assert.Equal(4 + count * 15, raw.Length);

            // A busca sobre o conteúdo do arquivo deve bater com a busca em memória.
            var (memVec, memLab) = KdTree.Build(QuantizeAll(floats, count), labels, count);
            var query = new byte[14];
            for (int d = 0; d < 14; d++) query[d] = Quantizer.Quantize((float)rng.NextDouble());

            float memScore = KdTree.Search(memVec, memLab, count, query);

            var fileVec = raw.AsSpan(4, count * 14);
            var fileLab = raw.AsSpan(4 + count * 14, count);
            float fileScore = KdTree.Search(fileVec, fileLab, count, query);

            Assert.Equal(memScore, fileScore);
        }
        finally { File.Delete(path); }
    }

    private static byte[] QuantizeAll(float[] floats, int count)
    {
        var q = new byte[count * 14];
        for (int i = 0; i < count; i++)
            Quantizer.Quantize(floats.AsSpan(i * 14, 14), q.AsSpan(i * 14, 14));
        return q;
    }
}
```

- [ ] **Step 2: Rodar — deve falhar (IndexBuilder não existe)**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "IndexBuilder" -v minimal 2>&1 | tail -8
```
Esperado: erro de compilação.

- [ ] **Step 3: Criar `src/Api/Data/IndexBuilder.cs`**

```csharp
using RinhaBackend.Detection;

namespace RinhaBackend.Data;

// M6: quantiza vetores float, constrói a KD-Tree (reordena) e (de)serializa o index.bin.
// Formato: [int32 count][count×14 bytes vetores reordenados][count×1 byte labels reordenados].
public static class IndexBuilder
{
    public const int Dims = 14;

    // Quantiza + constrói a árvore em memória. Retorna os arrays reordenados (vetores, labels).
    public static (byte[] vectors, byte[] labels) BuildInMemory(
        ReadOnlySpan<float> floats, ReadOnlySpan<byte> labels, int count)
    {
        var q = new byte[count * Dims];
        for (int i = 0; i < count; i++)
            Quantizer.Quantize(floats.Slice(i * Dims, Dims), q.AsSpan(i * Dims, Dims));
        return KdTree.Build(q, labels, count);
    }

    // Gera o index.bin a partir de vetores float + labels.
    public static void WriteToFile(ReadOnlySpan<float> floats, ReadOnlySpan<byte> labels, int count, string path)
    {
        var (vec, lab) = BuildInMemory(floats, labels, count);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        Span<byte> header = stackalloc byte[4];
        BitConverter.TryWriteBytes(header, count);
        fs.Write(header);
        fs.Write(vec);
        fs.Write(lab);
    }
}
```

- [ ] **Step 4: Rodar — deve passar**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "IndexBuilder" -v minimal 2>&1 | tail -5
```
Esperado: passa.

- [ ] **Step 5: Criar o console `src/IndexGen/IndexGen.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Api/Api.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Criar `src/IndexGen/Program.cs`**

Lê o `references.bin` (formato float do M2/M3: `[int32 count][count×56 bytes floats][count×1 byte labels]`) e grava `index.bin` quantizado/reordenado.

```csharp
using RinhaBackend.Data;

// Uso: dotnet run --project src/IndexGen -- <dataDir>
// Lê <dataDir>/references.bin (float) e grava <dataDir>/index.bin (quantizado + KD-Tree).
string dataDir = args.Length > 0 ? args[0] : "/app/data";
string inPath = Path.Combine(dataDir, "references.bin");
string outPath = Path.Combine(dataDir, "index.bin");

using var fs = new FileStream(inPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
using var r = new BinaryReader(fs);

int count = r.ReadInt32();
if (count < 0 || count > 10_000_000)
    throw new InvalidDataException($"references.bin: count={count} inválido");

var floats = new float[count * 14];
for (int i = 0; i < count * 14; i++) floats[i] = r.ReadSingle();
var labels = new byte[count];
for (int i = 0; i < count; i++) labels[i] = r.ReadByte();

Console.WriteLine($"[IndexGen] {count} vetores lidos. Construindo KD-Tree quantizada...");
IndexBuilder.WriteToFile(floats, labels, count, outPath);
Console.WriteLine($"[IndexGen] index.bin gravado: {new FileInfo(outPath).Length:N0} bytes");
```

- [ ] **Step 7: Commit**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Data/IndexBuilder.cs tests/Api.Tests/IndexBuilderTests.cs src/IndexGen/
git commit -m "feat: M6 — IndexBuilder (index.bin) + console IndexGen (gera o índice offline)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: Runtime — ReferenceDataset (mmap index.bin) + Program + limpeza (TDD)

**Files:**
- Modify (rewrite): `src/Api/Data/ReferenceDataset.cs`
- Modify: `src/Api/Program.cs`
- Delete: `src/Api/Detection/KnnSearch.cs`
- Delete: `tests/Api.Tests/KnnSearchTests.cs`
- Modify: `tests/Api.Tests/DatasetTests.cs`
- Modify: `infra/Dockerfile`

`ReferenceDataset` passa a expor os bytes quantizados+reordenados (via mmap do `index.bin` em produção, ou build em memória a partir do JSON nos testes). `Program.ComputeScore` quantiza o query e chama `KdTree.Search`. O `KnnSearch` brute-force e seus testes são removidos (comportamento migrado para `KdTreeTests`).

- [ ] **Step 1: Reescrever `src/Api/Data/ReferenceDataset.cs`**

```csharp
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using System.Text.Json.Serialization;
using RinhaBackend.Detection;

namespace RinhaBackend.Data;

// M6: expõe os vetores QUANTIZADOS (byte) reordenados em ordem de KD-Tree, mais os labels.
// Produção: mmap do index.bin (file-backed, evictável — truque do M3).
// Testes: fallback que lê JSON, quantiza e constrói a árvore em memória.
public sealed class ReferenceDataset : IDisposable
{
    private const int Dims = 14;

    // --- MMF backing (produção: index.bin) ---
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private unsafe byte* _vectorsPtr;  // count×14 bytes
    private unsafe byte* _labelsPtr;   // count bytes
    private bool _ptrAcquired;

    // --- Managed backing (fallback JSON: testes) ---
    private byte[]? _vectorsArr;
    private byte[]? _labelsArr;

    public int Count { get; private set; }
    public Dictionary<string, float> MccRisk { get; private set; } = [];

    private volatile bool _isReady;
    public bool IsReady => _isReady;

    // Bytes quantizados reordenados (count×14) e labels (count) — usados por KdTree.Search.
    public unsafe ReadOnlySpan<byte> Vectors =>
        _vectorsPtr != null ? new ReadOnlySpan<byte>(_vectorsPtr, Count * Dims) : _vectorsArr.AsSpan(0, Count * Dims);

    public unsafe ReadOnlySpan<byte> Labels =>
        _labelsPtr != null ? new ReadOnlySpan<byte>(_labelsPtr, Count) : _labelsArr.AsSpan(0, Count);

    public async Task LoadAsync(string dataDir)
    {
        var mccPath = Path.Combine(dataDir, "mcc_risk.json");
        var mccJson = await File.ReadAllTextAsync(mccPath);
        MccRisk = JsonSerializer.Deserialize<Dictionary<string, float>>(mccJson)
                  ?? throw new InvalidOperationException("mcc_risk.json inválido");

        var indexPath = Path.Combine(dataDir, "index.bin");
        if (File.Exists(indexPath))
            LoadFromMmf(indexPath);
        else
            await LoadFromJsonAsync(dataDir);

        _isReady = true;
    }

    private unsafe void LoadFromMmf(string indexPath)
    {
        _mmf = MemoryMappedFile.CreateFromFile(
            indexPath, FileMode.Open, mapName: null, capacity: 0, access: MemoryMappedFileAccess.Read);
        _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        byte* ptr = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _ptrAcquired = true;
        try
        {
            int count = *(int*)ptr;  // little-endian, x64
            if (count < 0 || count > 10_000_000)
                throw new InvalidDataException($"index.bin: count={count} inválido");

            long expected = 4L + (long)count * Dims + count;   // header + vetores + labels
            ulong actual = _view.SafeMemoryMappedViewHandle.ByteLength;
            if (actual < (ulong)expected)
                throw new InvalidDataException(
                    $"index.bin: tamanho {actual} menor que o esperado {expected} para count={count}");

            _vectorsPtr = ptr + 4;
            _labelsPtr = ptr + 4 + (long)count * Dims;
            Count = count;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Fallback de teste: lê JSON, quantiza, constrói a árvore em memória.
    private async Task LoadFromJsonAsync(string dataDir)
    {
        var fullPath = Path.Combine(dataDir, "references.json.gz");
        var examplePath = Path.Combine(dataDir, "example-references.json");

        ReferenceEntry[] entries;
        if (File.Exists(fullPath))
        {
            using var fileStream = File.OpenRead(fullPath);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            entries = await JsonSerializer.DeserializeAsync<ReferenceEntry[]>(gzipStream)
                      ?? throw new InvalidOperationException("references.json.gz inválido");
        }
        else if (File.Exists(examplePath))
        {
            var json = await File.ReadAllTextAsync(examplePath);
            entries = JsonSerializer.Deserialize<ReferenceEntry[]>(json)
                      ?? throw new InvalidOperationException("example-references.json inválido");
        }
        else
        {
            throw new FileNotFoundException(
                $"Nenhum dataset em {dataDir}. Esperado: index.bin, references.json.gz ou example-references.json");
        }

        int count = entries.Length;
        var floats = new float[count * Dims];
        var labels = new byte[count];
        for (int i = 0; i < count; i++)
        {
            for (int d = 0; d < Dims; d++) floats[i * Dims + d] = entries[i].Vector[d];
            labels[i] = entries[i].Label == "fraud" ? (byte)1 : (byte)0;
        }

        (_vectorsArr, _labelsArr) = IndexBuilder.BuildInMemory(floats, labels, count);
        Count = count;
    }

    public unsafe void Dispose()
    {
        if (_ptrAcquired)
        {
            _view?.SafeMemoryMappedViewHandle.ReleasePointer();
            _ptrAcquired = false;
            _vectorsPtr = null;
            _labelsPtr = null;
        }
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
    }

    private sealed class ReferenceEntry
    {
        [JsonPropertyName("vector")] public float[] Vector { get; set; } = [];
        [JsonPropertyName("label")] public string Label { get; set; } = "";
    }
}
```

- [ ] **Step 2: Atualizar `src/Api/Program.cs` — `ComputeScore` quantiza o query e chama KdTree**

Substituir o método `ComputeScore` (mantendo o resto do arquivo igual):

```csharp
// M6: síncrono — stackalloc do vetor normalizado E do vetor quantizado; busca na KD-Tree.
static IResult ComputeScore(TransactionRequest req, ReferenceDataset ds)
{
    var knownMerchants = new HashSet<string>(
        req.Customer.KnownMerchants,
        StringComparer.OrdinalIgnoreCase
    );

    Span<float> vector = stackalloc float[14];
    Normalizer.Normalize(req, ds.MccRisk, knownMerchants, vector);

    Span<byte> quantized = stackalloc byte[14];
    Quantizer.Quantize(vector, quantized);

    float fraudScore = KdTree.Search(ds.Vectors, ds.Labels, ds.Count, quantized);

    return Results.Ok(new FraudScoreResponse
    {
        Approved = fraudScore < 0.6f,
        FraudScore = fraudScore
    });
}
```

(O `using RinhaBackend.Detection;` já está no topo do Program.cs desde o M5; `Quantizer` e `KdTree` estão nesse namespace.)

- [ ] **Step 3: Remover o brute-force antigo**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git rm src/Api/Detection/KnnSearch.cs tests/Api.Tests/KnnSearchTests.cs
```

(O comportamento do KNN — all-legit→0, all-fraud→1, 3/5→0.6, sobra das 14 dims, count<5 — foi migrado para `KdTreeTests` na Task 2.)

- [ ] **Step 4: Atualizar `tests/Api.Tests/DatasetTests.cs` para o novo formato**

READ o arquivo primeiro. Ele cria um `references.bin` float e checa `GetVector`/`GetLabel`, que não existem mais. Substituir o conteúdo completo por testes do novo contrato (carrega via fallback JSON e confere `Vectors`/`Labels`/`Count`):

```csharp
using RinhaBackend.Data;
using Xunit;

namespace RinhaBackend.Tests;

public class DatasetTests
{
    private static async Task<(ReferenceDataset ds, string dir)> LoadFromJson(
        float[][] rows, bool[] frauds)
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "mcc_risk.json"), "{}");

        var entries = rows.Select((r, i) => new
        {
            vector = r,
            label = frauds[i] ? "fraud" : "legit"
        });
        await File.WriteAllTextAsync(
            Path.Combine(dir, "example-references.json"),
            System.Text.Json.JsonSerializer.Serialize(entries));

        var ds = new ReferenceDataset();
        await ds.LoadAsync(dir);
        return (ds, dir);
    }

    [Fact]
    public async Task Load_FromJson_ExposesQuantizedVectorsAndLabels()
    {
        float[][] rows =
        [
            Enumerable.Repeat(0.1f, 14).ToArray(),
            Enumerable.Repeat(0.9f, 14).ToArray(),
        ];
        var (ds, dir) = await LoadFromJson(rows, [false, true]);
        try
        {
            Assert.True(ds.IsReady);
            Assert.Equal(2, ds.Count);
            Assert.Equal(2 * 14, ds.Vectors.Length);
            Assert.Equal(2, ds.Labels.Length);
        }
        finally
        {
            ds.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 5: Atualizar `infra/Dockerfile` para gerar o index.bin no build**

READ o Dockerfile primeiro. No estágio de build (que já tem o SDK), depois do `dotnet publish`, gerar o `index.bin` a partir do `references.bin` e copiá-lo para a imagem final. Adicionar, no estágio `build` (após o publish), antes do estágio runtime:

```dockerfile
# M6: gera o index.bin (KD-Tree quantizada) a partir do references.bin no build.
# Requer que data/references.bin esteja no contexto de build (já está, via COPY do contexto).
COPY data/references.bin /build-data/references.bin
COPY data/mcc_risk.json /build-data/mcc_risk.json
RUN dotnet run -c Release --project src/IndexGen -- /build-data
```

E no estágio final, copiar o índice gerado para onde o runtime espera (o volume `../data:/app/data` monta o data local; para não depender só do volume, copie o index.bin gerado para a imagem em `/app/data`):

```dockerfile
COPY --from=build /build-data/index.bin /app/data/index.bin
```

> Nota para o implementador: confira no `docker-compose.yml` se `data/` é montado como volume read-only sobre `/app/data` (M3 montava). Se sim, o `index.bin` precisa existir no host (`data/index.bin`) OU o volume não pode sobrescrever `/app/data`. Caminho mais simples e robusto: **gerar o `data/index.bin` no host** (Task 5, Step 1, via `dotnet run --project src/IndexGen -- data`) e deixar o volume montá-lo, como já era feito com `references.bin`. Nesse caso, a parte do Dockerfile que gera o índice é opcional/redundante — prefira a geração no host e ajuste o `.gitignore` para `data/index.bin`. Decida com base no que o compose realmente monta e **reporte qual caminho usou**.

- [ ] **Step 6: Rodar TODOS os testes**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj -v minimal 2>&1 | tail -8
```
Esperado: `Com falha: 0`. Os testes de Quantizer, KdTree, IndexBuilder, Dataset (novo) e Endpoint passam. Se Endpoint falhar por falta de dataset, confirme que ele aponta para um dir com `example-references.json` (fallback) ou ajuste o setup do teste.

- [ ] **Step 7: Commit**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Data/ReferenceDataset.cs src/Api/Program.cs tests/Api.Tests/DatasetTests.cs infra/Dockerfile
git commit -m "feat: M6 — runtime usa KD-Tree quantizada (mmap index.bin); remove brute-force

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: Gerar índice, medir e documentar

**Files:** gera `data/index.bin`; atualiza Obsidian (vault `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/`).

- [ ] **Step 1: Gerar o `data/index.bin` no host**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" run -c Release --project src/IndexGen -- data 2>&1 | tail -5
ls -lah data/index.bin
```
Esperado: `index.bin` de ~45 MB (`4 + 3M×15`). Adicionar `data/index.bin` ao `.gitignore` (artefato grande, gerado).

- [ ] **Step 2: Build + subir + /ready**

```bash
cd /mnt/c/Users/Ivan/rinha-2026/infra
docker compose build --progress plain 2>&1 | tail -3
docker compose up -d 2>&1 | tail -4
cd /mnt/c/Users/Ivan/rinha-2026
START=$(date +%s)
until curl -sf http://localhost:9999/ready >/dev/null 2>&1; do sleep 2; [ $(( $(date +%s) - START )) -gt 120 ] && { echo TIMEOUT; break; }; done
echo "/ready em $(( $(date +%s) - START ))s"
```

- [ ] **Step 3: Latência de uma request (vs ~20ms do M5)**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
PAYLOAD=$(python3 -c "import json; d=json.load(open('test/test-data.json')); print(json.dumps(d['entries'][0]['request']))")
for i in $(seq 1 10); do curl -s -o /dev/null -w "%{time_total}s\n" -X POST http://localhost:9999/fraud-score -H "Content-Type: application/json" -d "$PAYLOAD"; done
```
Esperado: queda grande vs ~20ms do M5 (a busca toca ~milhares de vetores de 14 bytes, não 3M de 56).

- [ ] **Step 4: k6 oficial + resultado**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
export K6_NO_USAGE_REPORT=true
k6 run test/test.js 2>&1 | tail -5
python3 -m json.tool test/results.json
```
Anotar `p99`, `final_score`, `failure_rate`, breakdown (`tp/tn/fp/fn/http_errors`). **Crítico:** conferir `fp`/`fn` — se a quantização degradou o recall, eles sobem (comparar com M5: fp/fn=0). Comparar tudo com **M5 (p99 2002ms, score −6000, 317 corretas, fp/fn=0)**.

- [ ] **Step 5: RAM + parar**

```bash
docker stats --no-stream --format "table {{.Name}}\t{{.MemUsage}}\t{{.CPUPerc}}" 2>&1
cd /mnt/c/Users/Ivan/rinha-2026/infra && docker compose down 2>&1 | tail -2
```
Esperado: RAM bem menor que antes (índice ~45 MB vs 168 MB de vetores float).

- [ ] **Step 6: Documentar no Obsidian**

Criar a nota de conceito `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/TEORIA/Performance e Sistemas/Quantização Vetorial.md` (float32→int8, escala afim uniforme, por que preserva o ranking, distância em inteiros, trade-off resolução×recall). Linkar `[[SIMD]]`, `[[Distancia Euclidiana]]`.

Complementar `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/TEORIA/Performance e Sistemas/KNN Exato vs ANN.md` com uma seção sobre **KD-Tree implícita** (mediana recursiva, poda exata, por que a eficácia cai com a dimensão, Ball-Tree como alternativa em D médio) — alinhada à medição real (quantos nós a busca visitou em D=14).

Criar a nota de marco `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/RINHA DE BACKEND/M6 - Indice Quantizado.md` (primeira pessoa): o gargalo herdado do M5 (memory-bound), as duas alavancas e como conversam (índice = menos itens; quantização = itens menores; sinergia de cache), a nuance de "exato sobre dados quantizados", a **medição real** (latência, p99, score, fp/fn, RAM, nº de nós visitados) comparando com M5, e o veredito do gate KD-Tree vs Ball-Tree. Linkar `[[Quantização Vetorial]]`, `[[KNN Exato vs ANN]]`, `[[M5 - stackalloc e SIMD]]`.

Atualizar `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/RINHA DE BACKEND/RINHA DE BACKEND.md`: linha do `[[M6 - Indice Quantizado]]` com o resultado; marcar no menu de técnicas o que foi feito.

- [ ] **Step 7: Commit (docs do repo, se houver)**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add .gitignore docs/ 2>/dev/null
git commit -m "chore: registra resultado do M6 (índice quantizado) e ignora data/index.bin" 2>&1 | tail -2 || echo "nada a commitar"
```

---

## Self-Review

**Spec coverage:**
- ✅ Quantização int8 afim uniforme metric-preserving → Task 1 (`Quantizer`).
- ✅ Índice espacial exato (KD-Tree implícita) com poda → Task 2 (`KdTree`), teste de exatidão vs brute-force.
- ✅ Distância em inteiros, sem sqrt → `KdTree.SquaredDistance` (Task 2).
- ✅ Como conversam (índice reduz itens, quantização reduz bytes) → realizado: Kd-Tree sobre bytes quantizados; documentado na Task 5.
- ✅ Formato index.bin + build offline + mmap (truque M3) → Tasks 3 (`IndexBuilder`/`IndexGen`) e 4 (`ReferenceDataset.LoadFromMmf`).
- ✅ Fallback JSON em memória (testes) → Task 4 (`LoadFromJsonAsync` + `IndexBuilder.BuildInMemory`).
- ✅ Orçamento de RAM ~45 MB → conferido na Task 5 Step 1/5.
- ✅ Validação de recall (fp/fn) contra test-data → Task 5 Step 4.
- ✅ Gate KD-Tree vs Ball-Tree (medir nós visitados) → Task 5 (medição) + nota do marco.
- ✅ Contrato HTTP inalterado → Program só troca o miolo de `ComputeScore`.

**Placeholders:** nenhum no código (Tasks 1-4 têm arquivos/métodos completos). A nota de marco (Task 5) é preenchida com números pós-benchmark (inerente à medição). A decisão Dockerfile-vs-host para o index.bin está explicitada com recomendação (gerar no host) e instrução de reportar o caminho — não é placeholder, é decisão delegada com default claro.

**Type/consistência:**
- `Quantizer.Quantize(float)→byte` e `Quantize(ReadOnlySpan<float>, Span<byte>)` — Task 1; usados em IndexBuilder (Task 3) e Program (Task 4) ✓
- `KdTree.Build(ReadOnlySpan<byte>, ReadOnlySpan<byte>, int)→(byte[],byte[])` — Task 2; usado em IndexBuilder.BuildInMemory (Task 3) ✓
- `KdTree.Search(ReadOnlySpan<byte> vectors, ReadOnlySpan<byte> labels, int count, ReadOnlySpan<byte> query)→float` — Task 2; chamado em Program.ComputeScore (Task 4) com `ds.Vectors`, `ds.Labels`, `ds.Count` e o query quantizado ✓
- `KdTree.SearchTopK(...)→int[]` — Task 2; usado só nos testes ✓
- `IndexBuilder.BuildInMemory(ReadOnlySpan<float>, ReadOnlySpan<byte>, int)→(byte[],byte[])` e `WriteToFile(...)` — Task 3; `BuildInMemory` usado em ReferenceDataset (Task 4), `WriteToFile` em IndexGen ✓
- `ReferenceDataset.Vectors`/`Labels` (`ReadOnlySpan<byte>`), `Count`, `MccRisk`, `IsReady`, `LoadAsync`, `Dispose` — Task 4; `Vectors`/`Labels`/`Count` consumidos em Program ✓
- `index.bin` layout `[int32][count×14][count]` — consistente entre `IndexBuilder.WriteToFile`, `ReferenceDataset.LoadFromMmf` (offsets `+4`, `+4+count×14`) e o teste de round-trip ✓
- `KnnSearch` removido e nenhuma referência remanescente (Program agora chama `KdTree`) ✓

**Risco sinalizado:** poda do KD-Tree em D=14 pode visitar muitos nós (mede-se na Task 5; fallback Ball-Tree fica para um marco futuro se necessário). A quantização pode introduzir fp/fn (valida-se na Task 5 Step 4).
