using RinhaBackend;
using RinhaBackend.Data;
using RinhaBackend.Detection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ReferenceDataset>();

var app = builder.Build();

var dataset = app.Services.GetRequiredService<ReferenceDataset>();
var dataPath = builder.Configuration["DataPath"]
               ?? Environment.GetEnvironmentVariable("DATA_PATH")
               ?? "/app/data";

_ = Task.Run(async () =>
{
    try { await dataset.LoadAsync(dataPath); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[ERRO] Falha ao carregar dataset: {ex.Message}");
    }
});

app.MapGet("/ready", (ReferenceDataset ds) =>
    ds.IsReady ? Results.Ok("ready") : Results.StatusCode(503));

app.MapPost("/fraud-score", async (HttpRequest request, ReferenceDataset ds) =>
{
    TransactionRequest? req;
    try
    {
        req = await request.ReadFromJsonAsync<TransactionRequest>();
        if (req is null) return Results.BadRequest("body vazio");
    }
    catch (Exception)
    {
        return Results.BadRequest("JSON inválido");
    }

    if (!ds.IsReady)
        return Results.StatusCode(503);

    var knownMerchants = new HashSet<string>(
        req.Customer.KnownMerchants,
        StringComparer.OrdinalIgnoreCase
    );

    var vector = Normalizer.Normalize(req, ds.MccRisk, knownMerchants);
    var fraudScore = KnnSearch.Search(vector, ds);

    return Results.Ok(new FraudScoreResponse
    {
        Approved = fraudScore < 0.6f,
        FraudScore = fraudScore
    });
});

app.Run();

public partial class Program { }
