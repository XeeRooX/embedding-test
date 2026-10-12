using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using EmbedderApi;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
var settings = AppSettings.FromConfiguration(builder.Configuration);

builder.Services.AddSingleton(settings);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = settings.MaxUploadBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = settings.MaxUploadBytes);

builder.Services.AddHttpClient<OllamaClient>(c =>
{
    c.BaseAddress = new Uri(settings.OllamaUrl);
    c.Timeout = TimeSpan.FromMinutes(30); // CPU-инференс больших батчей бывает долгим
    if (settings.OllamaApiKey is not null)
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.OllamaApiKey);
});
builder.Services.AddHttpClient<QdrantClient>(c =>
{
    c.BaseAddress = new Uri(settings.QdrantUrl);
    c.Timeout = TimeSpan.FromMinutes(5);
    if (settings.QdrantApiKey is not null)
        c.DefaultRequestHeaders.Add("api-key", settings.QdrantApiKey);
});
builder.Services.AddTransient<IngestService>();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    model = settings.EmbedModel,
    collection = settings.CollectionName,
    numThread = settings.NumThread,
}));

// Обычный текст -> эмбеддинги -> Qdrant.
// Принимает text/plain или JSON {"text": "..."}.
app.MapPost("/embed", async (HttpRequest request, IngestService ingest, CancellationToken ct) =>
{
    string text;
    if (request.HasJsonContentType())
    {
        var body = await request.ReadFromJsonAsync<EmbedRequest>(ct);
        text = body?.Text ?? "";
    }
    else
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        text = await reader.ReadToEndAsync(ct);
    }

    if (string.IsNullOrWhiteSpace(text))
        return Results.BadRequest(new { error = "Пустой текст" });

    var result = await ingest.IngestAsync(text, "api:embed", ct);
    return Results.Ok(new
    {
        model = settings.EmbedModel,
        tokens = result.Tokens,
        chunks = result.Chunks,
        embeddingMs = result.EmbeddingMs,
        upsertMs = result.UpsertMs,
        totalMs = result.TotalMs,
    });
});

// Бенчмарк: несколько .txt файлов (multipart, поле "files") -> время на каждый файл.
app.MapPost("/benchmark", async (
    [FromForm] IFormFileCollection files,
    IngestService ingest,
    CancellationToken ct,
    bool warmup = true) =>
{
    if (files.Count == 0)
        return Results.BadRequest(new { error = "Передайте один или несколько файлов в поле 'files'" });

    if (warmup) await ingest.WarmupAsync(ct);

    var overall = Stopwatch.StartNew();
    var results = new List<IngestResult>();
    foreach (var file in files)
    {
        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ct);
        results.Add(await ingest.IngestAsync(text, file.FileName, ct));
    }
    overall.Stop();

    return Results.Ok(new
    {
        model = settings.EmbedModel,
        collection = settings.CollectionName,
        numThread = settings.NumThread,
        chunkSize = settings.ChunkSize,
        batchSize = settings.EmbedBatchSize,
        files = results.Select(r => new
        {
            file = r.Source,
            chars = r.Chars,
            chunks = r.Chunks,
            tokens = r.Tokens,
            embeddingMs = r.EmbeddingMs,
            upsertMs = r.UpsertMs,
            totalMs = r.TotalMs,
            tokensPerSec = r.EmbeddingMs > 0 ? Math.Round(r.Tokens / (r.EmbeddingMs / 1000), 1) : 0,
        }),
        totalTokens = results.Sum(r => r.Tokens),
        totalMs = Math.Round(overall.Elapsed.TotalMilliseconds, 2),
    });
}).DisableAntiforgery();

app.Run();

record EmbedRequest(string? Text);
