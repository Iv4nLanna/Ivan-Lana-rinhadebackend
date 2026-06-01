namespace RinhaBackend.Detection;

// M6: quantização afim uniforme [-1,1] → [0,255] (int8).
// Mesma escala em todas as dims ⇒ Σ(q(a)-q(b))² = (255/2)² · Σ(a-b)² ⇒ ranking euclidiano preservado.
// As dims 5 e 6 podem ser -1 (sentinela); -1 mapeia para 0 naturalmente.
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
        for (int d = 0; d < Dims; d++)
            dest[d] = Quantize(src[d]);
    }
}
