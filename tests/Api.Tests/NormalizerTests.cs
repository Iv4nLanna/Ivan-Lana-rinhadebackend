using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class NormalizerTests
{
    private static TransactionRequest Make(
        decimal amount = 5000m,
        int installments = 6,
        string requestedAt = "2026-01-12T14:30:00Z",
        decimal customerAvgAmount = 5000m,
        int txCount24h = 5,
        string[]? knownMerchants = null,
        string merchantId = "MERC-1",
        string mcc = "5411",
        decimal merchantAvgAmount = 2000m,
        bool isOnline = false,
        bool cardPresent = true,
        float kmFromHome = 5f,
        LastTransactionData? lastTransaction = null
    ) => new()
    {
        Id = "test",
        Transaction = new() { Amount = amount, Installments = installments, RequestedAt = DateTimeOffset.Parse(requestedAt) },
        Customer = new() { AvgAmount = customerAvgAmount, TxCount24h = txCount24h, KnownMerchants = (knownMerchants ?? []).ToList() },
        Merchant = new() { Id = merchantId, Mcc = mcc, AvgAmount = merchantAvgAmount },
        Terminal = new() { IsOnline = isOnline, CardPresent = cardPresent, KmFromHome = kmFromHome },
        LastTransaction = lastTransaction
    };

    private static float[] Normalize(TransactionRequest req,
        Dictionary<string, float>? mcc = null,
        HashSet<string>? known = null)
    {
        var vec = new float[14];
        Normalizer.Normalize(req, mcc ?? [], known ?? new HashSet<string>(req.Customer.KnownMerchants), vec);
        return vec;
    }

    [Fact] public void Dim0_MidRange() => Assert.Equal(0.5f, Normalize(Make(amount: 5000m))[0], 4);
    [Fact] public void Dim0_ClampsAboveMax() => Assert.Equal(1.0f, Normalize(Make(amount: 15000m))[0]);
    [Fact] public void Dim0_Zero() => Assert.Equal(0.0f, Normalize(Make(amount: 0m))[0]);

    [Fact] public void Dim1_SixOf12() => Assert.Equal(0.5f, Normalize(Make(installments: 6))[1], 4);
    [Fact] public void Dim1_ClampsAboveMax() => Assert.Equal(1.0f, Normalize(Make(installments: 24))[1]);

    [Fact] public void Dim2_EqualToAvg() => Assert.Equal(0.1f, Normalize(Make(amount: 5000m, customerAvgAmount: 5000m))[2], 4);
    [Fact] public void Dim2_Clamps() => Assert.Equal(1.0f, Normalize(Make(amount: 100000m, customerAvgAmount: 100m))[2]);

    [Fact] public void Dim3_Hour14() => Assert.Equal(14f / 23f, Normalize(Make(requestedAt: "2026-01-12T14:30:00Z"))[3], 4);
    [Fact] public void Dim3_Midnight() => Assert.Equal(0f, Normalize(Make(requestedAt: "2026-01-12T00:00:00Z"))[3], 4);

    [Fact] public void Dim4_Monday() => Assert.Equal(0f, Normalize(Make(requestedAt: "2026-01-12T00:00:00Z"))[4]);
    [Fact] public void Dim4_Sunday() => Assert.Equal(1f, Normalize(Make(requestedAt: "2026-01-11T00:00:00Z"))[4]);

    [Fact]
    public void Dim5_6_NullLastTx_ReturnsMinus1()
    {
        var vec = Normalize(Make(lastTransaction: null));
        Assert.Equal(-1f, vec[5]);
        Assert.Equal(-1f, vec[6]);
    }

    [Fact]
    public void Dim5_90MinutesSinceLastTx()
    {
        var req = Make(
            requestedAt: "2026-01-12T15:00:00Z",
            lastTransaction: new LastTransactionData
            {
                Timestamp = DateTimeOffset.Parse("2026-01-12T13:30:00Z"),
                KmFromCurrent = 0f
            }
        );
        Assert.Equal(90f / 1440f, Normalize(req)[5], 4);
    }

    [Fact]
    public void Dim6_500km()
    {
        var req = Make(
            lastTransaction: new LastTransactionData
            {
                Timestamp = DateTimeOffset.UtcNow.AddHours(-1),
                KmFromCurrent = 500f
            }
        );
        Assert.Equal(0.5f, Normalize(req)[6], 4);
    }

    [Fact] public void Dim7_50km() => Assert.Equal(0.05f, Normalize(Make(kmFromHome: 50f))[7], 4);

    [Fact] public void Dim8_10Of20() => Assert.Equal(0.5f, Normalize(Make(txCount24h: 10))[8], 4);

    [Fact] public void Dim9_Online() => Assert.Equal(1f, Normalize(Make(isOnline: true))[9]);
    [Fact] public void Dim9_NotOnline() => Assert.Equal(0f, Normalize(Make(isOnline: false))[9]);
    [Fact] public void Dim10_CardPresent() => Assert.Equal(1f, Normalize(Make(cardPresent: true))[10]);

    [Fact]
    public void Dim11_KnownMerchant_ReturnsZero()
    {
        var req = Make(merchantId: "MERC-1", knownMerchants: ["MERC-1", "MERC-2"]);
        Assert.Equal(0f, Normalize(req)[11]);
    }

    [Fact]
    public void Dim11_UnknownMerchant_ReturnsOne()
    {
        var req = Make(merchantId: "MERC-99", knownMerchants: ["MERC-1"]);
        Assert.Equal(1f, Normalize(req)[11]);
    }

    [Fact]
    public void Dim12_KnownMcc_ReturnsLookupValue()
    {
        var mcc = new Dictionary<string, float> { ["5411"] = 0.15f };
        Assert.Equal(0.15f, Normalize(Make(mcc: "5411"), mcc)[12], 4);
    }

    [Fact]
    public void Dim12_UnknownMcc_ReturnsDefault05()
    {
        Assert.Equal(0.5f, Normalize(Make(mcc: "9999"), [])[12]);
    }

    [Fact] public void Dim13_2000() => Assert.Equal(0.2f, Normalize(Make(merchantAvgAmount: 2000m))[13], 4);
}
