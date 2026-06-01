using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class KdTreeTests
{
    // brute-force de referência: os 5 de menor (dist, pos) sobre os MESMOS bytes.
    private static int[] BruteForceTop5(byte[] vec, int count, byte[] query)
    {
        var all = new (int dist, int pos)[count];
        for (int i = 0; i < count; i++)
        {
            int sum = 0;
            for (int d = 0; d < 14; d++) { int df = query[d] - vec[i * 14 + d]; sum += df * df; }
            all[i] = (sum, i);
        }
        Array.Sort(all, (a, b) => a.dist != b.dist ? a.dist.CompareTo(b.dist) : a.pos.CompareTo(b.pos));
        int k = Math.Min(5, count);
        var res = new int[k];
        for (int i = 0; i < k; i++) res[i] = all[i].pos;
        Array.Sort(res);
        return res;
    }

    [Fact]
    public void Search_MatchesBruteForce_OnManyRandomDatasets()
    {
        var rng = new Random(42);
        for (int trial = 0; trial < 200; trial++)
        {
            int count = rng.Next(1, 120);
            var vec = new byte[count * 14];
            var lab = new byte[count];
            rng.NextBytes(vec);
            for (int i = 0; i < count; i++) lab[i] = (byte)rng.Next(0, 2);

            // valores pequenos forçam empates (testa o desempate)
            if (trial % 2 == 0)
                for (int i = 0; i < vec.Length; i++) vec[i] = (byte)rng.Next(0, 4);

            var (rvec, rlab) = KdTree.Build(vec, lab, count);

            for (int q = 0; q < 10; q++)
            {
                var query = new byte[14];
                rng.NextBytes(query);
                if (trial % 2 == 0)
                    for (int d = 0; d < 14; d++) query[d] = (byte)rng.Next(0, 4);

                int[] treeTop = KdTree.SearchTopK(rvec, count, query);
                int[] bf = BruteForceTop5(rvec, count, query);
                Assert.Equal(bf, treeTop);
            }
        }
    }

    private static float Score(float[][] rows, bool[] frauds, float[] query)
    {
        int count = rows.Length;
        var vec = new byte[count * 14];
        var lab = new byte[count];
        for (int i = 0; i < count; i++)
        {
            for (int d = 0; d < 14; d++) vec[i * 14 + d] = Quantizer.Quantize(rows[i][d]);
            lab[i] = frauds[i] ? (byte)1 : (byte)0;
        }
        var (rvec, rlab) = KdTree.Build(vec, lab, count);
        var q = new byte[14];
        Quantizer.Quantize(query, q);
        return KdTree.Search(rvec, rlab, count, q);
    }

    private static float[][] Rows(params float[] vals)
        => vals.Select(v => Enumerable.Repeat(v, 14).ToArray()).ToArray();

    [Fact]
    public void Search_AllLegit_ReturnsZero()
        => Assert.Equal(0f, Score(Rows(0.1f, 0.2f, 0.3f, 0.4f, 0.5f),
                                  [false, false, false, false, false], new float[14]));

    [Fact]
    public void Search_AllFraud_ReturnsOne()
        => Assert.Equal(1f, Score(Rows(0.1f, 0.2f, 0.3f, 0.4f, 0.5f),
                                  [true, true, true, true, true], new float[14]));

    [Fact]
    public void Search_ThreeOfFiveFraud_ReturnsPointSix()
    {
        float[][] rows = Rows(0.01f, 0.02f, 0.03f, 0.5f, 0.6f, 0.7f, 0.8f);
        bool[] frauds = [true, true, true, false, false, true, false];
        Assert.Equal(0.6f, Score(rows, frauds, new float[14]), 4);
    }

    [Fact]
    public void Search_CoversAllFourteenDims()
    {
        float[] legit = Enumerable.Repeat(0.1f, 14).ToArray();
        float[] fraud = new float[14];
        for (int d = 8; d < 14; d++) fraud[d] = 0.5f;
        float[][] rows = [legit, legit, legit, legit, legit, fraud];
        bool[] frauds = [false, false, false, false, false, true];
        Assert.Equal(0f, Score(rows, frauds, new float[14]));
    }

    [Fact]
    public void Search_FewerThanFive_DividesByFive()
    {
        float[][] rows = Rows(0.1f, 0.2f, 0.3f);
        bool[] frauds = [true, true, false];
        Assert.Equal(0.4f, Score(rows, frauds, new float[14]), 4);
    }

    [Fact]
    public void Search_MatchesBruteForce_LargeCount()
    {
        var rng = new Random(123);
        foreach (int count in new[] { 1000, 5000 })
        {
            var vec = new byte[count * 14];
            var lab = new byte[count];
            rng.NextBytes(vec);
            for (int i = 0; i < count; i++) lab[i] = (byte)rng.Next(0, 2);
            var (rvec, rlab) = KdTree.Build(vec, lab, count);
            for (int q = 0; q < 20; q++)
            {
                var query = new byte[14];
                rng.NextBytes(query);
                Assert.Equal(BruteForceTop5(rvec, count, query), KdTree.SearchTopK(rvec, count, query));
            }
        }
    }

    [Fact]
    public void Search_EmptyDataset_ReturnsZero()
    {
        var (rvec, rlab) = KdTree.Build(Array.Empty<byte>(), Array.Empty<byte>(), 0);
        Assert.Empty(rvec);
        Assert.Empty(rlab);
        Assert.Equal(0f, KdTree.Search(rvec, rlab, 0, new byte[14]));
    }
}
