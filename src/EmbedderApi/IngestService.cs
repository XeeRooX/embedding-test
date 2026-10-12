using System.Diagnostics;

namespace EmbedderApi;

public sealed record IngestResult(
    string Source,
    int Chars,
    int Chunks,
    long Tokens,
    double EmbeddingMs,
    double UpsertMs,
    double TotalMs);

public sealed class IngestService(OllamaClient ollama, QdrantClient qdrant, AppSettings settings)
{
    public async Task<IngestResult> IngestAsync(string text, string source, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var embedSw = new Stopwatch();
        var upsertSw = new Stopwatch();
        long tokens = 0;

        var chunks = TextChunker.Split(text, settings.ChunkSize, settings.ChunkOverlap);

        for (var offset = 0; offset < chunks.Count; offset += settings.EmbedBatchSize)
        {
            var batch = chunks.Skip(offset).Take(settings.EmbedBatchSize).ToList();

            embedSw.Start();
            var resp = await ollama.EmbedAsync(batch, ct);
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
                        ["model"] = settings.EmbedModel,
                    }))
                .ToList();

            upsertSw.Start();
            await qdrant.EnsureCollectionAsync(resp.Embeddings[0].Length, ct);
            await qdrant.UpsertAsync(points, ct);
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

    /// <summary>Прогрев: заставляет Ollama загрузить модель в память, чтобы не мерить загрузку.</summary>
    public Task WarmupAsync(CancellationToken ct) => ollama.EmbedAsync(["warmup"], ct);
}
