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

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private unsafe byte* _vectorsPtr;
    private unsafe byte* _labelsPtr;
    private unsafe int* _offsetsPtr;
    private unsafe float* _cutsPtr;
    private int _numPartitions;
    private bool _ptrAcquired;

    private byte[]? _vectorsArr;
    private byte[]? _labelsArr;
    private int[]? _offsetsArr;
    private float[]? _cutsArr;

    public int Count { get; private set; }
    public Dictionary<string, float> MccRisk { get; private set; } = [];

    // M8: taxa de fraude por gaveta, precomputada no load. Gaveta "pura" (rate ~0 ou ~1)
    // responde O(1) — em gaveta pura os 5 vizinhos têm o mesmo rótulo, então a taxa == kNN.
    public float[] PartitionRate { get; private set; } = [];

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

        ComputePartitionRates();
        _isReady = true;
    }

    // M8: uma passada sobre os labels agrupados → fração de fraude de cada gaveta.
    private void ComputePartitionRates()
    {
        var offsets = Offsets;
        var labels = Labels;
        var rates = new float[_numPartitions];
        for (int p = 0; p < _numPartitions; p++)
        {
            int start = offsets[p], end = offsets[p + 1];
            int frauds = 0;
            for (int i = start; i < end; i++) if (labels[i] != 0) frauds++;
            rates[p] = end > start ? frauds / (float)(end - start) : 0f;
        }
        PartitionRate = rates;
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
