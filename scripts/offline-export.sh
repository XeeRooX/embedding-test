#!/usr/bin/env bash
# Экспорт стека для переноса на машину без интернета: образы, тома (модели Ollama, данные Qdrant)
# и файлы проекта. Запускать на машине С интернетом из любой папки:
#
#   ./scripts/offline-export.sh [папка-бандла]      # по умолчанию ./offline-bundle
#
# Результат переносится целиком (флешка, scp) и разворачивается скриптом offline-import.sh.
set -euo pipefail

cd "$(dirname "$0")/.."
OUT="${1:-offline-bundle}"
PROJECT="${COMPOSE_PROJECT_NAME:-embedding-test}"

mkdir -p "$OUT/volumes"
OUT="$(cd "$OUT" && pwd)"

echo "==> 1/5 Сборка API и загрузка образов"
docker compose build
docker compose pull --ignore-buildable

echo "==> 2/5 Запуск стека, чтобы скачать все модели из OLLAMA_EMBED_MODELS"
# up ждёт успешного завершения ollama-pull (depends_on embedder-api) и падает, если скачать не удалось
docker compose up -d

echo "==> 3/5 Остановка стека (консистентная копия томов)"
docker compose stop
trap 'echo "==> Запуск стека обратно"; docker compose start' EXIT

echo "==> 4/5 Экспорт томов"
# Архивируем через образ ollama (в нём есть tar) — отдельный образ на целевой машине не нужен
TOOL_IMAGE="$(docker compose config --images | grep '^ollama/ollama' | head -n1)"
for v in $(docker compose config --volumes); do
  echo "    ${PROJECT}_${v} -> volumes/${v}.tar"
  docker run --rm --entrypoint tar -v "${PROJECT}_${v}:/data:ro" "$TOOL_IMAGE" -cf - -C /data . \
    > "$OUT/volumes/${v}.tar"
done

echo "==> 5/5 Экспорт образов и файлов проекта"
IMAGES="$(docker compose config --images | sort -u)"
echo "$IMAGES" | sed 's/^/    /'
# shellcheck disable=SC2086
docker save $IMAGES > "$OUT/images.tar"
tar -czf "$OUT/project.tar.gz" \
  --exclude=./.git --exclude='*/bin' --exclude='*/obj' \
  --exclude="./$(basename "$OUT")" .
cp scripts/offline-import.sh "$OUT/"

(cd "$OUT" && sha256sum images.tar project.tar.gz volumes/*.tar > SHA256SUMS)

echo
echo "Готово: $OUT ($(du -sh "$OUT" | cut -f1))"
echo "Перенесите папку на целевую машину и запустите: ./offline-import.sh"
