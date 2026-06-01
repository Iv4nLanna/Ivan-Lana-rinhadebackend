using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class PartitionedIndexTests
{
    private static float BruteForceScore(byte[] vec, byte[] lab, int start, int end, byte[] query)
    {
        var cand = new List<(int dist, int pos)>();
        for (int i = start; i < end; i++)
        {
            int sum = 0;
            for (int d = 0; d < 14; d++) { int df = query[d] - vec[i * 14 + d]; sum += df * df; }
            cand.Add((sum, i));
        }
        cand.Sort((a, b) => a.dist != b.dist ? a.dist.CompareTo(b.dist) : a.pos.CompareTo(b.pos));
        int k = Math.Min(5, cand.Count);
        int frauds = 0;
        for (int i = 0; i < k; i++) if (lab[cand[i].pos] != 0) frauds++;
        return frauds / 5f;
    }

    [Fact]
    public void Search_MatchesBruteForceWithinThePartition()
    {
        var rng = new Random(11);
        for (int trial = 0; trial < 100; trial++)
        {
            int count = rng.Next(6, 80);
            var vec = new byte[count * 14];
            var lab = new byte[count];
            rng.NextBytes(vec);
            for (int i = 0; i < count; i++) lab[i] = (byte)rng.Next(0, 2);
            if (trial % 2 == 0)
                for (int i = 0; i < vec.Length; i++) vec[i] = (byte)rng.Next(0, 4);

            int mid = count / 2;
            int[] offsets = [0, mid, count];

            var query = new byte[14];
            rng.NextBytes(query);
            if (trial % 2 == 0) for (int d = 0; d < 14; d++) query[d] = (byte)rng.Next(0, 4);

            for (int key = 0; key < 2; key++)
            {
                float got = PartitionedIndex.Search(vec, lab, offsets, key, query);
                float exp = BruteForceScore(vec, lab, offsets[key], offsets[key + 1], query);
                Assert.Equal(exp, got);
            }
        }
    }

    [Fact]
    public void Search_EmptyPartition_ReturnsZero()
    {
        var vec = new byte[3 * 14];
        var lab = new byte[3];
        int[] offsets = [0, 0, 3];
        Assert.Equal(0f, PartitionedIndex.Search(vec, lab, offsets, 0, new byte[14]));
    }

    [Fact]
    public void Search_AllFraudInPartition_ReturnsOne()
    {
        var vec = new byte[5 * 14];
        var lab = new byte[] { 1, 1, 1, 1, 1 };
        int[] offsets = [0, 5];
        Assert.Equal(1f, PartitionedIndex.Search(vec, lab, offsets, 0, new byte[14]));
    }
}
