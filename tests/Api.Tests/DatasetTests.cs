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
