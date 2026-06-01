using RinhaBackend.Detection;

namespace RinhaBackend.Data;

// M7: monta o índice por partição. Calcula os quartis do valor (dim 0), atribui a gaveta
// de cada vetor, agrupa por gaveta (counting sort), quantiza e serializa.
// index.bin: [int32 count][int32 numPartitions][3×float cuts][(numPartitions+1)×int32 offsets]
//            [count×14 bytes vetores agrupados][count×1 byte labels agrupados]
public static class IndexBuilder
{
    public const int Dims = 14;

    public static (byte[] vectors, byte[] labels, int[] offsets, float[] cuts) BuildInMemory(
        ReadOnlySpan<float> floats, ReadOnlySpan<byte> labels, int count)
    {
        float[] cuts;
        if (count == 0)
        {
            cuts = [0f, 0f, 0f];
        }
        else
        {
            var amounts = new float[count];
            for (int i = 0; i < count; i++) amounts[i] = floats[i * Dims + 0];
            Array.Sort(amounts);
            cuts = [amounts[count / 4], amounts[count / 2], amounts[(3 * count) / 4]];
        }

        var keys = new int[count];
        var counts = new int[PartitionKey.Count];
        for (int i = 0; i < count; i++)
        {
            int k = PartitionKey.Compute(floats.Slice(i * Dims, Dims), cuts);
            keys[i] = k;
            counts[k]++;
        }

        var offsets = new int[PartitionKey.Count + 1];
        for (int p = 0; p < PartitionKey.Count; p++) offsets[p + 1] = offsets[p] + counts[p];

        var cursor = (int[])offsets.Clone();
        var outV = new byte[count * Dims];
        var outL = new byte[count];
        for (int i = 0; i < count; i++)
        {
            int dst = cursor[keys[i]]++;
            Quantizer.Quantize(floats.Slice(i * Dims, Dims), outV.AsSpan(dst * Dims, Dims));
            outL[dst] = labels[i];
        }

        return (outV, outL, offsets, cuts);
    }

    public static void WriteToFile(ReadOnlySpan<float> floats, ReadOnlySpan<byte> labels, int count, string path)
    {
        var (vec, lab, offsets, cuts) = BuildInMemory(floats, labels, count);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        Span<byte> buf = stackalloc byte[4];

        BitConverter.TryWriteBytes(buf, count); fs.Write(buf);
        BitConverter.TryWriteBytes(buf, PartitionKey.Count); fs.Write(buf);
        foreach (float c in cuts) { BitConverter.TryWriteBytes(buf, c); fs.Write(buf); }
        foreach (int o in offsets) { BitConverter.TryWriteBytes(buf, o); fs.Write(buf); }
        fs.Write(vec);
        fs.Write(lab);
    }
}
