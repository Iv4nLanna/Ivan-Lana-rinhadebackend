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
