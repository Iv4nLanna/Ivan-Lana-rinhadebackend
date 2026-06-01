using RinhaBackend.Data;

namespace RinhaBackend.Detection;

public static class KnnSearch
{
    private const int K = 5;

    // M4: Top-K em uma passada O(n·k). Mantém os K vizinhos mais próximos num
    // buffer stackalloc de K — sem `new float[count]` (~12 MB/req) e sem `.OrderBy()`
    // de 3M itens (O(n log n)). Para k=5 fixo, a inserção limitada supera tanto um heap quanto o sort completo.
    public static float Search(ReadOnlySpan<float> query, ReferenceDataset ds)
    {
        int count = ds.Count;

        // Os K menores: distância² e índice original. stackalloc → zero heap.
        Span<float> bestDist = stackalloc float[K];
        Span<int> bestIdx = stackalloc int[K];
        int filled = 0;
        int worst = 0; // slot a descartar: maior distância (empate → maior índice)

        for (int i = 0; i < count; i++)
        {
            var vec = ds.GetVector(i);

            // distância² (sem sqrt — monotônica, mesmo ranking que a distância)
            float sum = 0f;
            for (int d = 0; d < 14; d++)
            {
                float diff = query[d] - vec[d];
                sum += diff * diff;
            }

            if (filled < K)
            {
                bestDist[filled] = sum;
                bestIdx[filled] = i;
                filled++;
                if (filled == K)
                    worst = FindWorst(bestDist, bestIdx);
            }
            // Comparação ESTRITA: empate não substitui → mantém o índice menor,
            // igual ao OrderBy().Take(5) estável.
            else if (sum < bestDist[worst])
            {
                bestDist[worst] = sum;
                bestIdx[worst] = i;
                worst = FindWorst(bestDist, bestIdx);
            }
        }

        int fraudCount = 0;
        for (int j = 0; j < filled; j++)
            if (ds.GetLabel(bestIdx[j]))
                fraudCount++;

        return fraudCount / (float)K;
    }

    // Slot a descartar entre os K: o de maior distância. Em empate de distância,
    // o de MAIOR índice original — replica o desempate estável do OrderBy (mantém
    // os índices menores quando vários vetores empatam na distância de corte).
    private static int FindWorst(ReadOnlySpan<float> dist, ReadOnlySpan<int> idx)
    {
        int worst = 0;
        for (int j = 1; j < dist.Length; j++)
        {
            if (dist[j] > dist[worst] ||
                (dist[j] == dist[worst] && idx[j] > idx[worst]))
                worst = j;
        }
        return worst;
    }
}
