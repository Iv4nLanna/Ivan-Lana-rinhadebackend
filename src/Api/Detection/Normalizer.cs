namespace RinhaBackend.Detection;

public static class Normalizer
{
    private static float Clamp(float x) => MathF.Max(0f, MathF.Min(1f, x));

    public static float[] Normalize(
        TransactionRequest req,
        Dictionary<string, float> mccRisk,
        HashSet<string> knownMerchants)
    {
        var vec = new float[14]; // M1: heap alloc — será stackalloc em M4

        vec[0] = Clamp((float)req.Transaction.Amount / 10_000f);

        vec[1] = Clamp(req.Transaction.Installments / 12f);

        vec[2] = req.Customer.AvgAmount == 0m
            ? 0f
            : Clamp((float)(req.Transaction.Amount / req.Customer.AvgAmount) / 10f);

        vec[3] = req.Transaction.RequestedAt.Hour / 23f;

        int dotNetDow = (int)req.Transaction.RequestedAt.DayOfWeek;
        int mondayBasedDow = (dotNetDow + 6) % 7;
        vec[4] = mondayBasedDow / 6f;

        if (req.LastTransaction is null)
        {
            vec[5] = -1f;
            vec[6] = -1f;
        }
        else
        {
            float minutes = (float)(req.Transaction.RequestedAt - req.LastTransaction.Timestamp).TotalMinutes;
            vec[5] = Clamp(minutes / 1_440f);
            vec[6] = Clamp(req.LastTransaction.KmFromCurrent / 1_000f);
        }

        vec[7] = Clamp(req.Terminal.KmFromHome / 1_000f);

        vec[8] = Clamp(req.Customer.TxCount24h / 20f);

        vec[9] = req.Terminal.IsOnline ? 1f : 0f;

        vec[10] = req.Terminal.CardPresent ? 1f : 0f;

        vec[11] = knownMerchants.Contains(req.Merchant.Id) ? 0f : 1f;

        vec[12] = mccRisk.TryGetValue(req.Merchant.Mcc, out float risk) ? risk : 0.5f;

        vec[13] = Clamp((float)req.Merchant.AvgAmount / 10_000f);

        return vec;
    }
}
