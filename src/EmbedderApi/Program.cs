using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
app.UseDefaultFiles();
app.UseStaticFiles(); // UI: wwwroot/index.html

app.MapGet("/health", () => Results.Ok(new { status = "ok", models = settings.EmbedModels }));

// Модели из конфигурации и параметры прогона — для UI
app.MapGet("/models", () => Results.Ok(new
{
    models = settings.EmbedModels.Select(m => new { name = m, collection = settings.CollectionName(m) }),
    numThread = settings.NumThread,
    chunkSize = settings.ChunkSize,
    chunkOverlap = settings.ChunkOverlap,
    batchSize = settings.EmbedBatchSize,
}));

// Обычный текст -> эмбеддинги -> Qdrant для каждой модели из ?models=...
// (по умолчанию — все модели из конфигурации).
// Принимает text/plain или JSON {"text": "..."}.
app.MapPost("/embed", async (
    HttpRequest request,
    IngestService ingest,
    CancellationToken ct,
    [FromQuery] string[]? models,
    bool warmup = false) =>
{
    if (!TryResolveModels(models, out var selected, out var error)) return error;

    string text;
    if (request.HasJsonContentType())
    {
        try
        {
            var body = await request.ReadFromJsonAsync<EmbedRequest>(ct);
            text = body?.Text ?? "";
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = $"Некорректный JSON (ожидается UTF-8): {ex.Message}" });
        }
    }
    else
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        text = await reader.ReadToEndAsync(ct);
    }

    if (string.IsNullOrWhiteSpace(text))
        return Results.BadRequest(new { error = "Пустой текст" });

    var runs = await ingest.RunAsync(selected, [new SourceText("text", text)], warmup, ct);
    return Results.Ok(Report(runs, warmup));
});

// Бенчмарк: несколько .txt файлов (multipart, поле "files") -> время на каждый файл и каждую модель.
app.MapPost("/benchmark", async (
    [FromForm] IFormFileCollection files,
    IngestService ingest,
    CancellationToken ct,
    [FromQuery] string[]? models,
    bool warmup = true) =>
{
    if (files.Count == 0)
        return Results.BadRequest(new { error = "Передайте один или несколько файлов в поле 'files'" });
    if (!TryResolveModels(models, out var selected, out var error)) return error;

    var sources = new List<SourceText>();
    foreach (var file in files)
    {
        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
        sources.Add(new SourceText(file.FileName, await reader.ReadToEndAsync(ct)));
    }

    var runs = await ingest.RunAsync(selected, sources, warmup, ct);
    return Results.Ok(Report(runs, warmup));
}).DisableAntiforgery();

app.Run();

// Пустой список — все модели из конфигурации; неизвестные модели — 400.
bool TryResolveModels(string[]? requested, out IReadOnlyList<string> selected, out IResult error)
{
    selected = requested is { Length: > 0 } ? requested.Distinct().ToList() : settings.EmbedModels;
    var unknown = selected.Except(settings.EmbedModels).ToList();
    error = Results.BadRequest(new
    {
        error = $"Модели не заданы в OLLAMA_EMBED_MODELS: {string.Join(", ", unknown)}",
        available = settings.EmbedModels,
    });
    return unknown.Count == 0;
}

object Report(List<ModelRun> runs, bool warmup) => new
{
    numThread = settings.NumThread,
    chunkSize = settings.ChunkSize,
    chunkOverlap = settings.ChunkOverlap,
    batchSize = settings.EmbedBatchSize,
    warmup,
    runs,
};

record EmbedRequest(string? Text);
