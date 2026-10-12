using System.Text.Json.Serialization;

namespace EmbedderApi;

public sealed class OllamaClient(HttpClient http, AppSettings settings)
{
    public async Task<EmbedResponse> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var request = new Dictionary<string, object>
        {
            ["model"] = settings.EmbedModel,
            ["input"] = inputs,
            ["truncate"] = true,
            ["keep_alive"] = settings.KeepAlive,
        };
        if (settings.NumThread > 0)
            request["options"] = new Dictionary<string, object> { ["num_thread"] = settings.NumThread };

        using var resp = await http.PostAsJsonAsync("/api/embed", request, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Ollama {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");

        return await resp.Content.ReadFromJsonAsync<EmbedResponse>(ct)
               ?? throw new InvalidOperationException("Ollama вернула пустой ответ");
    }

    public sealed record EmbedResponse(
        [property: JsonPropertyName("embeddings")] float[][] Embeddings,
        [property: JsonPropertyName("prompt_eval_count")] long PromptEvalCount,
        [property: JsonPropertyName("total_duration")] long TotalDurationNs,
        [property: JsonPropertyName("load_duration")] long LoadDurationNs);
}
