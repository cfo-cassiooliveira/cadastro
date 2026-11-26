#!/usr/bin/env bash
# E2E REAL da importação em lote (spec 002, E1): sobe nada, só exercita a stack que já está de pé, sem mocks.
# Fluxo: arquivo de 5.000 linhas (fictícias) enviado pela tela, confirmação, acompanhamento até concluir, conferência no
# Keycloak/Mailpit/Legado, reenvio (409), idempotência, Keycloak parado no meio de um lote menor (retry/DLQ) e varredura
# LGPD dos logs dos containers e dos payloads AMQP. Resultados numéricos em e2e/.artifacts/import-real/results.json.
#
# Pré-requisitos (ordem em docs/specs/002-uso-pelos-projetos/e2e-report.md): redes workspace-*; Legado (com .identity.yml e
# .data.yml), Keycloak provisionado (com Mailpit), RabbitMQ provisionado, Cadastro com
# docker-compose.yml + .data.yml + .messaging.yml + .import.yml; migrations do esquema import aplicadas.
# Roda no HOST (usa o CLI do Docker para parar o Keycloak, ler logs e medir memória) com o Playwright de e2e/node_modules.
# Variáveis opcionais: E2E_IMPORT_VALID/INVALID/DUPLICATES (padrão 4950/20/30), E2E_IMPORT_SMALL_VALID (400),
# E2E_IMPORT_OUT (saída), E2E_BASE_URL, E2E_KEYCLOAK_URL, E2E_MAILPIT_URL, E2E_RABBITMQ_URL, E2E_LEGADO_URL.
# Segredos (admin do Keycloak e do broker, sa do Legado) são lidos dos .env irmãos pelo próprio teste; nunca são impressos.
set -euo pipefail

project_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$project_root/e2e"

require_container() {
  local name="$1" status
  status="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$name" 2>/dev/null || true)"
  if [[ "$status" != "healthy" && "$status" != "running" ]]; then
    echo "Container $name não está de pé (estado: ${status:-inexistente}). Suba a stack (veja o cabeçalho deste script)." >&2
    exit 2
  fi
}
for container in cadastro-api-1 cadastro-web-1 cadastro-clamav-1 legado-api-1 legado-sqlserver-1 keycloak-keycloak-1 keycloak-mailpit-1 rabbitmq-rabbitmq-1; do
  require_container "$container"
done
curl -fs -o /dev/null http://localhost:4300/ || { echo "Cadastro não responde em http://localhost:4300" >&2; exit 2; }

[[ -d node_modules/@playwright/test ]] || npm ci --no-audit --no-fund --loglevel=error
npx playwright install chromium >/dev/null

export E2E_LEGADO_URL="${E2E_LEGADO_URL:-http://localhost:4200}"
export E2E_KEYCLOAK_URL="${E2E_KEYCLOAK_URL:-http://localhost:8180}"
export E2E_MAILPIT_URL="${E2E_MAILPIT_URL:-http://127.0.0.1:8025}"
export E2E_RABBITMQ_URL="${E2E_RABBITMQ_URL:-http://127.0.0.1:15672}"
# O helper de Admin API compartilhado com o E2E padrão lê o segredo do ambiente; aqui ele vem do .env, sem ser impresso.
KC_AUTOMATION_CLIENT_SECRET="$(grep -m1 '^KC_AUTOMATION_CLIENT_SECRET=' "$project_root/../keycloak/.env" | cut -d= -f2- | tr -d '')"
export KC_AUTOMATION_CLIENT_SECRET
exec npx playwright test -c import-real/playwright.config.js "$@"
