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
        Assert.Equal(14, dataset.Vectors.GetLength(1));
        Assert.Equal(dataset.Count, dataset.Labels.Length);
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
            for (int d = 0; d < 14; d++)
            {
                float v = dataset.Vectors[i, d];
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
            // mcc_risk.json é sempre necessário
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
            Assert.Equal(14, dataset.Vectors.GetLength(1));
            Assert.Equal(2, dataset.Labels.Length);

            Assert.Equal(0.5f, dataset.Vectors[0, 0]);
            Assert.Equal(0.5f, dataset.Vectors[0, 13]);
            Assert.Equal(0.1f, dataset.Vectors[1, 0]);
            Assert.Equal(-1f,  dataset.Vectors[1, 5]);
            Assert.Equal(-1f,  dataset.Vectors[1, 6]);

            Assert.True(dataset.Labels[0]);   // fraude
            Assert.False(dataset.Labels[1]);  // legítimo
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
