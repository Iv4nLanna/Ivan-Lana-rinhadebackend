using RinhaBackend.Data;
using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class IndexBuilderTests
{
    [Fact]
    public void BuildInMemory_GroupsByPartition_OffsetsMatchKeyCounts()
    {
        int count = 200;
        var rng = new Random(3);
        var floats = new float[count * 14];
        var labels = new byte[count];
        for (int i = 0; i < count * 14; i++) floats[i] = (float)rng.NextDouble();
        for (int i = 0; i < count; i++) labels[i] = (byte)rng.Next(0, 2);

        var (vec, lab, offsets, cuts) = IndexBuilder.BuildInMemory(floats, labels, count);

        Assert.Equal(PartitionKey.Count + 1, offsets.Length);
        Assert.Equal(0, offsets[0]);
        Assert.Equal(count, offsets[^1]);
        Assert.Equal(count * 14, vec.Length);
        Assert.Equal(count, lab.Length);
        Assert.Equal(3, cuts.Length);
        for (int p = 0; p < PartitionKey.Count; p++)
            Assert.True(offsets[p + 1] >= offsets[p], "offsets devem ser não-decrescentes");

        // os tamanhos das gavetas devem bater com recontar as chaves dos floats ORIGINAIS
        // (mesmos cuts) — robusto, sem depender de dequantizar.
        var expected = new int[PartitionKey.Count];
        for (int i = 0; i < count; i++)
            expected[PartitionKey.Compute(floats.AsSpan(i * 14, 14), cuts)]++;
        for (int p = 0; p < PartitionKey.Count; p++)
            Assert.Equal(expected[p], offsets[p + 1] - offsets[p]);
    }

    [Fact]
    public void WriteToFile_RoundTrips_SearchParity()
    {
        int count = 150;
        var rng = new Random(9);
        var floats = new float[count * 14];
        var labels = new byte[count];
        for (int i = 0; i < count * 14; i++) floats[i] = (float)rng.NextDouble();
        for (int i = 0; i < count; i++) labels[i] = (byte)rng.Next(0, 2);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            IndexBuilder.WriteToFile(floats, labels, count, path);
            byte[] raw = File.ReadAllBytes(path);

            int readCount = BitConverter.ToInt32(raw, 0);
            int numParts = BitConverter.ToInt32(raw, 4);
            Assert.Equal(count, readCount);
            Assert.Equal(PartitionKey.Count, numParts);
            int expectedSize = 20 + (numParts + 1) * 4 + count * 15;
            Assert.Equal(expectedSize, raw.Length);

            var fileOffsets = new int[numParts + 1];
            for (int p = 0; p <= numParts; p++) fileOffsets[p] = BitConverter.ToInt32(raw, 20 + p * 4);
            int vecStart = 20 + (numParts + 1) * 4;
            var fileVec = raw.AsSpan(vecStart, count * 14);
            var fileLab = raw.AsSpan(vecStart + count * 14, count);

            var (memVec, memLab, memOff, _) = IndexBuilder.BuildInMemory(floats, labels, count);
            var query = new byte[14];
            for (int d = 0; d < 14; d++) query[d] = Quantizer.Quantize((float)rng.NextDouble());

            for (int key = 0; key < numParts; key++)
            {
                float fileScore = PartitionedIndex.Search(fileVec, fileLab, fileOffsets, key, query);
                float memScore = PartitionedIndex.Search(memVec, memLab, memOff, key, query);
                Assert.Equal(memScore, fileScore);
            }
        }
        finally { File.Delete(path); }
    }
}
