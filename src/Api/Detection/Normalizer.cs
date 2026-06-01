namespace RinhaBackend.Detection;

public static class Normalizer
{
    private static float Clamp(float x) => MathF.Max(0f, MathF.Min(1f, x));

    // M5: escreve no buffer fornecido pelo chamador (stackalloc) — sem `new float[14]` por request.
    public static void Normalize(
        TransactionRequest req,
        Dictionary<string, float> mccRisk,
        HashSet<string> knownMerchants,
        Span<float> dest)
    {
        dest[0] = Clamp((float)req.Transaction.Amount / 10_000f);

        dest[1] = Clamp(req.Transaction.Installments / 12f);

        dest[2] = req.Customer.AvgAmount == 0m
            ? 0f
            : Clamp((float)(req.Transaction.Amount / req.Customer.AvgAmount) / 10f);

        dest[3] = req.Transaction.RequestedAt.Hour / 23f;

        int dotNetDow = (int)req.Transaction.RequestedAt.DayOfWeek;
        int mondayBasedDow = (dotNetDow + 6) % 7;
        dest[4] = mondayBasedDow / 6f;

        if (req.LastTransaction is null)
        {
            dest[5] = -1f;
            dest[6] = -1f;
        }
        else
        {
            float minutes = (float)(req.Transaction.RequestedAt - req.LastTransaction.Timestamp).TotalMinutes;
            dest[5] = Clamp(minutes / 1_440f);
            dest[6] = Clamp(req.LastTransaction.KmFromCurrent / 1_000f);
        }

        dest[7] = Clamp(req.Terminal.KmFromHome / 1_000f);

        dest[8] = Clamp(req.Customer.TxCount24h / 20f);

        dest[9] = req.Terminal.IsOnline ? 1f : 0f;

        dest[10] = req.Terminal.CardPresent ? 1f : 0f;

        dest[11] = knownMerchants.Contains(req.Merchant.Id) ? 0f : 1f;

        dest[12] = mccRisk.TryGetValue(req.Merchant.Mcc, out float risk) ? risk : 0.5f;

        dest[13] = Clamp((float)req.Merchant.AvgAmount / 10_000f);
    }
}
