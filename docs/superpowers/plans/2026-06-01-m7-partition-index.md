# M7 — Índice por Partição ("gavetas"): Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduzir o nº de vetores comparados por request de 3M para ~milhares, agrupando o dataset em 32 "gavetas" (3 dims categóricas + 4 faixas de valor) e escaneando só a gaveta do query, sobre vetores quantizados int8.

**Architecture:** Cada vetor recebe uma chave de partição (0..31) a partir das dims 9/10/11 (binárias) e da dim 0 (valor, em 4 faixas por quartis). O índice agrupa os vetores por chave e guarda uma tabela de offsets. Na busca, calcula-se a chave do query e escaneia-se só `[offsets[key], offsets[key+1])` com um buffer top-5. Versão SIMPLES (aproximada): só a gaveta do query. Build offline gera `index.bin`; runtime faz mmap (truque do M3). Substitui o brute-force do M5.

**Tech Stack:** C# .NET 9, `System.IO.MemoryMappedFiles`, `Span<T>`/`stackalloc`, xUnit.

---

## Convenções compartilhadas (todas as tasks)

- `Dims = 14`, `K = 5`, `NumPartitions = 32` (8 combinações de flags × 4 faixas de valor).
- **Chave de partição:** `flags = (v[9]>0.5?1:0) | (v[10]>0.5?2:0) | (v[11]>0.5?4:0)`; `bucket = nº de cortes que v[0] ultrapassa (0..3)`; `key = bucket*8 + flags`.
- **`index.bin` layout** (little-endian, x64):
  ```
  offset 0   : int32 count
  offset 4   : int32 numPartitions (=32)
  offset 8   : 3 × float32  amountCuts (quartis de dim 0)
  offset 20  : (numPartitions+1) × int32 offsets (prefix-sum)
  offset 152 : count × 14 bytes  vetores quantizados, agrupados por partição   (152 = 20 + 33×4)
  offset 152+count×14 : count × 1 byte labels, mesma ordem agrupada
  ```
- **Quantização int8:** afim uniforme `[-1,1]→[0,255]` (ressuscitada do M6).
- **Top-5 com desempate lexicográfico (dist, pos)** — independente da ordem, igual ao brute-force restrito à partição.

---

## Estrutura de arquivos

```
src/Api/Detection/Quantizer.cs        ← CREATE (Task 1)
src/Api/Detection/PartitionKey.cs     ← CREATE (Task 2)
src/Api/Detection/PartitionedIndex.cs ← CREATE (Task 3)
src/Api/Data/IndexBuilder.cs          ← CREATE (Task 4)
src/IndexGen/{IndexGen.csproj,Program.cs} ← CREATE (Task 4)
src/Api/Data/ReferenceDataset.cs      ← REWRITE (Task 5)
src/Api/Program.cs                    ← MODIFY (Task 5)
src/Api/Detection/KnnSearch.cs        ← DELETE (Task 5)
tests/Api.Tests/QuantizerTests.cs     ← CREATE (Task 1)
tests/Api.Tests/PartitionKeyTests.cs  ← CREATE (Task 2)
tests/Api.Tests/PartitionedIndexTests.cs ← CREATE (Task 3)
tests/Api.Tests/IndexBuilderTests.cs  ← CREATE (Task 4)
tests/Api.Tests/KnnSearchTests.cs     ← DELETE (Task 5)
tests/Api.Tests/DatasetTests.cs       ← REWRITE (Task 5)
tests/Api.Tests/EndpointTests.cs      ← REWRITE (Task 5, hermético)
```

---

## Task 1: Quantizer int8 (TDD)

**Files:** Create `src/Api/Detection/Quantizer.cs`, `tests/Api.Tests/QuantizerTests.cs`.

- [ ] **Step 1: `tests/Api.Tests/QuantizerTests.cs`**

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
    [InlineData(0.5f, 191)]
    public void Quantize_MapsRangeToByte(float input, int expected)
        => Assert.Equal((byte)expected, Quantizer.Quantize(input));

    [Theory]
    [InlineData(-5f, 0)]
    [InlineData(5f, 255)]
    public void Quantize_ClampsOutOfRange(float input, int expected)
        => Assert.Equal((byte)expected, Quantizer.Quantize(input));

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
    }
}
```

- [ ] **Step 2: Run — fails (no Quantizer)**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "Quantizer" -v minimal 2>&1 | tail -8
```

- [ ] **Step 3: `src/Api/Detection/Quantizer.cs`**

```csharp
namespace RinhaBackend.Detection;

// M7: quantização afim uniforme [-1,1] → [0,255] (int8). Mesma escala em todas as dims
// preserva o ranking euclidiano. As dims 5/6 podem ser -1 (sentinela) → mapeiam para 0.
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
        for (int d = 0; d < Dims; d++) dest[d] = Quantize(src[d]);
    }
}
```

- [ ] **Step 4: Run — passes**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "Quantizer" -v minimal 2>&1 | tail -5
```

- [ ] **Step 5: Commit**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Detection/Quantizer.cs tests/Api.Tests/QuantizerTests.cs
git commit -m "feat: M7 — Quantizer int8 (escala afim uniforme [-1,1]→[0,255])

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: PartitionKey (TDD)

**Files:** Create `src/Api/Detection/PartitionKey.cs`, `tests/Api.Tests/PartitionKeyTests.cs`.

- [ ] **Step 1: `tests/Api.Tests/PartitionKeyTests.cs`**

```csharp
using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class PartitionKeyTests
{
    private static float[] Vec(float amount, bool online, bool cardPresent, bool merchantUnknown)
    {
        var v = new float[14];
        v[0] = amount;
        v[9] = online ? 1f : 0f;
        v[10] = cardPresent ? 1f : 0f;
        v[11] = merchantUnknown ? 1f : 0f;
        return v;
    }

    private static readonly float[] Cuts = [0.25f, 0.5f, 0.75f];

    [Fact]
    public void Flags_AllZero_AmountBucket0_KeyIsZero()
        => Assert.Equal(0, PartitionKey.Compute(Vec(0.1f, false, false, false), Cuts));

    [Fact]
    public void Flags_AllSet_AddFirstSevenBits()
        => Assert.Equal(7, PartitionKey.Compute(Vec(0.1f, true, true, true), Cuts));

    [Theory]
    [InlineData(0.10f, 0)]  // < 0.25
    [InlineData(0.40f, 1)]  // entre 0.25 e 0.5
    [InlineData(0.60f, 2)]  // entre 0.5 e 0.75
    [InlineData(0.90f, 3)]  // > 0.75
    public void AmountBucket_TimesEight(float amount, int bucket)
        => Assert.Equal(bucket * 8, PartitionKey.Compute(Vec(amount, false, false, false), Cuts));

    [Fact]
    public void Combined_BucketAndFlags()
        // amount 0.6 → bucket 2 → 16; online+card → 1|2 = 3 → 19
        => Assert.Equal(19, PartitionKey.Compute(Vec(0.6f, true, true, false), Cuts));

    [Fact]
    public void KeyAlwaysInRange()
    {
        for (int i = 0; i < 32; i++)
        {
            bool o = (i & 1) != 0, c = (i & 2) != 0, m = (i & 4) != 0;
            float amt = (i % 4) * 0.3f;
            int key = PartitionKey.Compute(Vec(amt, o, c, m), Cuts);
            Assert.InRange(key, 0, 31);
        }
    }
}
```

- [ ] **Step 2: Run — fails (no PartitionKey)**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "PartitionKey" -v minimal 2>&1 | tail -8
```

- [ ] **Step 3: `src/Api/Detection/PartitionKey.cs`**

```csharp
namespace RinhaBackend.Detection;

// M7: "número da gaveta" (0..31). 3 dims categóricas (binárias) + faixa de valor.
// dim 9 = is_online, dim 10 = card_present, dim 11 = merchant desconhecido (todas 0/1).
// dim 0 = amount, fatiado em 4 faixas por 3 cortes (quartis, calculados offline).
public static class PartitionKey
{
    public const int FlagCount = 8;       // 2³ combinações das 3 dims binárias
    public const int AmountBuckets = 4;   // 3 cortes
    public const int Count = FlagCount * AmountBuckets; // 32

    public static int Compute(ReadOnlySpan<float> v, ReadOnlySpan<float> cuts)
    {
        int flags = (v[9] > 0.5f ? 1 : 0)
                  | (v[10] > 0.5f ? 2 : 0)
                  | (v[11] > 0.5f ? 4 : 0);

        int bucket = 0;
        for (int i = 0; i < cuts.Length; i++)
        {
            if (v[0] > cuts[i]) bucket++;
            else break;
        }

        return bucket * FlagCount + flags;
    }
}
```

- [ ] **Step 4: Run — passes**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "PartitionKey" -v minimal 2>&1 | tail -5
```

- [ ] **Step 5: Commit**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Detection/PartitionKey.cs tests/Api.Tests/PartitionKeyTests.cs
git commit -m "feat: M7 — PartitionKey (gaveta a partir das dims categóricas + faixa de valor)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: PartitionedIndex — busca dentro da gaveta (TDD)

**Files:** Create `src/Api/Detection/PartitionedIndex.cs`, `tests/Api.Tests/PartitionedIndexTests.cs`.

Escaneia só o range `[offsets[key], offsets[key+1])` mantendo os 5 mais próximos. Desempate lexicográfico (dist, pos) → resultado igual a um brute-force restrito àquela partição.

- [ ] **Step 1: `tests/Api.Tests/PartitionedIndexTests.cs`**

```csharp
using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class PartitionedIndexTests
{
    // brute-force restrito a [start,end): score com top-5 lexicográfico (dist,pos).
    private static float BruteForceScore(byte[] vec, byte[] lab, int start, int end, byte[] query)
    {
        var cand = new List<(int dist, int pos)>();
        for (int i = start; i < end; i++)
        {
            int sum = 0;
            for (int d = 0; d < 14; d++) { int df = query[d] - vec[i * 14 + d]; sum += df * df; }
            cand.Add((sum, i));
        }
        cand.Sort((a, b) => a.dist != b.dist ? a.dist.CompareTo(b.dist) : a.pos.CompareTo(b.pos));
        int k = Math.Min(5, cand.Count);
        int frauds = 0;
        for (int i = 0; i < k; i++) if (lab[cand[i].pos] != 0) frauds++;
        return frauds / 5f;
    }

    [Fact]
    public void Search_MatchesBruteForceWithinThePartition()
    {
        var rng = new Random(11);
        for (int trial = 0; trial < 100; trial++)
        {
            int count = rng.Next(6, 80);
            var vec = new byte[count * 14];
            var lab = new byte[count];
            rng.NextBytes(vec);
            for (int i = 0; i < count; i++) lab[i] = (byte)rng.Next(0, 2);
            if (trial % 2 == 0) // força empates
                for (int i = 0; i < vec.Length; i++) vec[i] = (byte)rng.Next(0, 4);

            int mid = count / 2;
            int[] offsets = [0, mid, count];

            var query = new byte[14];
            rng.NextBytes(query);
            if (trial % 2 == 0) for (int d = 0; d < 14; d++) query[d] = (byte)rng.Next(0, 4);

            for (int key = 0; key < 2; key++)
            {
                float got = PartitionedIndex.Search(vec, lab, offsets, key, query);
                float exp = BruteForceScore(vec, lab, offsets[key], offsets[key + 1], query);
                Assert.Equal(exp, got);
            }
        }
    }

    [Fact]
    public void Search_EmptyPartition_ReturnsZero()
    {
        var vec = new byte[3 * 14];
        var lab = new byte[3];
        int[] offsets = [0, 0, 3]; // gaveta 0 vazia
        Assert.Equal(0f, PartitionedIndex.Search(vec, lab, offsets, 0, new byte[14]));
    }

    [Fact]
    public void Search_AllFraudInPartition_ReturnsOne()
    {
        // 5 vetores iguais, todos fraude, numa única gaveta
        var vec = new byte[5 * 14];
        var lab = new byte[] { 1, 1, 1, 1, 1 };
        int[] offsets = [0, 5];
        Assert.Equal(1f, PartitionedIndex.Search(vec, lab, offsets, 0, new byte[14]));
    }
}
```

- [ ] **Step 2: Run — fails (no PartitionedIndex)**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "PartitionedIndex" -v minimal 2>&1 | tail -8
```

- [ ] **Step 3: `src/Api/Detection/PartitionedIndex.cs`**

```csharp
namespace RinhaBackend.Detection;

// M7: busca dentro de UMA gaveta (versão simples/aproximada). Escaneia o range da partição
// e mantém os K mais próximos com desempate lexicográfico (dist, pos) — idêntico a um
// brute-force restrito àquela partição.
public static class PartitionedIndex
{
    public const int K = 5;
    public const int Dims = 14;

    public static float Search(
        ReadOnlySpan<byte> vectors, ReadOnlySpan<byte> labels,
        ReadOnlySpan<int> offsets, int key, ReadOnlySpan<byte> query)
    {
        int start = offsets[key];
        int end = offsets[key + 1];

        Span<int> bestDist = stackalloc int[K];
        Span<int> bestPos = stackalloc int[K];
        int filled = 0, worst = 0;

        for (int i = start; i < end; i++)
        {
            int d2 = SquaredDistance(query, vectors.Slice(i * Dims, Dims));
            if (filled < K)
            {
                bestDist[filled] = d2; bestPos[filled] = i; filled++;
                if (filled == K) worst = FindWorst(bestDist, bestPos);
            }
            else if (d2 < bestDist[worst] || (d2 == bestDist[worst] && i < bestPos[worst]))
            {
                bestDist[worst] = d2; bestPos[worst] = i; worst = FindWorst(bestDist, bestPos);
            }
        }

        int frauds = 0;
        for (int j = 0; j < filled; j++)
            if (labels[bestPos[j]] != 0) frauds++;
        return frauds / (float)K;
    }

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

- [ ] **Step 4: Run — passes**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "PartitionedIndex" -v minimal 2>&1 | tail -6
```
Se `Search_MatchesBruteForceWithinThePartition` falhar, a busca não está restrita ao range ou o desempate diverge — revise.

- [ ] **Step 5: Commit**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Detection/PartitionedIndex.cs tests/Api.Tests/PartitionedIndexTests.cs
git commit -m "feat: M7 — PartitionedIndex (busca top-5 dentro da gaveta do query)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: IndexBuilder + IndexGen (TDD)

**Files:** Create `src/Api/Data/IndexBuilder.cs`, `tests/Api.Tests/IndexBuilderTests.cs`, `src/IndexGen/IndexGen.csproj`, `src/IndexGen/Program.cs`.

`IndexBuilder` calcula os quartis do valor, atribui a gaveta de cada vetor, agrupa (counting sort), quantiza e serializa o `index.bin`. `IndexGen` gera o índice de produção a partir do `references.bin` (float).

- [ ] **Step 1: `tests/Api.Tests/IndexBuilderTests.cs`**

```csharp
using RinhaBackend.Data;
using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class IndexBuilderTests
{
    [Fact]
    public void BuildInMemory_GroupsByPartition_OffsetsConsistent()
    {
        int count = 200;
        var rng = new Random(3);
        var floats = new float[count * 14];
        var labels = new byte[count];
        for (int i = 0; i < count * 14; i++) floats[i] = (float)rng.NextDouble();
        for (int i = 0; i < count; i++) labels[i] = (byte)rng.Next(0, 2);

        var (vec, lab, offsets, cuts) = IndexBuilder.BuildInMemory(floats, labels, count);

        Assert.Equal(PartitionKey.Count + 1, offsets.Length);
        Assert.Equal(0, offsets[0]);
        Assert.Equal(count, offsets[^1]);
        Assert.Equal(count * 14, vec.Length);
        Assert.Equal(count, lab.Length);
        Assert.Equal(3, cuts.Length);

        // cada vetor agrupado deve ter a chave da sua gaveta
        for (int key = 0; key < PartitionKey.Count; key++)
            for (int i = offsets[key]; i < offsets[key + 1]; i++)
            {
                var fv = new float[14];
                for (int d = 0; d < 14; d++) fv[d] = vec[i * 14 + d] / 255f * 2f - 1f; // dequant aprox
                // recomputa a chave a partir do byte quantizado das dims categóricas (0 ou 255)
                Assert.Equal(key, RecomputeKey(vec, i, cuts));
            }
    }

    // chave a partir dos bytes quantizados: categóricas viram 0 ou 255 (limiar 128); valor idem.
    private static int RecomputeKey(byte[] vec, int i, float[] cuts)
    {
        int flags = (vec[i * 14 + 9] > 128 ? 1 : 0)
                  | (vec[i * 14 + 10] > 128 ? 2 : 0)
                  | (vec[i * 14 + 11] > 128 ? 4 : 0);
        float amount = vec[i * 14 + 0] / 255f * 2f - 1f;
        int bucket = 0;
        for (int c = 0; c < cuts.Length; c++) { if (amount > cuts[c]) bucket++; else break; }
        return bucket * 8 + flags;
    }

    [Fact]
    public void WriteToFile_RoundTrips_SearchParity()
    {
        int count = 150;
        var rng = new Random(9);
        var floats = new float[count * 14];
        var labels = new byte[count];
        for (int i = 0; i < count * 14; i++) floats[i] = (float)rng.NextDouble();
        for (int i = 0; i < count; i++) labels[i] = (byte)rng.Next(0, 2);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            IndexBuilder.WriteToFile(floats, labels, count, path);
            byte[] raw = File.ReadAllBytes(path);

            int readCount = BitConverter.ToInt32(raw, 0);
            int numParts = BitConverter.ToInt32(raw, 4);
            Assert.Equal(count, readCount);
            Assert.Equal(PartitionKey.Count, numParts);
            int expectedSize = 20 + (numParts + 1) * 4 + count * 15;
            Assert.Equal(expectedSize, raw.Length);

            // spans do arquivo
            var fileOffsets = new int[numParts + 1];
            for (int p = 0; p <= numParts; p++) fileOffsets[p] = BitConverter.ToInt32(raw, 20 + p * 4);
            int vecStart = 20 + (numParts + 1) * 4;
            var fileVec = raw.AsSpan(vecStart, count * 14);
            var fileLab = raw.AsSpan(vecStart + count * 14, count);

            // busca pelo arquivo == busca em memória, p/ uma chave qualquer
            var (memVec, memLab, memOff, _) = IndexBuilder.BuildInMemory(floats, labels, count);
            var query = new byte[14];
            for (int d = 0; d < 14; d++) query[d] = Quantizer.Quantize((float)rng.NextDouble());

            for (int key = 0; key < numParts; key++)
            {
                float fileScore = PartitionedIndex.Search(fileVec, fileLab, fileOffsets, key, query);
                float memScore = PartitionedIndex.Search(memVec, memLab, memOff, key, query);
                Assert.Equal(memScore, fileScore);
            }
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run — fails (no IndexBuilder)**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "IndexBuilder" -v minimal 2>&1 | tail -8
```

- [ ] **Step 3: `src/Api/Data/IndexBuilder.cs`**

```csharp
using RinhaBackend.Detection;

namespace RinhaBackend.Data;

// M7: monta o índice por partição. Calcula os quartis do valor (dim 0), atribui a gaveta
// de cada vetor, agrupa por gaveta (counting sort), quantiza e serializa.
// index.bin: [int32 count][int32 numPartitions][3×float cuts][(numPartitions+1)×int32 offsets]
//            [count×14 bytes vetores agrupados][count×1 byte labels agrupados]
public static class IndexBuilder
{
    public const int Dims = 14;

    public static (byte[] vectors, byte[] labels, int[] offsets, float[] cuts) BuildInMemory(
        ReadOnlySpan<float> floats, ReadOnlySpan<byte> labels, int count)
    {
        // 1. cuts = quartis de dim 0 (valor), p/ faixas equilibradas
        float[] cuts;
        if (count == 0)
        {
            cuts = [0f, 0f, 0f];
        }
        else
        {
            var amounts = new float[count];
            for (int i = 0; i < count; i++) amounts[i] = floats[i * Dims + 0];
            Array.Sort(amounts);
            cuts = [amounts[count / 4], amounts[count / 2], amounts[(3 * count) / 4]];
        }

        // 2. chave de cada vetor + contagem por gaveta
        var keys = new int[count];
        var counts = new int[PartitionKey.Count];
        for (int i = 0; i < count; i++)
        {
            int k = PartitionKey.Compute(floats.Slice(i * Dims, Dims), cuts);
            keys[i] = k;
            counts[k]++;
        }

        // 3. offsets (prefix-sum)
        var offsets = new int[PartitionKey.Count + 1];
        for (int p = 0; p < PartitionKey.Count; p++) offsets[p + 1] = offsets[p] + counts[p];

        // 4. coloca em ordem agrupada, quantizando
        var cursor = (int[])offsets.Clone();
        var outV = new byte[count * Dims];
        var outL = new byte[count];
        for (int i = 0; i < count; i++)
        {
            int dst = cursor[keys[i]]++;
            Quantizer.Quantize(floats.Slice(i * Dims, Dims), outV.AsSpan(dst * Dims, Dims));
            outL[dst] = labels[i];
        }

        return (outV, outL, offsets, cuts);
    }

    public static void WriteToFile(ReadOnlySpan<float> floats, ReadOnlySpan<byte> labels, int count, string path)
    {
        var (vec, lab, offsets, cuts) = BuildInMemory(floats, labels, count);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        Span<byte> buf = stackalloc byte[4];

        BitConverter.TryWriteBytes(buf, count); fs.Write(buf);
        BitConverter.TryWriteBytes(buf, PartitionKey.Count); fs.Write(buf);
        foreach (float c in cuts) { BitConverter.TryWriteBytes(buf, c); fs.Write(buf); }
        foreach (int o in offsets) { BitConverter.TryWriteBytes(buf, o); fs.Write(buf); }
        fs.Write(vec);
        fs.Write(lab);
    }
}
```

- [ ] **Step 4: Run — passes**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "IndexBuilder" -v minimal 2>&1 | tail -5
```

- [ ] **Step 5: `src/IndexGen/IndexGen.csproj`**

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

- [ ] **Step 6: `src/IndexGen/Program.cs`**

```csharp
using RinhaBackend.Data;

// Uso: dotnet run --project src/IndexGen -- <dataDir>
// Lê <dataDir>/references.bin (float) e grava <dataDir>/index.bin (gavetas + quantizado).
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

Console.WriteLine($"[IndexGen] {count} vetores. Calculando gavetas + quantizando...");
IndexBuilder.WriteToFile(floats, labels, count, outPath);
Console.WriteLine($"[IndexGen] index.bin: {new FileInfo(outPath).Length:N0} bytes");
```

- [ ] **Step 7: Build do IndexGen**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" build src/IndexGen/IndexGen.csproj -c Release 2>&1 | tail -4
```
Esperado: Build succeeded. (NÃO rodar contra os 3M agora — isso é Task 6.)

- [ ] **Step 8: Commit**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Data/IndexBuilder.cs tests/Api.Tests/IndexBuilderTests.cs src/IndexGen/
git commit -m "feat: M7 — IndexBuilder (gavetas + quartis + serialização) e IndexGen

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: Runtime — ReferenceDataset + Program + limpeza (TDD)

**Files:** Rewrite `src/Api/Data/ReferenceDataset.cs`, modify `src/Api/Program.cs`, delete `src/Api/Detection/KnnSearch.cs` + `tests/Api.Tests/KnnSearchTests.cs`, rewrite `tests/Api.Tests/DatasetTests.cs` + `tests/Api.Tests/EndpointTests.cs`. **NÃO mexer no Dockerfile** (o `data/index.bin` gerado no host é visto via volume).

- [ ] **Step 1: Reescrever `src/Api/Data/ReferenceDataset.cs`**

```csharp
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using System.Text.Json.Serialization;
using RinhaBackend.Detection;

namespace RinhaBackend.Data;

// M7: carrega o índice por partição. Produção: mmap do index.bin (file-backed, evictável).
// Testes: monta em memória a partir do JSON. Expõe os spans usados por PartitionedIndex.Search.
public sealed class ReferenceDataset : IDisposable
{
    private const int Dims = 14;

    // MMF backing (produção)
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private unsafe byte* _vectorsPtr;
    private unsafe byte* _labelsPtr;
    private unsafe int* _offsetsPtr;
    private unsafe float* _cutsPtr;
    private int _numPartitions;
    private bool _ptrAcquired;

    // Managed backing (testes)
    private byte[]? _vectorsArr;
    private byte[]? _labelsArr;
    private int[]? _offsetsArr;
    private float[]? _cutsArr;

    public int Count { get; private set; }
    public Dictionary<string, float> MccRisk { get; private set; } = [];

    private volatile bool _isReady;
    public bool IsReady => _isReady;

    public unsafe ReadOnlySpan<byte> Vectors =>
        _vectorsPtr != null ? new ReadOnlySpan<byte>(_vectorsPtr, Count * Dims) : _vectorsArr.AsSpan(0, Count * Dims);

    public unsafe ReadOnlySpan<byte> Labels =>
        _labelsPtr != null ? new ReadOnlySpan<byte>(_labelsPtr, Count) : _labelsArr.AsSpan(0, Count);

    public unsafe ReadOnlySpan<int> Offsets =>
        _offsetsPtr != null ? new ReadOnlySpan<int>(_offsetsPtr, _numPartitions + 1) : _offsetsArr.AsSpan();

    public unsafe ReadOnlySpan<float> Cuts =>
        _cutsPtr != null ? new ReadOnlySpan<float>(_cutsPtr, 3) : _cutsArr.AsSpan();

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
            int count = *(int*)ptr;
            int numParts = *(int*)(ptr + 4);
            if (count < 0 || count > 10_000_000)
                throw new InvalidDataException($"index.bin: count={count} inválido");
            if (numParts != PartitionKey.Count)
                throw new InvalidDataException($"index.bin: numPartitions={numParts} inesperado");

            // layout: [4 count][4 numParts][12 cuts][(numParts+1)*4 offsets][count*14 vetores][count labels]
            long offsetsBytes = (long)(numParts + 1) * 4;
            long vecStart = 4 + 4 + 12 + offsetsBytes;
            long expected = vecStart + (long)count * Dims + count;
            ulong actual = _view.SafeMemoryMappedViewHandle.ByteLength;
            if (actual < (ulong)expected)
                throw new InvalidDataException(
                    $"index.bin: tamanho {actual} menor que o esperado {expected} para count={count}");

            _cutsPtr = (float*)(ptr + 8);
            _offsetsPtr = (int*)(ptr + 20);
            _vectorsPtr = ptr + vecStart;
            _labelsPtr = ptr + vecStart + (long)count * Dims;
            _numPartitions = numParts;
            Count = count;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

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

        (_vectorsArr, _labelsArr, _offsetsArr, _cutsArr) = IndexBuilder.BuildInMemory(floats, labels, count);
        _numPartitions = PartitionKey.Count;
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
            _offsetsPtr = null;
            _cutsPtr = null;
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

- [ ] **Step 2: Atualizar `src/Api/Program.cs` — substituir `ComputeScore`**

READ Program.cs. Substituir o método `ComputeScore` por:

```csharp
// M7: síncrono — normaliza, calcula a gaveta, quantiza e busca dentro da gaveta.
static IResult ComputeScore(TransactionRequest req, ReferenceDataset ds)
{
    var knownMerchants = new HashSet<string>(
        req.Customer.KnownMerchants,
        StringComparer.OrdinalIgnoreCase
    );

    Span<float> vector = stackalloc float[14];
    Normalizer.Normalize(req, ds.MccRisk, knownMerchants, vector);

    int key = PartitionKey.Compute(vector, ds.Cuts);

    Span<byte> quantized = stackalloc byte[14];
    Quantizer.Quantize(vector, quantized);

    float fraudScore = PartitionedIndex.Search(ds.Vectors, ds.Labels, ds.Offsets, key, quantized);

    return Results.Ok(new FraudScoreResponse
    {
        Approved = fraudScore < 0.6f,
        FraudScore = fraudScore
    });
}
```
O `using RinhaBackend.Detection;` já está no topo (do M5). Confirme; se faltar, adicione.

- [ ] **Step 3: Remover o brute-force do M5**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
git rm src/Api/Detection/KnnSearch.cs tests/Api.Tests/KnnSearchTests.cs
```

- [ ] **Step 4: Reescrever `tests/Api.Tests/DatasetTests.cs`**

```csharp
using RinhaBackend.Data;
using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class DatasetTests
{
    private static async Task<(ReferenceDataset ds, string dir)> LoadFromJson(float[][] rows, bool[] frauds)
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "mcc_risk.json"), "{}");

        var entries = rows.Select((r, i) => new { vector = r, label = frauds[i] ? "fraud" : "legit" });
        await File.WriteAllTextAsync(
            Path.Combine(dir, "example-references.json"),
            System.Text.Json.JsonSerializer.Serialize(entries));

        var ds = new ReferenceDataset();
        await ds.LoadAsync(dir);
        return (ds, dir);
    }

    [Fact]
    public async Task Load_FromJson_ExposesGroupedIndex()
    {
        float[][] rows =
        [
            Enumerable.Repeat(0.1f, 14).ToArray(),
            Enumerable.Repeat(0.9f, 14).ToArray(),
            Enumerable.Repeat(0.5f, 14).ToArray(),
        ];
        var (ds, dir) = await LoadFromJson(rows, [false, true, false]);
        try
        {
            Assert.True(ds.IsReady);
            Assert.Equal(3, ds.Count);
            Assert.Equal(3 * 14, ds.Vectors.Length);
            Assert.Equal(3, ds.Labels.Length);
            Assert.Equal(PartitionKey.Count + 1, ds.Offsets.Length);
            Assert.Equal(0, ds.Offsets[0]);
            Assert.Equal(3, ds.Offsets[^1]);
            Assert.Equal(3, ds.Cuts.Length);
        }
        finally
        {
            ds.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 5: Reescrever `tests/Api.Tests/EndpointTests.cs` (hermético — não depende do ./data grande)**

READ o arquivo. Trocar a declaração da classe e o construtor (deixar os `[Fact]` e `WaitForReady` como estão):

```csharp
public class EndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
```

```csharp
    private readonly HttpClient _client;
    private readonly string _dataDir;

    public EndpointTests(WebApplicationFactory<Program> factory)
    {
        // dataset pequeno e hermético — sem depender do references.json.gz grande
        _dataDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(Path.Combine(_dataDir, "mcc_risk.json"), "{}");

        var rng = new Random(1);
        var sb = new System.Text.StringBuilder("[");
        for (int i = 0; i < 16; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"vector\":[");
            for (int d = 0; d < 14; d++)
            {
                if (d > 0) sb.Append(',');
                sb.Append(rng.NextDouble().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append("],\"label\":\"").Append(i % 2 == 0 ? "legit" : "fraud").Append("\"}");
        }
        sb.Append(']');
        File.WriteAllText(Path.Combine(_dataDir, "example-references.json"), sb.ToString());

        _client = factory
            .WithWebHostBuilder(b => b.UseSetting("DataPath", _dataDir))
            .CreateClient();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort */ }
    }
```

- [ ] **Step 6: Rodar TODOS os testes**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj -v minimal 2>&1 | tail -10
```
Esperado: `Com falha: 0`. Confirme que EndpointTests passou sem timeout (usa o dataset hermético). grep `src/` por `KnnSearch`, `GetVector`, `GetLabel` — não deve sobrar nada.

- [ ] **Step 7: Commit**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Data/ReferenceDataset.cs src/Api/Program.cs tests/Api.Tests/DatasetTests.cs tests/Api.Tests/EndpointTests.cs
git commit -m "feat: M7 — runtime usa índice por partição (mmap index.bin); remove brute-force

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: Gerar índice, medir e documentar

**Files:** gera `data/index.bin`; atualiza Obsidian.

- [ ] **Step 1: Gerar `data/index.bin` no host + ver distribuição das gavetas**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" run -c Release --project src/IndexGen -- data 2>&1 | tail -3
ls -lah data/index.bin
```
Esperado: ~45 MB (já ignorado por `data/*.bin`). Para ver a distribuição das gavetas (detectar gaveta gigante), ler os offsets do `index.bin`:
```bash
python3 - <<'PY'
import struct
with open('data/index.bin','rb') as f:
    count=struct.unpack('<i',f.read(4))[0]
    nparts=struct.unpack('<i',f.read(4))[0]
    f.read(12)  # cuts
    offs=struct.unpack('<%di'%(nparts+1), f.read((nparts+1)*4))
sizes=[offs[i+1]-offs[i] for i in range(nparts)]
print("count",count,"nparts",nparts)
print("maior gaveta:",max(sizes),"menor:",min(sizes),"média:",count//nparts)
print("top5 gavetas:",sorted(sizes,reverse=True)[:5])
PY
```
Anotar a maior gaveta — se for muito grande (ex.: >500k), é o gargalo residual a documentar.

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

- [ ] **Step 3: Latência de 1 request (vs ~20ms do M5)**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
PAYLOAD=$(python3 -c "import json; d=json.load(open('test/test-data.json')); print(json.dumps(d['entries'][0]['request']))")
for i in $(seq 1 10); do curl -s -o /dev/null -w "%{time_total}s\n" -X POST http://localhost:9999/fraud-score -H "Content-Type: application/json" -d "$PAYLOAD"; done
```

- [ ] **Step 4: k6 oficial + resultado**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
export K6_NO_USAGE_REPORT=true
k6 run test/test.js 2>&1 | tail -5
python3 -m json.tool test/results.json
```
Anotar p99, final_score, failure_rate, breakdown (tp/tn/fp/fn/http_errors). **Crítico: olhar fp/fn** — é o custo da aproximação (só a gaveta). Comparar com **M5 (p99 2002ms, score −6000, 317 corretas, fp/fn=0)**.

- [ ] **Step 5: RAM + parar**
```bash
docker stats --no-stream --format "table {{.Name}}\t{{.MemUsage}}\t{{.CPUPerc}}" 2>&1
cd /mnt/c/Users/Ivan/rinha-2026/infra && docker compose down 2>&1 | tail -2
```

- [ ] **Step 6: Documentar no Obsidian** (vault `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/`)

Criar a nota de conceito `TEORIA/Performance e Sistemas/IVF - Indice por Particao.md` (a ideia das gavetas; por que particionar pelas dims categóricas funciona onde a KD-Tree falhou — explorar a estrutura dos dados vs cortes genéricos; o trade-off aproximado×exato).

Criar a nota de marco `RINHA DE BACKEND/M7 - Indice por Particao.md` (primeira pessoa): por que a KD-Tree do M6 falhou, a virada pra gavetas, como a gaveta é escolhida (3 dims sim/não + faixa de valor) e POR QUÊ (dims binárias = gavetas limpas + "diferente = longe"), a versão simples (só a gaveta do query), e a **medição real** (latência, p99, score, fp/fn, distribuição das gavetas) comparando com M5. Linkar `[[IVF - Indice por Particao]]`, `[[KNN Exato vs ANN]]`, `[[M5 - stackalloc e SIMD]]`.

Atualizar `RINHA DE BACKEND/RINHA DE BACKEND.md`: registrar M6 (KD-Tree, revertido — maldição da dimensionalidade) e M7 (gavetas, com o resultado).

- [ ] **Step 7: Commit (docs do repo, se houver)**
```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add docs/ 2>/dev/null
git commit -m "chore: registra resultado do M7 (índice por partição)" 2>&1 | tail -2 || echo "nada a commitar"
```

---

## Self-Review

**Spec coverage:**
- ✅ Chave de partição (dims 9/10/11 + valor em 4 faixas) → Task 2 (`PartitionKey`).
- ✅ Quantização int8 → Task 1 (`Quantizer`).
- ✅ Busca só na gaveta do query (versão simples) → Task 3 (`PartitionedIndex`).
- ✅ Cortes por quartis (faixas equilibradas) → Task 4 (`IndexBuilder.BuildInMemory`).
- ✅ Formato index.bin (count, numParts, cuts, offsets, vetores, labels) → Task 4 + Task 5 (offsets do mmap batem: cuts@8, offsets@20, vetores@152).
- ✅ mmap (M3) + fallback JSON em memória → Task 5.
- ✅ Gaveta vazia tratada (filled<5 → /5) → Task 3 (`Search_EmptyPartition`).
- ✅ Medição fp/fn + distribuição das gavetas → Task 6.
- ✅ Contrato HTTP inalterado → Program só troca o miolo de ComputeScore.

**Placeholders:** nenhum no código (Tasks 1-5 completas). A nota de marco (Task 6) é preenchida pós-benchmark (inerente à medição).

**Type/consistência:**
- `Quantizer.Quantize(float)→byte`, `Quantize(ReadOnlySpan<float>,Span<byte>)` — Task 1; usados em IndexBuilder (T4) e Program (T5) ✓
- `PartitionKey.Compute(ReadOnlySpan<float>, ReadOnlySpan<float>)→int` e `PartitionKey.Count=32` — Task 2; usados em IndexBuilder (T4), ReferenceDataset (T5 validação numParts), Program (T5) ✓
- `PartitionedIndex.Search(ReadOnlySpan<byte> vectors, ReadOnlySpan<byte> labels, ReadOnlySpan<int> offsets, int key, ReadOnlySpan<byte> query)→float` — Task 3; chamado em IndexBuilderTests (T4) e Program (T5) com `ds.Vectors/Labels/Offsets` + key + query quantizado ✓
- `IndexBuilder.BuildInMemory(...)→(byte[],byte[],int[],float[])` e `WriteToFile(...)` — Task 4; `BuildInMemory` em ReferenceDataset (T5) e IndexBuilderTests; `WriteToFile` em IndexGen ✓
- `ReferenceDataset.Vectors/Labels/Offsets/Cuts/Count/MccRisk/IsReady/LoadAsync/Dispose` — Task 5; `Vectors/Labels/Offsets/Cuts/Count` consumidos em Program ✓
- index.bin offsets: writer (T4: count@0, numParts@4, cuts@8, offsets@20, vetores@20+(numParts+1)*4) == reader (T5: cuts@8, offsets@20, vecStart=4+4+12+(numParts+1)*4) ✓ (numParts=32 → vecStart=152)
- `KnnSearch` removido; Program chama `PartitionedIndex` ✓

**Riscos sinalizados (medidos na Task 6):** (a) gaveta desbalanceada (uma partição gigante mantém custo alto) → adicionar dims de split num marco futuro; (b) recall da aproximação (só a gaveta → fp/fn) → adicionar "espiar vizinhas" se necessário.
