namespace EmbedderApi;

public static class TextChunker
{
    /// <summary>Режет текст на чанки ~chunkSize символов с перекрытием, стараясь резать по пробелу.</summary>
    public static List<string> Split(string text, int chunkSize, int overlap)
    {
        var chunks = new List<string>();
        text = text.Trim();
        if (text.Length == 0) return chunks;

        overlap = Math.Clamp(overlap, 0, chunkSize / 2);
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + chunkSize, text.Length);
            if (end < text.Length)
            {
                // ищем пробел в последних 20% чанка, чтобы не рвать слова
                var ws = text.LastIndexOfAny([' ', '\n', '\r', '\t'], end - 1, (end - start) / 5);
                if (ws > start) end = ws;
            }

            var chunk = text[start..end].Trim();
            if (chunk.Length > 0) chunks.Add(chunk);

            if (end >= text.Length) break;
            start = Math.Max(end - overlap, start + 1);
        }
        return chunks;
    }
}
