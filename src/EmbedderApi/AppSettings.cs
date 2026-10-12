using System.Globalization;
using System.Text.RegularExpressions;

namespace EmbedderApi;

public sealed record AppSettings
{
    public required string OllamaUrl { get; init; }
    public string? OllamaApiKey { get; init; }
    public required string EmbedModel { get; init; }
    public required string KeepAlive { get; init; }
    public int NumThread { get; init; }

    public required string QdrantUrl { get; init; }
    public string? QdrantApiKey { get; init; }
    public required string CollectionPrefix { get; init; }

    public int ChunkSize { get; init; }
    public int ChunkOverlap { get; init; }
    public int EmbedBatchSize { get; init; }
    public long MaxUploadBytes { get; init; }

    /// <summary>Коллекция на модель: разные модели дают разную размерность векторов.</summary>
    public string CollectionName =>
        $"{CollectionPrefix}_{Regex.Replace(EmbedModel, "[^a-zA-Z0-9_-]", "_")}";

    public static AppSettings FromConfiguration(IConfiguration c) => new()
    {
        OllamaUrl = c["OLLAMA_URL"] ?? "http://localhost:11434",
        OllamaApiKey = NullIfEmpty(c["OLLAMA_API_KEY"]),
        EmbedModel = c["OLLAMA_EMBED_MODEL"] ?? "nomic-embed-text",
        KeepAlive = c["OLLAMA_KEEP_ALIVE"] ?? "10m",
        NumThread = ResolveNumThread(c),

        QdrantUrl = c["QDRANT_URL"] ?? "http://localhost:6333",
        QdrantApiKey = NullIfEmpty(c["QDRANT_API_KEY"]),
        CollectionPrefix = c["QDRANT_COLLECTION"] ?? "embeddings",

        ChunkSize = c.GetValue("CHUNK_SIZE", 1000),
        ChunkOverlap = c.GetValue("CHUNK_OVERLAP", 100),
        EmbedBatchSize = Math.Max(1, c.GetValue("EMBED_BATCH_SIZE", 16)),
        MaxUploadBytes = c.GetValue("MAX_UPLOAD_BYTES", 200L * 1024 * 1024),
    };

    /// <summary>
    /// Явный OLLAMA_NUM_THREAD, иначе — лимит CPU контейнера (OLLAMA_CPUS).
    /// Без этого Ollama берёт число ядер хоста и не видит квоту Docker.
    /// </summary>
    private static int ResolveNumThread(IConfiguration c)
    {
        if (int.TryParse(c["OLLAMA_NUM_THREAD"], out var explicitThreads) && explicitThreads > 0)
            return explicitThreads;

        return double.TryParse(c["OLLAMA_CPUS"], NumberStyles.Float, CultureInfo.InvariantCulture, out var cpus)
               && cpus > 0
            ? Math.Max(1, (int)Math.Floor(cpus))
            : 0;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
