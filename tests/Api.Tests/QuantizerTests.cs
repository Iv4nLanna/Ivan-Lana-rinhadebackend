using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class QuantizerTests
{
    [Theory]
    [InlineData(-1f, 0)]
    [InlineData(0f, 128)]
    [InlineData(1f, 255)]
    [InlineData(0.5f, 191)]   // (0.5+1)/2*255 = 191.25 → 191
    public void Quantize_MapsRangeToByte(float input, int expected)
    {
        Assert.Equal((byte)expected, Quantizer.Quantize(input));
    }

    [Theory]
    [InlineData(-5f, 0)]   // clamp abaixo de -1
    [InlineData(5f, 255)]  // clamp acima de 1
    public void Quantize_ClampsOutOfRange(float input, int expected)
    {
        Assert.Equal((byte)expected, Quantizer.Quantize(input));
    }

    [Fact]
    public void QuantizeInto_FillsAllDims()
    {
        float[] src = [-1f, 0f, 1f, 0.5f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f];
        Span<byte> dest = stackalloc byte[14];
        Quantizer.Quantize(src, dest);
        Assert.Equal((byte)0, dest[0]);
        Assert.Equal((byte)128, dest[1]);
        Assert.Equal((byte)255, dest[2]);
        Assert.Equal((byte)191, dest[3]);
        Assert.Equal((byte)128, dest[13]);
    }
}
