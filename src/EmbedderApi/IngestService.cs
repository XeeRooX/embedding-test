using System.Diagnostics;

namespace EmbedderApi;

public sealed record SourceText(string Name, string Text);

public sealed record IngestResult(
    string Source,
    int Chars,
    int Chunks,
    long Tokens,
    double EmbeddingMs,
    double UpsertMs,
    double TotalMs)
{
    public double TokensPerSec => PerSecond(Tokens, EmbeddingMs);

    /// <summary>У моделей разные токенизаторы, поэтому сравнивать модели честнее по символам.</summary>
    public double CharsPerSec => PerSecond(Chars, EmbeddingMs);

    internal static double PerSecond(long count, double ms) =>
        ms > 0 ? Math.Round(count / (ms / 1000), 1) : 0;
}

/// <summary>Результат прогона одной модели по всем источникам.</summary>
public sealed record ModelRun(
    string Model,
    string Collection,
    string? Error,
    double? WarmupMs,
    IReadOnlyList<IngestResult> Files)
{
    public long TotalChars => Files.Sum(f => (long)f.Chars);
    public long TotalTokens => Files.Sum(f => f.Tokens);
    public double EmbeddingMs => Math.Round(Files.Sum(f => f.EmbeddingMs), 2);
    public double UpsertMs => Math.Round(Files.Sum(f => f.UpsertMs), 2);
    public double TotalMs => Math.Round(Files.Sum(f => f.TotalMs), 2);
    public double TokensPerSec => IngestResult.PerSecond(TotalTokens, EmbeddingMs);
    public double CharsPerSec => IngestResult.PerSecond(TotalChars, EmbeddingMs);
}

public sealed class IngestService(
    OllamaClient ollama,
    QdrantClient qdrant,
    AppSettings settings,
    ILogger<IngestService> logger)
{
    /// <summary>
    /// Прогоняет источники через каждую модель по очереди.
    /// Ошибка одной модели (не скачана, не поддерживается) не прерывает остальные.
    /// </summary>
    public async Task<List<ModelRun>> RunAsync(
        IReadOnlyList<string> models, IReadOnlyList<SourceText> sources, bool warmup, CancellationToken ct)
    {
        var runs = new List<ModelRun>();
        foreach (var model in models)
        {
            var collection = settings.CollectionName(model);
            double? warmupMs = null;
            var files = new List<IngestResult>();
            try
            {
                if (warmup)
                {
                    // Прогрев: загрузка модели в память не должна попадать в замер
                    var sw = Stopwatch.StartNew();
                    await ollama.EmbedAsync(model, ["warmup"], ct);
                    warmupMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                }

                foreach (var source in sources)
                    files.Add(await IngestAsync(model, source.Text, source.Name, ct));

                runs.Add(new ModelRun(model, collection, null, warmupMs, files));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Модель {Model} завершилась с ошибкой", model);
                runs.Add(new ModelRun(model, collection, ex.Message, warmupMs, files));
            }
        }
        return runs;
    }

    public async Task<IngestResult> IngestAsync(string model, string text, string source, CancellationToken ct)
    {
        var collection = settings.CollectionName(model);
        var total = Stopwatch.StartNew();
        var embedSw = new Stopwatch();
        var upsertSw = new Stopwatch();
        long tokens = 0;

        var chunks = TextChunker.Split(text, settings.ChunkSize, settings.ChunkOverlap);

        for (var offset = 0; offset < chunks.Count; offset += settings.EmbedBatchSize)
        {
            var batch = chunks.Skip(offset).Take(settings.EmbedBatchSize).ToList();

            embedSw.Start();
            var resp = await ollama.EmbedAsync(model, batch, ct);
            embedSw.Stop();
            tokens += resp.PromptEvalCount;

            var points = resp.Embeddings
                .Select((vector, i) => new QdrantPoint(
                    Guid.NewGuid(),
                    vector,
                    new Dictionary<string, object>
                    {
                        ["source"] = source,
                        ["chunk_index"] = offset + i,
                        ["text"] = batch[i],
                        ["model"] = model,
                    }))
                .ToList();

            upsertSw.Start();
            await qdrant.EnsureCollectionAsync(collection, resp.Embeddings[0].Length, ct);
            await qdrant.UpsertAsync(collection, points, ct);
            upsertSw.Stop();
        }

        total.Stop();
        return new IngestResult(
            source,
            text.Length,
            chunks.Count,
            tokens,
            Math.Round(embedSw.Elapsed.TotalMilliseconds, 2),
            Math.Round(upsertSw.Elapsed.TotalMilliseconds, 2),
            Math.Round(total.Elapsed.TotalMilliseconds, 2));
    }
}
