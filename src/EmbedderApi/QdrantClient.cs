using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace EmbedderApi;

public sealed record QdrantPoint(Guid Id, float[] Vector, Dictionary<string, object> Payload);

public sealed class QdrantClient(HttpClient http, AppSettings settings)
{
    // Коллекции, наличие которых уже проверено (живёт весь процесс)
    private static readonly ConcurrentDictionary<string, int> KnownCollections = new();
    private static readonly SemaphoreSlim CreateLock = new(1, 1);

    public async Task EnsureCollectionAsync(int vectorSize, CancellationToken ct)
    {
        var name = settings.CollectionName;
        if (KnownCollections.ContainsKey(name)) return;

        await CreateLock.WaitAsync(ct);
        try
        {
            if (KnownCollections.ContainsKey(name)) return;

            using var check = await http.GetAsync($"/collections/{name}", ct);
            if (check.IsSuccessStatusCode)
            {
                var info = JsonNode.Parse(await check.Content.ReadAsStringAsync(ct));
                var size = info?["result"]?["config"]?["params"]?["vectors"]?["size"]?.GetValue<int>();
                if (size is not null && size != vectorSize)
                    throw new InvalidOperationException(
                        $"Коллекция '{name}' имеет размерность {size}, а модель выдаёт {vectorSize}");
            }
            else
            {
                var body = new { vectors = new { size = vectorSize, distance = "Cosine" } };
                using var create = await http.PutAsJsonAsync($"/collections/{name}", body, ct);
                await EnsureSuccess(create, ct);
            }

            KnownCollections[name] = vectorSize;
        }
        finally
        {
            CreateLock.Release();
        }
    }

    public async Task UpsertAsync(IReadOnlyList<QdrantPoint> points, CancellationToken ct)
    {
        var body = new
        {
            points = points.Select(p => new { id = p.Id, vector = p.Vector, payload = p.Payload })
        };
        using var resp = await http.PutAsJsonAsync(
            $"/collections/{settings.CollectionName}/points?wait=true", body, ct);
        await EnsureSuccess(resp, ct);
    }

    private static async Task EnsureSuccess(HttpResponseMessage resp, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Qdrant {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
    }
}
