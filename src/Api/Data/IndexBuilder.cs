using RinhaBackend.Detection;

namespace RinhaBackend.Data;

// M6: quantiza vetores float, constrói a KD-Tree (reordena) e (de)serializa o index.bin.
// Formato: [int32 count][count×14 bytes vetores reordenados][count×1 byte labels reordenados].
public static class IndexBuilder
{
    public const int Dims = 14;

    public static (byte[] vectors, byte[] labels) BuildInMemory(
        ReadOnlySpan<float> floats, ReadOnlySpan<byte> labels, int count)
    {
        var q = new byte[count * Dims];
        for (int i = 0; i < count; i++)
            Quantizer.Quantize(floats.Slice(i * Dims, Dims), q.AsSpan(i * Dims, Dims));
        return KdTree.Build(q, labels, count);
    }

    public static void WriteToFile(ReadOnlySpan<float> floats, ReadOnlySpan<byte> labels, int count, string path)
    {
        var (vec, lab) = BuildInMemory(floats, labels, count);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        Span<byte> header = stackalloc byte[4];
        BitConverter.TryWriteBytes(header, count);
        fs.Write(header);
        fs.Write(vec);
        fs.Write(lab);
    }
}
