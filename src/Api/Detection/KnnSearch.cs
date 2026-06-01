using RinhaBackend.Data;

namespace RinhaBackend.Detection;

public static class KnnSearch
{
    // M1: brute force naive com nova assinatura (M3)
    // Alocação de ~12 MB por request (new float[count]) será eliminada em M4
    public static float Search(float[] query, ReferenceDataset ds)
    {
        int count = ds.Count;
        var distances = new float[count];

        for (int i = 0; i < count; i++)
        {
            var vec = ds.GetVector(i);
            float sum = 0f;
            for (int d = 0; d < 14; d++)
            {
                float diff = query[d] - vec[d];
                sum += diff * diff;
            }
            distances[i] = sum;
        }

        var top5 = distances
            .Select((dist, idx) => (dist, idx))
            .OrderBy(x => x.dist)
            .Take(5)
            .ToArray();

        int fraudCount = top5.Count(x => ds.GetLabel(x.idx));
        return fraudCount / 5f;
    }
}
