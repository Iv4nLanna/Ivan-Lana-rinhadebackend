using System.Diagnostics;

namespace RinhaBackend.Detection;

// M7: "número da gaveta" (0..31). 3 dims categóricas (binárias) + faixa de valor.
// dim 9 = is_online, dim 10 = card_present, dim 11 = merchant desconhecido (todas 0/1).
// dim 0 = amount, fatiado em 4 faixas por 3 cortes (quartis, calculados offline).
public static class PartitionKey
{
    public const int FlagCount = 8;       // 2³ combinações das 3 dims binárias
    public const int AmountBuckets = 4;   // 3 cortes
    public const int Count = FlagCount * AmountBuckets; // 32

    public static int Compute(ReadOnlySpan<float> v, ReadOnlySpan<float> cuts)
    {
        // cuts: AmountBuckets-1 cortes ORDENADOS ascendente (quartis do valor, do IndexBuilder).
        // A coupling com AmountBuckets garante que key ∈ [0, Count).
        Debug.Assert(cuts.Length == AmountBuckets - 1, "cuts deve ter AmountBuckets-1 elementos");

        int flags = (v[9] > 0.5f ? 1 : 0)
                  | (v[10] > 0.5f ? 2 : 0)
                  | (v[11] > 0.5f ? 4 : 0);

        int bucket = 0;
        for (int i = 0; i < cuts.Length; i++)
        {
            if (v[0] > cuts[i]) bucket++;
            else break;
        }

        return bucket * FlagCount + flags;
    }
}
