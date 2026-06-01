namespace RinhaBackend.Detection;

// M7: busca dentro de UMA gaveta (versão simples/aproximada). Escaneia o range da partição
// e mantém os K mais próximos com desempate lexicográfico (dist, pos) — idêntico a um
// brute-force restrito àquela partição.
public static class PartitionedIndex
{
    public const int K = 5;
    public const int Dims = 14;

    public static float Search(
        ReadOnlySpan<byte> vectors, ReadOnlySpan<byte> labels,
        ReadOnlySpan<int> offsets, int key, ReadOnlySpan<byte> query)
    {
        int start = offsets[key];
        int end = offsets[key + 1];

        Span<int> bestDist = stackalloc int[K];
        Span<int> bestPos = stackalloc int[K];
        int filled = 0, worst = 0;

        for (int i = start; i < end; i++)
        {
            int d2 = SquaredDistance(query, vectors.Slice(i * Dims, Dims));
            if (filled < K)
            {
                bestDist[filled] = d2; bestPos[filled] = i; filled++;
                if (filled == K) worst = FindWorst(bestDist, bestPos);
            }
            else if (d2 < bestDist[worst] || (d2 == bestDist[worst] && i < bestPos[worst]))
            {
                bestDist[worst] = d2; bestPos[worst] = i; worst = FindWorst(bestDist, bestPos);
            }
        }

        int frauds = 0;
        for (int j = 0; j < filled; j++)
            if (labels[bestPos[j]] != 0) frauds++;
        return frauds / (float)K;
    }

    private static int FindWorst(ReadOnlySpan<int> dist, ReadOnlySpan<int> pos)
    {
        int w = 0;
        for (int j = 1; j < dist.Length; j++)
            if (dist[j] > dist[w] || (dist[j] == dist[w] && pos[j] > pos[w])) w = j;
        return w;
    }

    private static int SquaredDistance(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int sum = 0;
        for (int d = 0; d < Dims; d++) { int df = a[d] - b[d]; sum += df * df; }
        return sum;
    }
}
