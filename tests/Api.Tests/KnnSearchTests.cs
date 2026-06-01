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

    [Fact]
    public async Task Search_TiedDistances_PicksLowestIndices()
    {
        // 6 vetores idênticos → todos empatam na distância à query (zero).
        // OrderBy().Take(5) estável mantém os 5 de MENOR índice (0..4) e descarta o índice 5.
        // Só o índice 5 (descartado) é fraude → o score correto é 0.
        // Uma seleção Top-K que desempata errado manteria o índice 5 e daria 0.2.
        float[][] rows =
        [
            Enumerable.Repeat(0.1f, 14).ToArray(),
            Enumerable.Repeat(0.1f, 14).ToArray(),
            Enumerable.Repeat(0.1f, 14).ToArray(),
            Enumerable.Repeat(0.1f, 14).ToArray(),
            Enumerable.Repeat(0.1f, 14).ToArray(),
            Enumerable.Repeat(0.1f, 14).ToArray(),
        ];
        bool[] frauds = [false, false, false, false, false, true];
        var (ds, dir) = await BuildDatasetFromRowsAsync(rows, frauds);
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
    public async Task Search_FewerThanFiveVectors_DividesByFive()
    {
        // Só 3 vetores no dataset; 2 são fraude. O score divide por 5 (constante K),
        // não por 3 — replicando o `Take(5)` + `/5f` original quando há < 5 vizinhos.
        float[][] rows =
        [
            Enumerable.Repeat(0.1f, 14).ToArray(),
            Enumerable.Repeat(0.2f, 14).ToArray(),
            Enumerable.Repeat(0.3f, 14).ToArray(),
        ];
        bool[] frauds = [true, true, false];
        var (ds, dir) = await BuildDatasetFromRowsAsync(rows, frauds);
        try
        {
            float score = KnnSearch.Search(new float[14], ds);
            Assert.Equal(0.4f, score, 4); // 2 fraudes / 5 = 0.4
        }
        finally
        {
            ds.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }
}
