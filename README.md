# Embedding CPU test

Ollama (эмбеддер на CPU) + Qdrant + .NET 10 API с веб-интерфейсом для сравнения моделей.

```bash
docker compose up -d --build
```

Интерфейс: http://localhost:8080. Дашборд Qdrant: http://localhost:6333/dashboard (ключ берётся из `QDRANT_API_KEY`).

## Настройки

Все настройки лежат в `.env`.

- `OLLAMA_EMBED_MODELS`: модели для тестирования через запятую. Все модели скачиваются при старте (контейнер
  `ollama-pull`), уже скачанные пропускаются. После изменения списка выполните `docker compose up -d`.
- `OLLAMA_CPUS`: лимит CPU для Ollama. Число потоков инференса (`num_thread`) берётся отсюда же.
- Векторы каждой модели пишутся в отдельную коллекцию `<QDRANT_COLLECTION>_<model>`, потому что у разных
  моделей разная размерность векторов.

## Интерфейс

- Выберите модели, вставьте текст или перетащите `.txt` файлы и нажмите «Запустить».
- Модели прогоняются по очереди. Ошибка одной модели (не скачана, не поддерживается) не останавливает остальные.
- График сравнивает модели по **символам в секунду**. Токены в секунду между моделями несопоставимы: у моделей
  разные токенизаторы, и один и тот же русский текст `nomic-embed-text` режет на токены в ~2,5 раза мельче,
  чем `bekko`. Токены в секунду есть в таблице.
- «Прогрев» загружает модель в память до замера, время загрузки показывается отдельно (в подсказке на графике).
- История запусков хранится в браузере (localStorage).

## API

Параметры в query string у обоих эндпоинтов:
- `models` (можно повторять). Если не указан, берутся все модели из `OLLAMA_EMBED_MODELS`.
- `warmup`. По умолчанию `false` для `/embed` и `true` для `/benchmark`.

**POST /embed** принимает обычный текст (`text/plain`) или JSON `{"text": "..."}`.

```bash
curl -X POST "localhost:8080/embed?models=nomic-embed-text" -H "Content-Type: text/plain" --data-binary @samples/small.txt
```

**POST /benchmark** принимает несколько файлов в поле `files` (multipart).

```bash
curl -X POST "localhost:8080/benchmark" -F files=@samples/small.txt -F files=@samples/ru-medium.txt
```

Ответ обоих эндпоинтов: параметры прогона и `runs`, по одному элементу на модель.

```jsonc
{
  "numThread": 4, "chunkSize": 1000, "chunkOverlap": 100, "batchSize": 16, "warmup": true,
  "runs": [{
    "model": "nomic-embed-text", "collection": "embeddings_nomic-embed-text",
    "error": null, "warmupMs": 6628.7,
    "files": [{ "source": "small.txt", "chars": 1060, "chunks": 2, "tokens": 273,
                "embeddingMs": 500, "upsertMs": 19, "totalMs": 519, "charsPerSec": 2119, "tokensPerSec": 546 }],
    "totalChars": 1060, "totalTokens": 273, "embeddingMs": 500, "upsertMs": 19, "totalMs": 519,
    "charsPerSec": 2119, "tokensPerSec": 546
  }]
}
```

**GET /models** возвращает модели из конфигурации и параметры чанкинга. **GET /health** — проверка живости.

Токены считаются по `prompt_eval_count`, который возвращает Ollama.

## Перенос на машину без интернета (Linux)

Переносятся образы (API, Ollama, Qdrant), тома (скачанные модели и данные Qdrant) и файлы проекта вместе с `.env`.
На целевой машине нужен только Docker с плагином compose. Объём бандла около 5,5 ГБ, из них 3,8 ГБ занимает
образ Ollama.

### Скриптами

На машине с интернетом, из корня проекта:

```bash
bash scripts/offline-export.sh            # создаст ./offline-bundle
```

Скрипт выполняет пять шагов:
1. собирает API и скачивает образы;
2. поднимает стек, чтобы скачать все модели;
3. останавливает стек на время копирования томов;
4. экспортирует тома, образы и проект;
5. считает контрольные суммы.

После экспорта стек запускается обратно.

Перенесите папку `offline-bundle` на целевую машину и запустите:

```bash
cd offline-bundle
bash offline-import.sh ~/embedding-test   # папка установки, по умолчанию ~/embedding-test
```

Импорт проверяет контрольные суммы, загружает образы, распаковывает проект, восстанавливает тома и запускает
стек с `--no-build --pull never`. Существующие тома импорт не перезаписывает.

### Вручную, те же шаги командами

Экспорт (машина с интернетом, корень проекта):

```bash
mkdir -p offline-bundle/volumes
docker compose build && docker compose pull --ignore-buildable
docker compose up -d          # скачает модели из OLLAMA_EMBED_MODELS
docker compose stop           # консистентная копия томов

# тома: tar изнутри контейнера (образ ollama уже содержит tar)
for v in ollama_data qdrant_data; do
  docker run --rm --entrypoint tar -v embedding-test_$v:/data:ro ollama/ollama:0.40.3 \
    -cf - -C /data . > offline-bundle/volumes/$v.tar
done

# образы
docker save $(docker compose config --images) > offline-bundle/images.tar

# проект
tar -czf offline-bundle/project.tar.gz --exclude=./.git --exclude=./offline-bundle --exclude='*/bin' --exclude='*/obj' .

docker compose start
```

Импорт (целевая машина, внутри `offline-bundle`):

```bash
docker load < images.tar
mkdir -p ~/embedding-test && tar -xzf project.tar.gz -C ~/embedding-test

for v in ollama_data qdrant_data; do
  docker volume create --label com.docker.compose.project=embedding-test \
    --label com.docker.compose.volume=$v embedding-test_$v
  docker run --rm -i --entrypoint tar -v embedding-test_$v:/data ollama/ollama:0.40.3 \
    -xf - -C /data < volumes/$v.tar
done

cd ~/embedding-test && docker compose up -d --no-build --pull never
```

Если версия Ollama в `.env` другая, замените `ollama/ollama:0.40.3` в командах на неё.
