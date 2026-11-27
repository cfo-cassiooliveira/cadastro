#!/usr/bin/env bash
# Regressão do limite de envio no nginx do front: o padrão (1 MB) barrava com 413 arquivos que a API aceita (até 5 MB).
# Sobe só a imagem web, sem API: "502" significa que o nginx aceitou o corpo e tentou repassar; "413", que o recusou.
#
# Uso: ./scripts/check-upload-limit.sh [imagem]   (padrão: cadastro-web, gerada por "docker compose build web")
set -euo pipefail

IMAGE="${1:-cadastro-web}"
work="$(mktemp -d)"
name="cadastro-upload-limit-$$"
trap 'docker rm -f "$name" >/dev/null 2>&1 || true; rm -rf "$work"' EXIT

# "api" aponta para o próprio container, onde nada escuta na 8080.
docker run -d --name "$name" --add-host api:127.0.0.1 -p 127.0.0.1::8080 "$IMAGE" >/dev/null
base="http://$(docker port "$name" 8080/tcp | head -n 1)"
curl --silent --output /dev/null --retry 20 --retry-delay 1 --retry-all-errors "$base/"

failures=0
check() { # descrição, rota, tamanho em MB, status esperado
  head -c "$(($3 * 1024 * 1024))" /dev/zero > "$work/body"
  status="$(curl --silent --output /dev/null --write-out '%{http_code}' --request POST --header 'Expect:' \
    --header 'Content-Type: application/octet-stream' --data-binary "@$work/body" "$base$2" || true)"
  if [[ "$status" == "$4" ]]; then
    echo "OK    $1 ($status)"
  else
    echo "FAIL  $1: expected $4, got $status"
    failures=$((failures + 1))
  fi
}

check "2 MB upload is forwarded to the API" /api/v1/user-imports 2 502
check "5 MB upload is forwarded to the API" /api/v1/user-imports 5 502
check "7 MB upload is refused" /api/v1/user-imports 7 413
check "other routes keep the 1 MB default" /api/v1/profile 2 413

exit "$((failures > 0))"
