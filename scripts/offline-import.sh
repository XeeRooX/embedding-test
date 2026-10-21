#!/usr/bin/env bash
# Развёртывание бандла, созданного offline-export.sh, на машине без интернета.
# Требуется только Docker с плагином compose. Запуск из папки бандла:
#
#   ./offline-import.sh [папка-установки]      # по умолчанию ~/embedding-test
#
# Существующие тома не перезаписываются (чтобы не потерять данные) — удалите их вручную,
# если нужна чистая установка: docker volume rm embedding-test_ollama_data embedding-test_qdrant_data
set -euo pipefail

BUNDLE="$(cd "$(dirname "$0")" && pwd)"
TARGET="${1:-$HOME/embedding-test}"
PROJECT="${COMPOSE_PROJECT_NAME:-embedding-test}"

echo "==> 1/5 Проверка контрольных сумм"
(cd "$BUNDLE" && sha256sum -c --quiet SHA256SUMS)

echo "==> 2/5 Загрузка образов"
docker load < "$BUNDLE/images.tar"

echo "==> 3/5 Распаковка проекта в $TARGET"
mkdir -p "$TARGET"
tar -xzf "$BUNDLE/project.tar.gz" -C "$TARGET"
cd "$TARGET"

echo "==> 4/5 Восстановление томов"
TOOL_IMAGE="$(docker compose config --images | grep '^ollama/ollama' | head -n1)"
for v in $(docker compose config --volumes); do
  vol="${PROJECT}_${v}"
  archive="$BUNDLE/volumes/${v}.tar"
  if [ ! -f "$archive" ]; then
    echo "    $vol: нет архива, будет создан пустым"
    continue
  fi
  if docker volume inspect "$vol" >/dev/null 2>&1; then
    echo "    $vol: уже существует, пропускаю"
    continue
  fi
  echo "    volumes/${v}.tar -> $vol"
  # Метки compose — чтобы compose считал том своим и не ругался
  docker volume create \
    --label "com.docker.compose.project=$PROJECT" \
    --label "com.docker.compose.volume=$v" \
    "$vol" >/dev/null
  docker run --rm -i --entrypoint tar -v "$vol:/data" "$TOOL_IMAGE" -xf - -C /data < "$archive"
done

echo "==> 5/5 Запуск стека (без сборки и без обращения к registry)"
docker compose up -d --no-build --pull never

echo
echo "Готово. UI: http://localhost:$(grep -E '^API_PORT=' .env | cut -d= -f2)"
