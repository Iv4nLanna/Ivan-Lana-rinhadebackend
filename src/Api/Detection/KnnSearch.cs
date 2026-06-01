namespace RinhaBackend.Detection;

public static class KnnSearch
{
    // M1: brute force naive
    // Allocates float[count] for all distances — intentionally to measure impact in M2
    public static float Search(float[] query, float[,] vectors, bool[] labels, int count)
    {
        // ALLOCATION M1: ~12 MB for 3M vectors — eliminated in M2
        var distances = new float[count];

        for (int i = 0; i < count; i++)
        {
            float sum = 0f;
            for (int d = 0; d < 14; d++)
            {
                float diff = query[d] - vectors[i, d];
                sum += diff * diff;
            }
            distances[i] = sum;
        }

        // LINQ sort — readable but slow, replaced in M2
        var top5 = distances
            .Select((dist, idx) => (dist, idx))
            .OrderBy(x => x.dist)
            .Take(5)
            .ToArray();

        int fraudCount = top5.Count(x => labels[x.idx]);
        return fraudCount / 5f;
    }
}
