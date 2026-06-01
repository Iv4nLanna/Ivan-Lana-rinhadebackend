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
}
