#!/usr/bin/env bash
# Regressão do limite de envio no nginx do front: o padrão (1 MB) barrava com 413 arquivos que a API aceita (até 5 MB).
# Sobe a imagem web e, como "api", um substituto que responde 204: "204" significa que o nginx aceitou o corpo e repassou;
# "413", que o recusou.
#
# Uso: ./scripts/check-upload-limit.sh [imagem]   (padrão: cadastro-web, gerada por "docker compose build web")
set -euo pipefail

IMAGE="${1:-cadastro-web}"
work="$(mktemp -d)"
name="cadastro-upload-limit-$$"
trap 'docker rm -f "$name" "$name-api" >/dev/null 2>&1 || true; docker network rm "$name" >/dev/null 2>&1 || true; rm -rf "$work"' EXIT

docker network create "$name" >/dev/null
# Substituto da API: a própria imagem do nginx com uma configuração mínima que responde 204.
stub='pid /tmp/stub.pid; events {} http { server { listen 8080; client_max_body_size 20m; location / { return 204; } } }'
docker run -d --name "$name-api" --network "$name" --network-alias api --entrypoint sh "$IMAGE" \
  -c "echo '$stub' > /tmp/stub.conf && exec nginx -c /tmp/stub.conf -g 'daemon off;'" >/dev/null
docker run -d --name "$name" --network "$name" -p 127.0.0.1::8080 "$IMAGE" >/dev/null
base="http://$(docker port "$name" 8080/tcp | head -n 1)"
if ! curl --silent --output /dev/null --retry 20 --retry-delay 1 --retry-all-errors "$base/"; then
  docker logs "$name" >&2
  docker logs "$name-api" >&2
  exit 1
fi

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

check "2 MB upload is forwarded to the API" /api/v1/user-imports 2 204
check "5 MB upload is forwarded to the API" /api/v1/user-imports 5 204
check "5 MB upload with trailing slash is forwarded" /api/v1/user-imports/ 5 204
check "7 MB upload is refused" /api/v1/user-imports 7 413
check "other routes keep the 1 MB default" /api/v1/profile 2 413

exit "$((failures > 0))"
