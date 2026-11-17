#!/usr/bin/env bash
# Testes E2E do frontend e da migração Legado -> Keycloak (Playwright + axe-core em desktop, tablet e celular).
# Roda no container oficial do Playwright: a máquina só precisa do Docker.
# Uso (na raiz do projeto, com a stack de pé): ./scripts/e2e.sh [argumentos do "playwright test"]
#
# Pré-requisitos da migração: ../keycloak, ../legado (com docker-compose.identity.yml), ../graylog e o Mailpit do
# Keycloak de pé. Variáveis (todas opcionais; os padrões servem à stack local):
#   E2E_LEGADO_URL    Legado web e API        (padrão http://localhost:4200)
#   E2E_KEYCLOAK_URL  Keycloak                (padrão http://localhost:8180)
#   E2E_MAILPIT_URL   Mailpit                 (padrão http://localhost:8025)
#   E2E_GRAYLOG_URL   Graylog                 (padrão http://localhost:9000)
#   LEGADO_API_CONTAINER  container da API do Legado, usado na conferência dos logs (padrão legado-api-1)
# Segredos: KC_AUTOMATION_CLIENT_SECRET (../keycloak/.env) e GRAYLOG_ROOT_PASSWORD (../graylog/.env) são lidos dos
# .env e repassados ao container pelo nome (nunca aparecem em argumento de processo nem na saída).
# No Docker Desktop o global-setup do Playwright encaminha localhost:4300/8180/4200/8025/9000 para o host, mantendo o
# hostname "localhost" (contexto seguro para os cookies __Host-).
set -euo pipefail

PLAYWRIGHT_IMAGE="mcr.microsoft.com/playwright:v1.64.1-noble@sha256:2f05f8a734509a7b3f26a912639488ee08f58a7b292d14d62271bcff9038768f"
project_root="$(cd "$(dirname "$0")/.." && pwd)"
# No Git Bash (Windows) o Docker precisa do caminho no formato C:/...; "pwd -W" só existe lá.
host_dir="$(cd "$project_root" && (pwd -W 2>/dev/null || pwd))"
workspace_dir="$(cd "$project_root/.." && pwd)"
legado_api_container="${LEGADO_API_CONTAINER:-legado-api-1}"

if ! curl -fs -o /dev/null http://127.0.0.1:4300/ 2>/dev/null; then
  echo "Frontend não está de pé em 127.0.0.1:4300. Suba o ambiente: docker compose up -d --build" >&2
  exit 2
fi

# Lê uma variável de um .env sem imprimir o valor.
read_env() {
  local file="$1" name="$2"
  [[ -f "$file" ]] && grep -m1 "^${name}=" "$file" | cut -d= -f2- | tr -d '\r' || true
}

KC_AUTOMATION_CLIENT_SECRET="$(read_env "$workspace_dir/keycloak/.env" KC_AUTOMATION_CLIENT_SECRET)"
GRAYLOG_ROOT_PASSWORD="$(read_env "$workspace_dir/graylog/.env" GRAYLOG_ROOT_PASSWORD)"
export KC_AUTOMATION_CLIENT_SECRET GRAYLOG_ROOT_PASSWORD
export E2E_LEGADO_URL="${E2E_LEGADO_URL:-http://localhost:4200}"
export E2E_KEYCLOAK_URL="${E2E_KEYCLOAK_URL:-http://localhost:8180}"
export E2E_MAILPIT_URL="${E2E_MAILPIT_URL:-http://localhost:8025}"
export E2E_GRAYLOG_URL="${E2E_GRAYLOG_URL:-http://localhost:9000}"

# Linux: a rede do host dá acesso direto às portas. Docker Desktop: o global-setup encaminha as portas para o host.
if [[ "$(uname -s)" == "Linux" ]]; then
  network_args=(--network host)
else
  network_args=(-e E2E_FORWARD_HOST=host.docker.internal)
fi

# Artefatos da execução (usuários criados e verificações esperadas no Legado) começam limpos.
rm -rf "$project_root/e2e/.artifacts"
started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

playwright_status=0
MSYS_NO_PATHCONV=1 docker run --rm --ipc=host \
  "${network_args[@]}" \
  -e KC_AUTOMATION_CLIENT_SECRET -e GRAYLOG_ROOT_PASSWORD \
  -e E2E_LEGADO_URL -e E2E_KEYCLOAK_URL -e E2E_MAILPIT_URL -e E2E_GRAYLOG_URL \
  -v "${host_dir}/e2e:/e2e" \
  -v cadastro-e2e-node-modules:/e2e/node_modules \
  -w /e2e \
  "$PLAYWRIGHT_IMAGE" \
  bash -c 'npm ci --no-audit --no-fund --loglevel=error && npx playwright test "$@"' -- "$@" || playwright_status=$?

# Segundo login do migrado não consulta o Legado: confere nos logs da API do Legado que cada usuário registrado pelo
# teste teve só as verificações esperadas (a da migração) e nenhuma rejeição "Migrated" depois.
checks_file="$project_root/e2e/.artifacts/legacy-verification-checks.txt"
log_status=0
if [[ -f "$checks_file" ]]; then
  api_logs="$(docker logs "$legado_api_container" --since "$started_at" 2>&1 || true)"
  while read -r legacy_id expected; do
    [[ -z "$legacy_id" ]] && continue
    actual="$(grep -c "\"@mt\":\"Legacy credentials [a-z]* for migration.*${legacy_id}" <<<"$api_logs" || true)"
    if [[ "$actual" != "$expected" ]]; then
      echo "FALHA: o Legado recebeu ${actual} verificação(ões) do usuário ${legacy_id}; esperado ${expected}." >&2
      log_status=1
    else
      echo "OK: Legado recebeu ${actual} verificação (a da migração) do usuário ${legacy_id}; o 2º login não o consultou."
    fi
  done <"$checks_file"
fi

exit $(( playwright_status != 0 || log_status != 0 ))
