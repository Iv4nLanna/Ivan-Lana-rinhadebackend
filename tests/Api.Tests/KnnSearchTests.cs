using RinhaBackend.Detection;
using Xunit;

namespace RinhaBackend.Tests;

public class KnnSearchTests
{
    private static (float[,] vectors, bool[] labels) MakeDataset(float[] values, bool[] frauds)
    {
        int count = values.Length;
        var vectors = new float[count, 14];
        for (int i = 0; i < count; i++)
            for (int d = 0; d < 14; d++)
                vectors[i, d] = values[i];
        return (vectors, frauds);
    }

    [Fact]
    public void Search_AllLegit_ReturnsZero()
    {
        var (vectors, labels) = MakeDataset(
            values: [0.1f, 0.2f, 0.3f, 0.4f, 0.5f],
            frauds: [false, false, false, false, false]
        );
        var query = new float[14];

        float score = KnnSearch.Search(query, vectors, labels, 5);

        Assert.Equal(0f, score);
    }

    [Fact]
    public void Search_AllFraud_ReturnsOne()
    {
        var (vectors, labels) = MakeDataset(
            values: [0.1f, 0.2f, 0.3f, 0.4f, 0.5f],
            frauds: [true, true, true, true, true]
        );
        var query = new float[14];

        float score = KnnSearch.Search(query, vectors, labels, 5);

        Assert.Equal(1f, score);
    }

    [Fact]
    public void Search_ThreeOfFiveFraud_ReturnsPointSix()
    {
        var vectors = new float[7, 14];
        var labels = new bool[7];

        for (int d = 0; d < 14; d++) { vectors[0, d] = 0.01f; vectors[1, d] = 0.02f; vectors[2, d] = 0.03f; }
        labels[0] = labels[1] = labels[2] = true;

        for (int d = 0; d < 14; d++) { vectors[3, d] = 0.5f; vectors[4, d] = 0.6f; }
        labels[3] = labels[4] = false;

        for (int d = 0; d < 14; d++) { vectors[5, d] = 0.7f; vectors[6, d] = 0.8f; }
        labels[5] = true; labels[6] = false;

        var query = new float[14];

        float score = KnnSearch.Search(query, vectors, labels, 7);

        Assert.Equal(0.6f, score, 4);
    }

    [Fact]
    public void Search_ApprovalThreshold_BelowPointSixApproved()
    {
        var (vectors, labels) = MakeDataset(
            values: [0.1f, 0.2f, 0.3f, 0.4f, 0.5f],
            frauds: [true, true, false, false, false]
        );
        var query = new float[14];

        float score = KnnSearch.Search(query, vectors, labels, 5);

        Assert.Equal(0.4f, score, 4);
        Assert.True(score < 0.6f);
    }
}
