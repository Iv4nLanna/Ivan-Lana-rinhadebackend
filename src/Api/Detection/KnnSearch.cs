using System.Runtime.Intrinsics;
using RinhaBackend.Data;

namespace RinhaBackend.Detection;

public static class KnnSearch
{
    private const int K = 5;

    // M4: Top-K em uma passada O(n·k). Mantém os K vizinhos mais próximos num
    // buffer stackalloc de K — sem `new float[count]` (~12 MB/req) e sem `.OrderBy()`
    // de 3M itens (O(n log n)). Para k=5 fixo, a inserção limitada supera tanto um heap quanto o sort completo.
    // M5: a distância² individual é calculada com SIMD (ver SquaredDistance). O vetor SIMD da
    // query é construído UMA vez fora do loop (loop-invariant) — não 3M vezes por request.
    public static float Search(ReadOnlySpan<float> query, ReferenceDataset ds)
    {
        int count = ds.Count;

        // Os K menores: distância² e índice original. stackalloc → zero heap.
        Span<float> bestDist = stackalloc float[K];
        Span<int> bestIdx = stackalloc int[K];
        int filled = 0;
        int worst = 0; // slot a descartar: maior distância (empate → maior índice)

        // Loop-invariant: a query não muda; carrega seu vetor SIMD (dims 0-7) uma vez só.
        // Quando não há aceleração de hardware, fica `default` e o caminho escalar o ignora.
        Vector256<float> vq = Vector256.IsHardwareAccelerated ? Vector256.Create(query) : default;

        for (int i = 0; i < count; i++)
        {
            var vec = ds.GetVector(i);
            float sum = SquaredDistance(vq, query, vec);

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

    // M5: distância² (sem sqrt). SIMD cobre as dims 0-7 (8 floats por instrução) usando o
    // vetor da query pré-construído (vq); as dims 8-13 ("sobra") são escalares. Fallback
    // escalar puro quando não há AVX.
    // Nota: a soma SIMD pode diferir do escalar em 1 ULP (soma de float não é associativa) —
    // mesma distância matemática, ranking dos 5 vizinhos inalterado.
    private static float SquaredDistance(Vector256<float> vq, ReadOnlySpan<float> query, ReadOnlySpan<float> vec)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            // dims 0-7 (Create lê os 8 primeiros; vec tem 14 ≥ 8)
            var vb = Vector256.Create(vec);
            var diff = vq - vb;
            float sum = Vector256.Sum(diff * diff);

            // sobra: dims 8-13 escalar
            for (int d = 8; d < 14; d++)
            {
                float df = query[d] - vec[d];
                sum += df * df;
            }
            return sum;
        }

        // fallback escalar (CPU sem AVX)
        float s = 0f;
        for (int d = 0; d < 14; d++)
        {
            float df = query[d] - vec[d];
            s += df * df;
        }
        return s;
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
