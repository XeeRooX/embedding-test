# Embedding CPU test

Ollama (эмбеддер на CPU) + Qdrant + .NET 10 API.

```bash
docker compose up -d --build
```

Все настройки лежат в `.env`. Чтобы сменить модель, поменяйте `OLLAMA_EMBED_MODEL` и выполните `docker compose up -d`.
Новая модель скачается сама, а векторы запишутся в отдельную коллекцию `<QDRANT_COLLECTION>_<model>`.

## Эндпоинты (по умолчанию http://localhost:8080)

**POST /embed**: обычный текст (`text/plain`) или JSON `{"text": "..."}`.

```bash
curl -X POST localhost:8080/embed -H "Content-Type: text/plain" --data-binary @samples/small.txt
# {"model":"nomic-embed-text","tokens":229,"chunks":2,"embeddingMs":946.2,"upsertMs":12.9,"totalMs":959.3}
```

**POST /benchmark**: несколько файлов в поле `files` (multipart). Параметр `?warmup=false` отключает
предварительную загрузку модели в память.

```bash
curl -X POST localhost:8080/benchmark -F files=@samples/small.txt -F files=@samples/medium.txt -F files=@samples/large.txt
```

Возвращает по каждому файлу: `chunks`, `tokens`, `embeddingMs` (Ollama), `upsertMs` (Qdrant), `totalMs`, `tokensPerSec`.

**GET /health**: текущая модель и коллекция.

Токены считаются по `prompt_eval_count`, который возвращает Ollama.
Дашборд Qdrant: http://localhost:6333/dashboard (ключ берётся из `QDRANT_API_KEY`).
