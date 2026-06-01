namespace RinhaBackend.Detection;

// M6: KD-Tree implícita sobre vetores quantizados (byte). A estrutura é o range [lo,hi)
// com axis = depth % Dims e o nó na mediana (mid). Build reordena os vetores+labels;
// a busca poda e mantém os K mais próximos com desempate lexicográfico (dist, pos),
// o que torna o resultado independente da ordem de visita = igual ao brute-force.
public static class KdTree
{
    public const int Dims = 14;
    public const int K = 5;

    public static (byte[] vectors, byte[] labels) Build(
        ReadOnlySpan<byte> vectors, ReadOnlySpan<byte> labels, int count)
    {
        var perm = new int[count];
        for (int i = 0; i < count; i++) perm[i] = i;
        BuildRange(vectors, perm, 0, count, 0);

        var outV = new byte[count * Dims];
        var outL = new byte[count];
        for (int i = 0; i < count; i++)
        {
            vectors.Slice(perm[i] * Dims, Dims).CopyTo(outV.AsSpan(i * Dims, Dims));
            outL[i] = labels[perm[i]];
        }
        return (outV, outL);
    }

    private static void BuildRange(ReadOnlySpan<byte> vectors, int[] perm, int lo, int hi, int depth)
    {
        if (hi - lo <= 1) return;
        int axis = depth % Dims;
        int mid = (lo + hi) / 2;
        QuickSelect(vectors, perm, lo, hi, mid, axis);
        BuildRange(vectors, perm, lo, mid, depth + 1);
        BuildRange(vectors, perm, mid + 1, hi, depth + 1);
    }

    private static void QuickSelect(ReadOnlySpan<byte> vectors, int[] perm, int lo, int hi, int k, int axis)
    {
        int l = lo, r = hi - 1;
        while (l < r)
        {
            byte pivot = vectors[perm[(l + r) / 2] * Dims + axis];
            int i = l, j = r;
            while (i <= j)
            {
                while (vectors[perm[i] * Dims + axis] < pivot) i++;
                while (vectors[perm[j] * Dims + axis] > pivot) j--;
                if (i <= j) { (perm[i], perm[j]) = (perm[j], perm[i]); i++; j--; }
            }
            if (k <= j) r = j;
            else if (k >= i) l = i;
            else break;
        }
    }

    public static float Search(ReadOnlySpan<byte> vectors, ReadOnlySpan<byte> labels, int count, ReadOnlySpan<byte> query)
    {
        Span<int> pos = stackalloc int[K];
        int filled = SearchTopKInto(vectors, count, query, pos);
        int frauds = 0;
        for (int j = 0; j < filled; j++)
            if (labels[pos[j]] != 0) frauds++;
        return frauds / (float)K;
    }

    public static int[] SearchTopK(ReadOnlySpan<byte> vectors, int count, ReadOnlySpan<byte> query)
    {
        Span<int> pos = stackalloc int[K];
        int filled = SearchTopKInto(vectors, count, query, pos);
        var res = pos.Slice(0, filled).ToArray();
        Array.Sort(res);
        return res;
    }

    private static int SearchTopKInto(ReadOnlySpan<byte> vectors, int count, ReadOnlySpan<byte> query, Span<int> outPos)
    {
        Span<int> bestDist = stackalloc int[K];
        Span<int> bestPos = stackalloc int[K];
        int filled = 0, worst = 0;
        SearchRange(vectors, query, 0, count, 0, bestDist, bestPos, ref filled, ref worst);
        for (int j = 0; j < filled; j++) outPos[j] = bestPos[j];
        return filled;
    }

    private static void SearchRange(
        ReadOnlySpan<byte> vectors, ReadOnlySpan<byte> query, int lo, int hi, int depth,
        Span<int> bestDist, Span<int> bestPos, ref int filled, ref int worst)
    {
        if (hi - lo <= 0) return;
        int axis = depth % Dims;
        int mid = (lo + hi) / 2;

        int d2 = SquaredDistance(query, vectors.Slice(mid * Dims, Dims));
        Consider(mid, d2, bestDist, bestPos, ref filled, ref worst);

        int diff = query[axis] - vectors[mid * Dims + axis];
        int nearLo, nearHi, farLo, farHi;
        if (diff < 0) { nearLo = lo; nearHi = mid; farLo = mid + 1; farHi = hi; }
        else { nearLo = mid + 1; nearHi = hi; farLo = lo; farHi = mid; }

        SearchRange(vectors, query, nearLo, nearHi, depth + 1, bestDist, bestPos, ref filled, ref worst);

        if (filled < K || diff * diff <= bestDist[worst])
            SearchRange(vectors, query, farLo, farHi, depth + 1, bestDist, bestPos, ref filled, ref worst);
    }

    private static void Consider(int pos, int dist, Span<int> bestDist, Span<int> bestPos, ref int filled, ref int worst)
    {
        if (filled < K)
        {
            bestDist[filled] = dist; bestPos[filled] = pos; filled++;
            if (filled == K) worst = FindWorst(bestDist, bestPos);
        }
        else if (dist < bestDist[worst] || (dist == bestDist[worst] && pos < bestPos[worst]))
        {
            bestDist[worst] = dist; bestPos[worst] = pos; worst = FindWorst(bestDist, bestPos);
        }
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
