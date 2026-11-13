#!/usr/bin/env bash
# Testes E2E do frontend (Playwright + axe-core em desktop, tablet e celular) contra o docker compose.
# Roda no container oficial do Playwright: a máquina só precisa do Docker.
# Uso (na raiz do projeto, com o stack de pé): ./scripts/e2e.sh [argumentos do "playwright test"]
set -euo pipefail

PLAYWRIGHT_IMAGE="mcr.microsoft.com/playwright:v1.64.1-noble@sha256:2f05f8a734509a7b3f26a912639488ee08f58a7b292d14d62271bcff9038768f"
project_root="$(cd "$(dirname "$0")/.." && pwd)"
# No Git Bash (Windows) o Docker precisa do caminho no formato C:/...; "pwd -W" só existe lá.
host_dir="$(cd "$project_root" && (pwd -W 2>/dev/null || pwd))"

if ! curl -fs -o /dev/null http://127.0.0.1:4300/ 2>/dev/null; then
  echo "Frontend não está de pé em 127.0.0.1:4300. Suba o ambiente: docker compose up -d --build" >&2
  exit 2
fi

# Linux: a rede do host dá acesso direto a localhost:4300 e :8180. Docker Desktop: o global-setup encaminha
# localhost:4300 e :8180 (Keycloak) para o host, mantendo o hostname "localhost" (contexto seguro).
if [[ "$(uname -s)" == "Linux" ]]; then
  network_args=(--network host)
else
  network_args=(-e E2E_FORWARD_HOST=host.docker.internal)
fi

MSYS_NO_PATHCONV=1 docker run --rm --ipc=host \
  "${network_args[@]}" \
  -v "${host_dir}/e2e:/e2e" \
  -v cadastro-e2e-node-modules:/e2e/node_modules \
  -w /e2e \
  "$PLAYWRIGHT_IMAGE" \
  bash -c 'npm ci --no-audit --no-fund --loglevel=error && npx playwright test "$@"' -- "$@"
