using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class PartitionKeyTests
{
    private static float[] Vec(float amount, bool online, bool cardPresent, bool merchantUnknown)
    {
        var v = new float[14];
        v[0] = amount;
        v[9] = online ? 1f : 0f;
        v[10] = cardPresent ? 1f : 0f;
        v[11] = merchantUnknown ? 1f : 0f;
        return v;
    }

    private static readonly float[] Cuts = [0.25f, 0.5f, 0.75f];

    [Fact]
    public void Flags_AllZero_AmountBucket0_KeyIsZero()
        => Assert.Equal(0, PartitionKey.Compute(Vec(0.1f, false, false, false), Cuts));

    [Fact]
    public void Flags_AllSet_AddFirstSevenBits()
        => Assert.Equal(7, PartitionKey.Compute(Vec(0.1f, true, true, true), Cuts));

    [Theory]
    [InlineData(0.10f, 0)]
    [InlineData(0.40f, 1)]
    [InlineData(0.60f, 2)]
    [InlineData(0.90f, 3)]
    public void AmountBucket_TimesEight(float amount, int bucket)
        => Assert.Equal(bucket * 8, PartitionKey.Compute(Vec(amount, false, false, false), Cuts));

    [Fact]
    public void Combined_BucketAndFlags()
        => Assert.Equal(19, PartitionKey.Compute(Vec(0.6f, true, true, false), Cuts));

    [Fact]
    public void KeyAlwaysInRange()
    {
        for (int i = 0; i < 32; i++)
        {
            bool o = (i & 1) != 0, c = (i & 2) != 0, m = (i & 4) != 0;
            float amt = (i % 4) * 0.3f;
            int key = PartitionKey.Compute(Vec(amt, o, c, m), Cuts);
            Assert.InRange(key, 0, 31);
        }
    }
}
