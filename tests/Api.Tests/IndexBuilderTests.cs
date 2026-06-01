using RinhaBackend.Data;
using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class IndexBuilderTests
{
    [Fact]
    public void WriteThenRead_RoundTripsVectorsAndLabels()
    {
        int count = 50;
        var rng = new Random(7);
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
            Assert.Equal(count, readCount);
            Assert.Equal(4 + count * 15, raw.Length);

            var (memVec, memLab) = KdTree.Build(QuantizeAll(floats, count), labels, count);
            var query = new byte[14];
            for (int d = 0; d < 14; d++) query[d] = Quantizer.Quantize((float)rng.NextDouble());

            float memScore = KdTree.Search(memVec, memLab, count, query);

            var fileVec = raw.AsSpan(4, count * 14);
            var fileLab = raw.AsSpan(4 + count * 14, count);
            float fileScore = KdTree.Search(fileVec, fileLab, count, query);

            Assert.Equal(memScore, fileScore);
        }
        finally { File.Delete(path); }
    }

    private static byte[] QuantizeAll(float[] floats, int count)
    {
        var q = new byte[count * 14];
        for (int i = 0; i < count; i++)
            Quantizer.Quantize(floats.AsSpan(i * 14, 14), q.AsSpan(i * 14, 14));
        return q;
    }
}
