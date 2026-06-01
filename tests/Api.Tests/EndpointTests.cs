using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RinhaBackend.Tests;

public class EndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _dataDir;

    public EndpointTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(Path.Combine(_dataDir, "mcc_risk.json"), "{}");

        var rng = new Random(1);
        var sb = new System.Text.StringBuilder("[");
        for (int i = 0; i < 16; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"vector\":[");
            for (int d = 0; d < 14; d++)
            {
                if (d > 0) sb.Append(',');
                sb.Append(rng.NextDouble().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append("],\"label\":\"").Append(i % 2 == 0 ? "legit" : "fraud").Append("\"}");
        }
        sb.Append(']');
        File.WriteAllText(Path.Combine(_dataDir, "example-references.json"), sb.ToString());

        _client = factory
            .WithWebHostBuilder(b => b.UseSetting("DataPath", _dataDir))
            .CreateClient();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task FraudScore_InvalidJson_Returns400()
    {
        var response = await _client.PostAsync(
            "/fraud-score",
            new StringContent("not json", System.Text.Encoding.UTF8, "application/json")
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FraudScore_EmptyBody_Returns400()
    {
        var response = await _client.PostAsync(
            "/fraud-score",
            new StringContent("", System.Text.Encoding.UTF8, "application/json")
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FraudScore_ValidPayload_Returns200WithExpectedFields()
    {
        await WaitForReady(TimeSpan.FromSeconds(10));

        var payload = new
        {
            id = "test-1",
            transaction = new { amount = 150.00m, installments = 1, requested_at = "2026-01-15T14:30:00Z" },
            customer = new { avg_amount = 120.00m, tx_count_24h = 3, known_merchants = new[] { "MERC-1" } },
            merchant = new { id = "MERC-99", mcc = "5411", avg_amount = 200.00m },
            terminal = new { is_online = false, card_present = true, km_from_home = 2.5f },
            last_transaction = new { timestamp = "2026-01-15T12:00:00Z", km_from_current = 1.2f }
        };

        var response = await _client.PostAsJsonAsync("/fraud-score", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        Assert.NotNull(body);
        Assert.True(body.ContainsKey("approved"));
        Assert.True(body.ContainsKey("fraud_score"));

        float score = body["fraud_score"].GetSingle();
        Assert.InRange(score, 0f, 1f);
    }

    [Fact]
    public async Task FraudScore_NullLastTransaction_Returns200()
    {
        await WaitForReady(TimeSpan.FromSeconds(10));

        var payload = new
        {
            id = "test-null-last",
            transaction = new { amount = 100m, installments = 1, requested_at = "2026-01-15T10:00:00Z" },
            customer = new { avg_amount = 100m, tx_count_24h = 1, known_merchants = Array.Empty<string>() },
            merchant = new { id = "MERC-1", mcc = "5999", avg_amount = 100m },
            terminal = new { is_online = true, card_present = false, km_from_home = 0f },
            last_transaction = (object?)null
        };

        var response = await _client.PostAsJsonAsync("/fraud-score", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task WaitForReady(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var r = await _client.GetAsync("/ready");
            if (r.IsSuccessStatusCode) return;
            await Task.Delay(200);
        }
        throw new TimeoutException("API não ficou pronta a tempo");
    }
}
