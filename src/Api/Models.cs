using System.Text.Json.Serialization;

namespace RinhaBackend;

public class TransactionRequest
{
    public string Id { get; set; } = "";

    public TransactionData Transaction { get; set; } = null!;

    public CustomerData Customer { get; set; } = null!;

    public MerchantData Merchant { get; set; } = null!;

    public TerminalData Terminal { get; set; } = null!;

    [JsonPropertyName("last_transaction")]
    public LastTransactionData? LastTransaction { get; set; }
}

public class TransactionData
{
    public decimal Amount { get; set; }

    public int Installments { get; set; }

    [JsonPropertyName("requested_at")]
    public DateTimeOffset RequestedAt { get; set; }
}

public class CustomerData
{
    [JsonPropertyName("avg_amount")]
    public decimal AvgAmount { get; set; }

    [JsonPropertyName("tx_count_24h")]
    public int TxCount24h { get; set; }

    [JsonPropertyName("known_merchants")]
    public List<string> KnownMerchants { get; set; } = [];
}

public class MerchantData
{
    public string Id { get; set; } = "";

    public string Mcc { get; set; } = "";

    [JsonPropertyName("avg_amount")]
    public decimal AvgAmount { get; set; }
}

public class TerminalData
{
    [JsonPropertyName("is_online")]
    public bool IsOnline { get; set; }

    [JsonPropertyName("card_present")]
    public bool CardPresent { get; set; }

    [JsonPropertyName("km_from_home")]
    public float KmFromHome { get; set; }
}

public class LastTransactionData
{
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("km_from_current")]
    public float KmFromCurrent { get; set; }
}

public class FraudScoreResponse
{
    public bool Approved { get; set; }

    [JsonPropertyName("fraud_score")]
    public float FraudScore { get; set; }
}
