using System.IO.Compression;
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

    public async Task LoadAsync(string dataDir)
    {
        var mccPath = Path.Combine(dataDir, "mcc_risk.json");
        var mccJson = await File.ReadAllTextAsync(mccPath);
        MccRisk = JsonSerializer.Deserialize<Dictionary<string, float>>(mccJson)
                  ?? throw new InvalidOperationException("mcc_risk.json inválido");

        var binPath = Path.Combine(dataDir, "references.bin");
        if (File.Exists(binPath))
        {
            LoadFromBinary(binPath);
        }
        else
        {
            await LoadFromJsonAsync(dataDir);
        }

        _isReady = true;
    }

    // M2: leitura binária — sem JSON parsing, sem alocações intermediárias
    // Formato: [int32 count][count×14×float32 vetores][count×byte labels]
    private void LoadFromBinary(string binPath)
    {
        using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 1 << 20); // 1 MB de buffer de leitura sequencial
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
