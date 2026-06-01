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
                "Esperado: references.json.gz ou example-references.json");
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

        _isReady = true;
    }

    private sealed class ReferenceEntry
    {
        [JsonPropertyName("vector")]
        public float[] Vector { get; set; } = [];

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";
    }
}
