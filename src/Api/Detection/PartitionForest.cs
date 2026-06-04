namespace RinhaBackend.Detection;

// M9: uma árvore k-d com poda por "caixa" (bounding box) DENTRO de cada gaveta.
// O M7/M8 varriam a gaveta inteira; aqui a gaveta vira uma árvore: cada nó guarda o
// min/max de cada dimensão dos vetores ali. Na busca, calculo a menor distância possível
// do query até a caixa do nó (lower bound); se já é pior que o 5º melhor atual, pulo a
// subárvore inteira. Isso limita o pior caso (gaveta de 300k não vira mais scan de 300k).
//
// Construída em memória no startup a partir dos vetores agrupados (sem mudar index.bin).
// As caixas são pré-calculadas e guardadas; a busca só lê os vetores nas folhas (via _order),
// preservando o mmap como fonte da verdade.
public sealed class PartitionForest
{
    public const int Dims = 14;
    public const int K = 5;
    public const int LeafSize = 56;

    private readonly int[] _order;       // permutação: posição-na-folha → índice no array agrupado
    private readonly int[] _left;        // filho esquerdo (-1 em folha)
    private readonly int[] _right;       // filho direito (-1 em folha)
    private readonly int[] _leafStart;   // início no _order (só folha)
    private readonly int[] _leafLen;     // qtd na folha (só folha)
    private readonly byte[] _boxMin;     // Dims bytes por nó (caixa)
    private readonly byte[] _boxMax;     // Dims bytes por nó
    private readonly int[] _roots;       // raiz de cada gaveta (-1 se vazia)

    private PartitionForest(int[] order, int[] left, int[] right, int[] leafStart, int[] leafLen,
        byte[] boxMin, byte[] boxMax, int[] roots)
    {
        _order = order; _left = left; _right = right;
        _leafStart = leafStart; _leafLen = leafLen;
        _boxMin = boxMin; _boxMax = boxMax; _roots = roots;
    }

    public static PartitionForest Build(ReadOnlySpan<byte> vectors, ReadOnlySpan<int> offsets, int numParts)
    {
        int count = offsets[numParts];
        var order = new int[count];
        for (int i = 0; i < count; i++) order[i] = i;

        var left = new List<int>();
        var right = new List<int>();
        var leafStart = new List<int>();
        var leafLen = new List<int>();
        var boxMin = new List<byte>();
        var boxMax = new List<byte>();
        var roots = new int[numParts];

        var scratch = new int[count];          // buffer reusável do counting sort
        var b = new Builder(vectors, order, scratch, left, right, leafStart, leafLen, boxMin, boxMax);
        for (int p = 0; p < numParts; p++)
        {
            int s = offsets[p], e = offsets[p + 1];
            roots[p] = e > s ? b.BuildNode(s, e) : -1;
        }

        return new PartitionForest(
            order, left.ToArray(), right.ToArray(), leafStart.ToArray(), leafLen.ToArray(),
            boxMin.ToArray(), boxMax.ToArray(), roots);
    }

    private readonly ref struct Builder
    {
        private readonly ReadOnlySpan<byte> _vectors;
        private readonly int[] _order;
        private readonly int[] _scratch;
        private readonly List<int> _left, _right, _leafStart, _leafLen;
        private readonly List<byte> _boxMin, _boxMax;

        public Builder(ReadOnlySpan<byte> vectors, int[] order, int[] scratch,
            List<int> left, List<int> right, List<int> leafStart, List<int> leafLen,
            List<byte> boxMin, List<byte> boxMax)
        {
            _vectors = vectors; _order = order; _scratch = scratch;
            _left = left; _right = right; _leafStart = leafStart; _leafLen = leafLen;
            _boxMin = boxMin; _boxMax = boxMax;
        }

        public int BuildNode(int lo, int hi)
        {
            Span<byte> min = stackalloc byte[Dims];
            Span<byte> max = stackalloc byte[Dims];
            min.Fill(255); max.Fill(0);
            for (int i = lo; i < hi; i++)
            {
                var v = _vectors.Slice(_order[i] * Dims, Dims);
                for (int d = 0; d < Dims; d++)
                {
                    if (v[d] < min[d]) min[d] = v[d];
                    if (v[d] > max[d]) max[d] = v[d];
                }
            }

            int node = _left.Count;
            _left.Add(-1); _right.Add(-1); _leafStart.Add(0); _leafLen.Add(0);
            for (int d = 0; d < Dims; d++) { _boxMin.Add(min[d]); _boxMax.Add(max[d]); }

            int len = hi - lo;
            if (len <= LeafSize)
            {
                _leafStart[node] = lo;
                _leafLen[node] = len;
                return node;
            }

            // corta pela dimensão mais larga, na mediana.
            int splitDim = 0, bestWidth = -1;
            for (int d = 0; d < Dims; d++)
            {
                int w = max[d] - min[d];
                if (w > bestWidth) { bestWidth = w; splitDim = d; }
            }
            CountingSort(lo, hi, splitDim);

            int mid = (lo + hi) / 2;
            int l = BuildNode(lo, mid);
            int r = BuildNode(mid, hi);
            _left[node] = l;
            _right[node] = r;
            return node;
        }

        // ordena order[lo..hi) pela dim escolhida via counting sort (chaves são bytes 0..255).
        // O(len + 256), estável, imune a duplicatas — sem risco quadrático.
        private void CountingSort(int lo, int hi, int dim)
        {
            Span<int> counts = stackalloc int[257];
            counts.Clear();
            for (int i = lo; i < hi; i++) counts[_vectors[_order[i] * Dims + dim] + 1]++;
            for (int c = 0; c < 256; c++) counts[c + 1] += counts[c];
            for (int i = lo; i < hi; i++)
            {
                int idx = _order[i];
                int key = _vectors[idx * Dims + dim];
                _scratch[counts[key]++] = idx;
            }
            int len = hi - lo;
            Array.Copy(_scratch, 0, _order, lo, len);
        }
    }

    // Busca top-5 na gaveta `partition` com poda por caixa. Mesmo desempate (dist, idx) do
    // PartitionedIndex, então o conjunto dos 5 é idêntico ao scan plano.
    public float Search(ReadOnlySpan<byte> vectors, ReadOnlySpan<byte> labels, int partition, ReadOnlySpan<byte> query)
    {
        int root = _roots[partition];
        if (root < 0) return 0f;

        Span<int> bestDist = stackalloc int[K];
        Span<int> bestIdx = stackalloc int[K];
        bestDist.Fill(int.MaxValue);
        bestIdx.Fill(int.MaxValue);
        int filled = 0, worst = 0;

        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = root;

        while (sp > 0)
        {
            int node = stack[--sp];
            int threshold = filled < K ? int.MaxValue : bestDist[worst];
            if (LowerBound(query, node) >= threshold) continue;

            int l = _left[node];
            if (l < 0)
            {
                int start = _leafStart[node], len = _leafLen[node];
                for (int i = start; i < start + len; i++)
                {
                    int idx = _order[i];
                    int d2 = SquaredDistance(query, vectors.Slice(idx * Dims, Dims));
                    if (filled < K)
                    {
                        bestDist[filled] = d2; bestIdx[filled] = idx; filled++;
                        if (filled == K) worst = FindWorst(bestDist, bestIdx);
                    }
                    else if (d2 < bestDist[worst] || (d2 == bestDist[worst] && idx < bestIdx[worst]))
                    {
                        bestDist[worst] = d2; bestIdx[worst] = idx; worst = FindWorst(bestDist, bestIdx);
                    }
                }
                continue;
            }

            int r = _right[node];
            // desce no filho mais próximo primeiro; o mais distante fica no topo da pilha e
            // é podado na hora do pop se a caixa dele não puder bater o 5º melhor.
            int lb = LowerBound(query, l);
            int rb = LowerBound(query, r);
            if (lb <= rb) { stack[sp++] = r; stack[sp++] = l; }
            else { stack[sp++] = l; stack[sp++] = r; }
        }

        int frauds = 0;
        for (int j = 0; j < filled; j++) if (labels[bestIdx[j]] != 0) frauds++;
        return frauds / (float)K;
    }

    // menor distância² possível do query até a caixa do nó (0 na dim se o query está dentro).
    private int LowerBound(ReadOnlySpan<byte> query, int node)
    {
        int baseOff = node * Dims;
        int sum = 0;
        for (int d = 0; d < Dims; d++)
        {
            int q = query[d];
            int lo = _boxMin[baseOff + d];
            int hi = _boxMax[baseOff + d];
            int diff = q < lo ? lo - q : (q > hi ? q - hi : 0);
            sum += diff * diff;
        }
        return sum;
    }

    private static int FindWorst(ReadOnlySpan<int> dist, ReadOnlySpan<int> idx)
    {
        int w = 0;
        for (int j = 1; j < dist.Length; j++)
            if (dist[j] > dist[w] || (dist[j] == dist[w] && idx[j] > idx[w])) w = j;
        return w;
    }

    private static int SquaredDistance(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int sum = 0;
        for (int d = 0; d < Dims; d++) { int df = a[d] - b[d]; sum += df * df; }
        return sum;
    }
}
