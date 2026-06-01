# M3 — MemoryMappedFile Dataset: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Substituir a alocação heap de 168 MB (`float[count, 14]`) por `MemoryMappedFile`, eliminando o `OutOfMemoryException` que impede os containers de subir dentro do limite de 175 MB/instância.

**Architecture:** `MemoryMappedFile.CreateFromFile` mapeia `references.bin` diretamente no espaço de endereçamento do processo — sem heap allocation. Dois punteiros unsafe (`float*` para vetores, `byte*` para labels) permitem acesso O(1) a qualquer linha. O fallback JSON (testes) mantém arrays gerenciados planos (`float[]` + `bool[]`) — nunca chega a produção. `ReferenceDataset` implementa `IDisposable` para liberar o handle do arquivo.

**Tech Stack:** C# .NET 9, `System.IO.MemoryMappedFiles`, `unsafe` / `AllowUnsafeBlocks`, xUnit

---

## Estrutura de arquivos

```
rinha-2026/
├── src/Api/
│   ├── Api.csproj                      ← MODIFY (Task 3): AllowUnsafeBlocks = true
│   ├── Program.cs                      ← MODIFY (Task 2): atualiza chamada KnnSearch
│   ├── Data/
│   │   └── ReferenceDataset.cs        ← MODIFY (Tasks 1, 3): GetVector/GetLabel + MMF
│   └── Detection/
│       └── KnnSearch.cs               ← MODIFY (Task 2): nova assinatura
└── tests/Api.Tests/
    ├── DatasetTests.cs                 ← MODIFY (Task 1): GetVector/GetLabel em vez de Vectors[i,d]
    └── KnnSearchTests.cs              ← MODIFY (Task 2): usar ReferenceDataset em vez de float[,]
```

---

## Task 1: Adicionar GetVector/GetLabel à ReferenceDataset (TDD)

**Files:**
- Modify: `src/Api/Data/ReferenceDataset.cs`
- Modify: `tests/Api.Tests/DatasetTests.cs`

Esta task adiciona os novos métodos de acesso público, ainda backed pelos arrays existentes (`float[,]` + `bool[]`). A API pública muda; a implementação interna ainda não.

- [ ] **Step 1: Atualizar DatasetTests.cs — os testes vão falhar porque GetVector/GetLabel não existem ainda**

Substituir o conteúdo completo de `tests/Api.Tests/DatasetTests.cs`:

```csharp
using RinhaBackend.Data;
using Xunit;

namespace RinhaBackend.Tests;

public class DatasetTests
{
    [Fact]
    public async Task Load_ExampleFile_LoadsVectorsAndLabels()
    {
        var dataset = new ReferenceDataset();
        var dataDir = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "..", "data"
        );

        await dataset.LoadAsync(dataDir);

        Assert.True(dataset.IsReady);
        Assert.True(dataset.Count > 0);
        Assert.Equal(14, dataset.GetVector(0).Length);
        Assert.NotEmpty(dataset.MccRisk);
    }

    [Fact]
    public async Task Load_ExampleFile_VectorValuesInExpectedRange()
    {
        var dataset = new ReferenceDataset();
        var dataDir = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "..", "data"
        );

        await dataset.LoadAsync(dataDir);

        // All dimensions except 5 and 6 must be in [0, 1]
        for (int i = 0; i < dataset.Count; i++)
        {
            var vec = dataset.GetVector(i);
            for (int d = 0; d < 14; d++)
            {
                float v = vec[d];
                if (d == 5 || d == 6)
                    Assert.True(v == -1f || (v >= 0f && v <= 1f),
                        $"Dim {d} row {i}: value {v} out of range");
                else
                    Assert.InRange(v, 0f, 1f);
            }
        }
    }

    [Fact]
    public async Task Load_BinaryFile_LoadsCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "mcc_risk.json"),
                "{\"5411\":0.15}"
            );

            // Cria references.bin com 2 entradas
            using (var fs = File.Create(Path.Combine(tempDir, "references.bin")))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(2); // count = 2

                // Entrada 0: todas as dims = 0.5f
                for (int d = 0; d < 14; d++) w.Write(0.5f);

                // Entrada 1: dims 5 e 6 = -1f, resto = 0.1f
                for (int d = 0; d < 14; d++) w.Write(d == 5 || d == 6 ? -1f : 0.1f);

                w.Write((byte)1); // label 0: fraude
                w.Write((byte)0); // label 1: legítimo
            }

            var dataset = new ReferenceDataset();
            await dataset.LoadAsync(tempDir);

            Assert.True(dataset.IsReady);
            Assert.Equal(2, dataset.Count);
            Assert.Equal(14, dataset.GetVector(0).Length);

            // Verifica vetores
            Assert.Equal(0.5f, dataset.GetVector(0)[0]);
            Assert.Equal(0.5f, dataset.GetVector(0)[13]);
            Assert.Equal(0.1f, dataset.GetVector(1)[0]);
            Assert.Equal(-1f,  dataset.GetVector(1)[5]);
            Assert.Equal(-1f,  dataset.GetVector(1)[6]);

            // Verifica labels
            Assert.True(dataset.GetLabel(0));   // fraude
            Assert.False(dataset.GetLabel(1));  // legítimo
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Confirmar que os testes falham**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj -v minimal 2>&1 | tail -10
```

Esperado: `FAILED` — `'ReferenceDataset' does not contain a definition for 'GetVector'`

- [ ] **Step 3: Adicionar GetVector e GetLabel em ReferenceDataset.cs**

Adicionar `using System.Runtime.InteropServices;` no topo e os dois métodos públicos logo após a propriedade `IsReady`:

```csharp
// Arquivo completo atualizado:
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RinhaBackend.Data;

public class ReferenceDataset
{
    public float[,] Vectors { get; private set; } = new float[0, 14];
    public bool[] Labels { get; private set; } = [];
    public int Count { get; private set; }
    public Dictionary<string, float> MccRisk { get; private set; } = [];

    private volatile bool _isReady;
    public bool IsReady => _isReady;

    // M3: API pública de acesso — backed por float[,] agora, por MMF em Task 3
    public ReadOnlySpan<float> GetVector(int i)
        => MemoryMarshal.CreateReadOnlySpan(ref Vectors[i, 0], 14);

    public bool GetLabel(int i)
        => Labels[i];

    public async Task LoadAsync(string dataDir)
    {
        var mccPath = Path.Combine(dataDir, "mcc_risk.json");
        var mccJson = await File.ReadAllTextAsync(mccPath);
        MccRisk = JsonSerializer.Deserialize<Dictionary<string, float>>(mccJson)
                  ?? throw new InvalidOperationException("mcc_risk.json inválido");

        var binPath = Path.Combine(dataDir, "references.bin");
        if (File.Exists(binPath))
            LoadFromBinary(binPath);
        else
            await LoadFromJsonAsync(dataDir);

        _isReady = true;
    }

    // M2: leitura binária — sem JSON parsing, sem alocações intermediárias
    // Formato: [int32 count][count×14×float32 vetores][count×byte labels]
    private void LoadFromBinary(string binPath)
    {
        using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 1 << 20);
        using var reader = new BinaryReader(fs);

        int count = reader.ReadInt32();
        if (count < 0 || count > 10_000_000)
            throw new InvalidDataException($"references.bin: count={count} inválido");

        var vectors = new float[count, 14];
        for (int i = 0; i < count; i++)
            for (int d = 0; d < 14; d++)
                vectors[i, d] = reader.ReadSingle();

        var labels = new bool[count];
        for (int i = 0; i < count; i++)
            labels[i] = reader.ReadByte() != 0;

        Count = count;
        Vectors = vectors;
        Labels = labels;
    }

    // Fallback para testes (example-references.json) e compatibilidade
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
                $"Nenhum arquivo de dataset encontrado em {dataDir}. " +
                "Esperado: references.bin, references.json.gz ou example-references.json");
        }

        Count = entries.Length;
        Vectors = new float[Count, 14];
        Labels = new bool[Count];

        for (int i = 0; i < Count; i++)
        {
            for (int d = 0; d < 14; d++)
                Vectors[i, d] = entries[i].Vector[d];
            Labels[i] = entries[i].Label == "fraud";
        }
    }

    private sealed class ReferenceEntry
    {
        [JsonPropertyName("vector")]
        public float[] Vector { get; set; } = [];

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";
    }
}
```

- [ ] **Step 4: Rodar os testes — devem passar**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj -v minimal 2>&1 | tail -5
```

Esperado: `Passed! – Failed: 0, Passed: 35`

- [ ] **Step 5: Commit**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Data/ReferenceDataset.cs tests/Api.Tests/DatasetTests.cs
git commit -m "feat: GetVector/GetLabel API — prepara interface pública para M3 MMF"
```

---

## Task 2: Atualizar KnnSearch e Program.cs (TDD)

**Files:**
- Modify: `src/Api/Detection/KnnSearch.cs`
- Modify: `tests/Api.Tests/KnnSearchTests.cs`
- Modify: `src/Api/Program.cs`

Esta task muda `KnnSearch.Search` para receber `ReferenceDataset` em vez de `float[,]` + `bool[]` + `int`.

- [ ] **Step 1: Atualizar KnnSearchTests.cs — vai falhar porque a assinatura de Search vai mudar**

Substituir o conteúdo completo de `tests/Api.Tests/KnnSearchTests.cs`:

```csharp
using RinhaBackend.Data;
using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class KnnSearchTests
{
    // Helper: escreve references.bin com N entradas onde cada entrada tem
    // todas as 14 dimensões = rowValues[i]
    private static async Task<(ReferenceDataset ds, string dir)> BuildDatasetAsync(
        float[] rowValues, bool[] frauds)
    {
        var rows = rowValues.Select(v => Enumerable.Repeat(v, 14).ToArray()).ToArray();
        return await BuildDatasetFromRowsAsync(rows, frauds);
    }

    // Helper: escreve references.bin com N entradas de vetores arbitrários
    private static async Task<(ReferenceDataset ds, string dir)> BuildDatasetFromRowsAsync(
        float[][] rows, bool[] frauds)
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "mcc_risk.json"), "{}");

        using (var fs = File.Create(Path.Combine(dir, "references.bin")))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(frauds.Length);
            foreach (var row in rows)
                foreach (var f in row)
                    w.Write(f);
            foreach (var l in frauds)
                w.Write(l ? (byte)1 : (byte)0);
        }

        var ds = new ReferenceDataset();
        await ds.LoadAsync(dir);
        return (ds, dir);
    }

    [Fact]
    public async Task Search_AllLegit_ReturnsZero()
    {
        var (ds, dir) = await BuildDatasetAsync(
            rowValues: [0.1f, 0.2f, 0.3f, 0.4f, 0.5f],
            frauds:    [false, false, false, false, false]
        );
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

    [Fact]
    public async Task Search_AllFraud_ReturnsOne()
    {
        var (ds, dir) = await BuildDatasetAsync(
            rowValues: [0.1f, 0.2f, 0.3f, 0.4f, 0.5f],
            frauds:    [true, true, true, true, true]
        );
        try
        {
            float score = KnnSearch.Search(new float[14], ds);
            Assert.Equal(1f, score);
        }
        finally
        {
            ds.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Search_ThreeOfFiveFraud_ReturnsPointSix()
    {
        float[][] rows =
        [
            Enumerable.Repeat(0.01f, 14).ToArray(),
            Enumerable.Repeat(0.02f, 14).ToArray(),
            Enumerable.Repeat(0.03f, 14).ToArray(),
            Enumerable.Repeat(0.5f,  14).ToArray(),
            Enumerable.Repeat(0.6f,  14).ToArray(),
            Enumerable.Repeat(0.7f,  14).ToArray(),
            Enumerable.Repeat(0.8f,  14).ToArray(),
        ];
        bool[] frauds = [true, true, true, false, false, true, false];
        var (ds, dir) = await BuildDatasetFromRowsAsync(rows, frauds);
        try
        {
            float score = KnnSearch.Search(new float[14], ds);
            Assert.Equal(0.6f, score, 4);
        }
        finally
        {
            ds.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Search_ApprovalThreshold_BelowPointSixApproved()
    {
        var (ds, dir) = await BuildDatasetAsync(
            rowValues: [0.1f, 0.2f, 0.3f, 0.4f, 0.5f],
            frauds:    [true, true, false, false, false]
        );
        try
        {
            float score = KnnSearch.Search(new float[14], ds);
            Assert.Equal(0.4f, score, 4);
            Assert.True(score < 0.6f);
        }
        finally
        {
            ds.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Confirmar que os testes falham**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "KnnSearch" -v minimal 2>&1 | tail -10
```

Esperado: erro de compilação — `KnnSearch.Search` ainda tem assinatura antiga.

- [ ] **Step 3: Atualizar KnnSearch.cs com nova assinatura**

Substituir o conteúdo completo de `src/Api/Detection/KnnSearch.cs`:

```csharp
using RinhaBackend.Data;

namespace RinhaBackend.Detection;

public static class KnnSearch
{
    // M1: brute force naive com nova assinatura (M3)
    // Alocação de ~12 MB por request (new float[count]) será eliminada em M4
    public static float Search(float[] query, ReferenceDataset ds)
    {
        int count = ds.Count;
        var distances = new float[count];

        for (int i = 0; i < count; i++)
        {
            var vec = ds.GetVector(i);
            float sum = 0f;
            for (int d = 0; d < 14; d++)
            {
                float diff = query[d] - vec[d];
                sum += diff * diff;
            }
            distances[i] = sum;
        }

        var top5 = distances
            .Select((dist, idx) => (dist, idx))
            .OrderBy(x => x.dist)
            .Take(5)
            .ToArray();

        int fraudCount = top5.Count(x => ds.GetLabel(x.idx));
        return fraudCount / 5f;
    }
}
```

- [ ] **Step 4: Atualizar Program.cs para a nova assinatura**

Substituir a linha do KnnSearch em `src/Api/Program.cs`:

```csharp
// Linha antiga:
    var fraudScore = KnnSearch.Search(vector, ds.Vectors, ds.Labels, ds.Count);

// Linha nova:
    var fraudScore = KnnSearch.Search(vector, ds);
```

O arquivo completo após a mudança:

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

    var knownMerchants = new HashSet<string>(
        req.Customer.KnownMerchants,
        StringComparer.OrdinalIgnoreCase
    );

    var vector = Normalizer.Normalize(req, ds.MccRisk, knownMerchants);
    var fraudScore = KnnSearch.Search(vector, ds);

    return Results.Ok(new FraudScoreResponse
    {
        Approved = fraudScore < 0.6f,
        FraudScore = fraudScore
    });
});

app.Run();

public partial class Program { }
```

- [ ] **Step 5: Rodar todos os testes — devem passar**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj -v minimal 2>&1 | tail -5
```

Esperado: `Passed! – Failed: 0, Passed: 35`

- [ ] **Step 6: Commit**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Detection/KnnSearch.cs tests/Api.Tests/KnnSearchTests.cs src/Api/Program.cs
git commit -m "feat: KnnSearch aceita ReferenceDataset — prepara para MMF em M3"
```

---

## Task 3: MemoryMappedFile — substituir heap por páginas de arquivo (TDD)

**Files:**
- Modify: `src/Api/Api.csproj`
- Modify: `src/Api/Data/ReferenceDataset.cs`

Esta task substitui o `float[count, 14]` heap por ponteiros unsafe apontando para o arquivo mapeado. Os testes das Tasks 1 e 2 continuam válidos — testam comportamento (GetVector/GetLabel), não o backing interno.

**Nota para o implementador:** o formato do arquivo binário é:
```
Offset 0          : int32 little-endian        = count
Offset 4          : count × 14 × float32       = vetores (row-major)
Offset 4+count×56 : count × uint8              = labels (1=fraude, 0=legítimo)
```

`count × 14 floats × 4 bytes/float = count × 56 bytes` para vetores.

- [ ] **Step 1: Adicionar AllowUnsafeBlocks ao Api.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>Api</AssemblyName>
    <RootNamespace>RinhaBackend</RootNamespace>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Confirmar que os testes ainda passam (compilação com AllowUnsafeBlocks)**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj -v minimal 2>&1 | tail -5
```

Esperado: `Passed! – Failed: 0, Passed: 35`

- [ ] **Step 3: Substituir ReferenceDataset.cs pela versão MMF completa**

Substituir o conteúdo completo de `src/Api/Data/ReferenceDataset.cs`:

```csharp
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RinhaBackend.Data;

public sealed class ReferenceDataset : IDisposable
{
    // --- MMF backing (produção: references.bin) ---
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private unsafe float* _vectorsPtr;  // aponta para byte 4 do arquivo mapeado
    private unsafe byte* _labelsPtr;    // aponta para byte (4 + count×56) do arquivo mapeado
    private bool _ptrAcquired;

    // --- Managed array backing (fallback JSON: testes pequenos) ---
    private float[]? _vectorsFlat; // flat row-major: índice [i,d] → i*14+d
    private bool[]? _labelsArray;

    public int Count { get; private set; }
    public Dictionary<string, float> MccRisk { get; private set; } = [];

    private volatile bool _isReady;
    public bool IsReady => _isReady;

    // Hot path: chamado 3M vezes por request no KNN
    // M3: sem cópia — Span aponta diretamente para memória mapeada ou array plano
    public unsafe ReadOnlySpan<float> GetVector(int i)
    {
        if (_vectorsPtr != null)
            return new ReadOnlySpan<float>(_vectorsPtr + i * 14, 14);
        return _vectorsFlat.AsSpan(i * 14, 14);
    }

    public unsafe bool GetLabel(int i)
    {
        if (_labelsPtr != null)
            return _labelsPtr[i] != 0;
        return _labelsArray![i];
    }

    public async Task LoadAsync(string dataDir)
    {
        var mccPath = Path.Combine(dataDir, "mcc_risk.json");
        var mccJson = await File.ReadAllTextAsync(mccPath);
        MccRisk = JsonSerializer.Deserialize<Dictionary<string, float>>(mccJson)
                  ?? throw new InvalidOperationException("mcc_risk.json inválido");

        var binPath = Path.Combine(dataDir, "references.bin");
        if (File.Exists(binPath))
            LoadFromMmf(binPath);
        else
            await LoadFromJsonAsync(dataDir);

        _isReady = true;
    }

    // M3: mapeia o arquivo diretamente no espaço de endereçamento — sem heap allocation
    // para os 168 MB de vetores. Páginas são file-backed: o kernel as evicta se necessário,
    // sem OOM kill. Dois processos mapeando o mesmo arquivo compartilham as páginas físicas.
    private unsafe void LoadFromMmf(string binPath)
    {
        _mmf = MemoryMappedFile.CreateFromFile(
            binPath, FileMode.Open, mapName: null, capacity: 0,
            access: MemoryMappedFileAccess.Read);

        _view = _mmf.CreateViewAccessor(
            offset: 0, size: 0,
            access: MemoryMappedFileAccess.Read);

        byte* ptr = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _ptrAcquired = true;

        int count = *(int*)ptr;
        if (count < 0 || count > 10_000_000)
            throw new InvalidDataException($"references.bin: count={count} inválido");

        _vectorsPtr = (float*)(ptr + 4);
        _labelsPtr  = ptr + 4 + (long)count * 56;  // 56 = 14 floats × 4 bytes
        Count = count;
    }

    // Fallback para testes: carrega example-references.json ou references.json.gz
    // em arrays gerenciados planos (float[] + bool[]) — nunca atinge produção
    private async Task LoadFromJsonAsync(string dataDir)
    {
        var fullPath    = Path.Combine(dataDir, "references.json.gz");
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
                $"Nenhum arquivo de dataset encontrado em {dataDir}. " +
                "Esperado: references.bin, references.json.gz ou example-references.json");
        }

        Count = entries.Length;
        _vectorsFlat = new float[Count * 14];
        _labelsArray = new bool[Count];

        for (int i = 0; i < Count; i++)
        {
            for (int d = 0; d < 14; d++)
                _vectorsFlat[i * 14 + d] = entries[i].Vector[d];
            _labelsArray[i] = entries[i].Label == "fraud";
        }
    }

    public unsafe void Dispose()
    {
        if (_ptrAcquired)
        {
            _view?.SafeMemoryMappedViewHandle.ReleasePointer();
            _ptrAcquired = false;
        }
        _view?.Dispose();
        _mmf?.Dispose();
    }

    private sealed class ReferenceEntry
    {
        [JsonPropertyName("vector")]
        public float[] Vector { get; set; } = [];

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";
    }
}
```

- [ ] **Step 4: Rodar todos os testes — devem passar**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj -v minimal 2>&1 | tail -5
```

Esperado: `Passed! – Failed: 0, Passed: 35`

O teste `Load_BinaryFile_LoadsCorrectly` agora exercita o caminho MMF real — cria um `references.bin` temporário e lê via `LoadFromMmf`. Se os valores estiverem corretos, o MMF está funcionando.

- [ ] **Step 5: Commit**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Api.csproj src/Api/Data/ReferenceDataset.cs
git commit -m "feat: M3 — MemoryMappedFile substitui heap de 168 MB, startup dentro de 175 MB"
```

---

## Task 4: Verificar startup e RAM com Docker

**Files:** nenhum — apenas medição

- [ ] **Step 1: Rebuild e subir os containers**

```bash
cd /mnt/c/Users/Ivan/rinha-2026/infra
docker compose build --progress plain 2>&1 | tail -5
docker compose up -d 2>&1
```

- [ ] **Step 2: Medir tempo até /ready responder**

```bash
START=$(date +%s)
echo "Cronômetro: $(date)"
until curl -sf http://localhost:9999/ready 2>/dev/null; do
  echo "  aguardando... $(($(date +%s) - START))s"
  sleep 3
done
echo "=== /ready respondeu em $(($(date +%s) - START)) segundos ==="
```

Resultado esperado: **< 60 segundos** (vs OOM imediato em M2).

- [ ] **Step 3: Verificar RAM**

```bash
docker stats --no-stream --format "table {{.Name}}\t{{.MemUsage}}\t{{.CPUPerc}}"
```

Resultado esperado:
- `rinha-2026-api-1-1`: **< 60 MiB / 175 MiB** (sem a alocação de 168 MB heap)
- `rinha-2026-api-2-1`: **< 60 MiB / 175 MiB`**

Se os valores forem maiores (~194 MB), o Docker está contabilizando as páginas file-backed individualmente por container — anote o valor e documente como aprendizado (comportamento depende da versão do cgroup/kernel).

- [ ] **Step 4: Teste manual do /fraud-score**

```bash
curl -s -X POST http://localhost:9999/fraud-score \
  -H "Content-Type: application/json" \
  -d '{
    "id": "m3-test",
    "transaction": {"amount": 150.0, "installments": 1, "requested_at": "2026-01-15T14:30:00Z"},
    "customer": {"avg_amount": 120.0, "tx_count_24h": 3, "known_merchants": ["MERC-1"]},
    "merchant": {"id": "MERC-99", "mcc": "5411", "avg_amount": 200.0},
    "terminal": {"is_online": false, "card_present": true, "km_from_home": 2.5},
    "last_transaction": {"timestamp": "2026-01-15T12:00:00Z", "km_from_current": 1.2}
  }' | python3 -m json.tool
```

Resultado esperado: JSON com `"approved"` e `"fraud_score"`.

- [ ] **Step 5: Parar os containers**

```bash
cd /mnt/c/Users/Ivan/rinha-2026/infra
docker compose down
```

- [ ] **Step 6: Commit dos resultados**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add docs/
git commit -m "chore: registra resultado de startup e RAM do M3"
```

---

## Self-Review

**Spec coverage:**
- ✅ `MemoryMappedFile` substitui alocação heap de 168 MB
- ✅ Dois processos mapeando o mesmo arquivo compartilham páginas físicas
- ✅ Fallback JSON mantido (testes continuam passando sem references.bin no tempDir)
- ✅ `IDisposable` implementado — handle do arquivo liberado no shutdown
- ✅ `AllowUnsafeBlocks` habilitado
- ✅ Verificação Docker com medição de startup e RAM

**Placeholders:** nenhum — todo o código está completo.

**Type consistency:**
- `GetVector(int i)` → `ReadOnlySpan<float>` — definido em Task 1, usado em Tasks 2 e 3 ✓
- `GetLabel(int i)` → `bool` — definido em Task 1, usado em Tasks 2 e 3 ✓
- `KnnSearch.Search(float[] query, ReferenceDataset ds)` — definido em Task 2, chamado em Program.cs Task 2 ✓
- `LoadFromMmf(string binPath)` — private, chamado só em `LoadAsync` ✓
- `_vectorsPtr` → `float*`, `_labelsPtr` → `byte*` — consistentes em GetVector, GetLabel e LoadFromMmf ✓

---

## Resultados da medição (Task 4) — 2026-06-01

Medido com `references.bin` real de **163 MB** (3M vetores × 14 dims), Docker 29.4.2 em WSL2, limite de 175 MB/instância.

**Startup até `/ready`:** ambos os containers (`api-1`, `api-2`) ficaram `Healthy` em poucos segundos após `docker compose up -d` — sem OOM. Comparar com M2, onde a alocação heap de 168 MB causava `OutOfMemoryException` imediato e os containers nem subiam.

**RAM logo após o startup (antes de qualquer request):**

| Container            | MEM USAGE / LIMIT   | MEM %  |
|----------------------|---------------------|--------|
| `rinha-2026-api-1-1` | 23.53 MiB / 175 MiB | 13.4 % |
| `rinha-2026-api-2-1` | 23.12 MiB / 175 MiB | 13.2 % |
| `rinha-2026-nginx-1` | 6.87 MiB / 10 MiB   | 68.7 % |

As páginas de `references.bin` ainda **não** foram faultadas — o `CreateFromFile` apenas mapeia o arquivo no espaço de endereçamento; o kernel só traz páginas para a RAM quando são tocadas. Por isso o working set inicial é ~23 MB (runtime .NET + Kestrel), não 168 MB.

**RAM após um `/fraud-score` (KNN brute-force varre todos os 3M vetores):**

| Container            | MEM USAGE / LIMIT    | MEM %  |
|----------------------|----------------------|--------|
| `rinha-2026-api-1-1` | 26.07 MiB / 175 MiB  | 14.9 % |
| `rinha-2026-api-2-1` | 109.4 MiB / 175 MiB  | 62.5 % |

O nginx roteou o request para o `api-2`, que então tocou todas as páginas de vetores durante o KNN → o kernel faultou o arquivo inteiro para a RAM, subindo o working set para **109 MB**. Continua **bem abaixo do limite de 175 MB** — sem OOM kill. O `api-1`, ocioso, ficou em 26 MB.

**Aprendizado importante:** o Docker contabiliza as páginas file-backed do MMF no `MemUsage` do container que as faulta (cada processo conta as suas — não há o "compartilhamento de 0 MB" idealizado, pois cada container é um cgroup separado). Mesmo assim, 109 MB de pico < 175 MB resolve o objetivo do M3: **os containers sobem e servem requests dentro do limite**, o que era impossível com a alocação heap de 168 MB no M2. A pressão de RAM passou a ser file-backed (evictável pelo kernel sob pressão, sem OOM kill do processo) em vez de heap gerenciado (não-evictável → OOM kill).

**`/fraud-score` manual:** retornou `{"approved": true, "fraud_score": 0.4}` — endpoint funcional ponta a ponta.
