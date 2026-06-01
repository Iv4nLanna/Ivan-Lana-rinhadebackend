namespace RinhaBackend.Detection;

// M7: quantização afim uniforme [-1,1] → [0,255] (int8). Mesma escala em todas as dims
// preserva o ranking euclidiano. As dims 5/6 podem ser -1 (sentinela) → mapeiam para 0.
public static class Quantizer
{
    public const int Dims = 14;

    public static byte Quantize(float x)
    {
        float c = MathF.Max(-1f, MathF.Min(1f, x));
        return (byte)MathF.Round((c + 1f) / 2f * 255f);
    }

    public static void Quantize(ReadOnlySpan<float> src, Span<byte> dest)
    {
        for (int d = 0; d < Dims; d++) dest[d] = Quantize(src[d]);
    }
}
