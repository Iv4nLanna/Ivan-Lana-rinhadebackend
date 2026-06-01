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

    // M5: stackalloc não pode existir num método async (Span não sobrevive ao await),
    // então o trabalho com o vetor na stack acontece no método síncrono ComputeScore.
    return ComputeScore(req, ds);
});

app.Run();

// M5: síncrono de propósito — aqui o vetor de query vive na stack (stackalloc).
static IResult ComputeScore(TransactionRequest req, ReferenceDataset ds)
{
    var knownMerchants = new HashSet<string>(
        req.Customer.KnownMerchants,
        StringComparer.OrdinalIgnoreCase
    );

    Span<float> vector = stackalloc float[14];
    Normalizer.Normalize(req, ds.MccRisk, knownMerchants, vector);
    float fraudScore = KnnSearch.Search(vector, ds);

    return Results.Ok(new FraudScoreResponse
    {
        Approved = fraudScore < 0.6f,
        FraudScore = fraudScore
    });
}

public partial class Program { }
