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

        // Qualquer exceção após AcquirePointer deve liberar recursos para evitar
        // ponteiro adquirido e MMF/view vazados no caminho de falha de startup.
        try
        {
            // Leitura nativa de inteiro little-endian; assume target x64 (little-endian).
            int count = *(int*)ptr;
            if (count < 0 || count > 10_000_000)
                throw new InvalidDataException($"references.bin: count={count} inválido");

            // Valida tamanho mapeado antes de confiar nos offsets: 4 bytes de header
            // + count×56 bytes de vetores (14 floats × 4 bytes) + count bytes de labels.
            long expected = 4L + (long)count * 56 + count;
            ulong actual  = _view.SafeMemoryMappedViewHandle.ByteLength;
            if (actual < (ulong)expected)
                throw new InvalidDataException(
                    $"references.bin: tamanho {actual} bytes menor que o esperado {expected} para count={count}");

            _vectorsPtr = (float*)(ptr + 4);
            _labelsPtr  = ptr + 4 + (long)count * 56;  // 56 = 14 floats × 4 bytes
            Count = count;
        }
        catch
        {
            Dispose();
            throw;
        }
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
            // Anula os ponteiros para que GetVector/GetLabel não desreferenciem
            // memória liberada e para que chamadas repetidas de Dispose sejam seguras.
            _vectorsPtr = null;
            _labelsPtr  = null;
        }
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf  = null;
    }

    private sealed class ReferenceEntry
    {
        [JsonPropertyName("vector")]
        public float[] Vector { get; set; } = [];

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";
    }
}
