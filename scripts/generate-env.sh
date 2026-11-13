#!/usr/bin/env bash
# Gera o .env local do cadastro (nunca versionado). Idempotente: só acrescenta as chaves que faltam.
# O segredo do client "cadastro" nasce no Keycloak (../keycloak/scripts/generate-env.sh) e é copiado de lá:
# uma única fonte, sem digitar segredo à mão.
# Uso: ./scripts/generate-env.sh
set -euo pipefail

cd "$(dirname "$0")/.."
KEYCLOAK_ENV="../keycloak/.env"

umask 077
[[ -f .env ]] || echo "# Gerado por scripts/generate-env.sh em $(date -u +%Y-%m-%dT%H:%M:%SZ). NUNCA versione este arquivo." > .env

if ! grep -q '^KC_CADASTRO_CLIENT_SECRET=' .env; then
  secret="$(grep '^KC_CADASTRO_CLIENT_SECRET=' "$KEYCLOAK_ENV" 2>/dev/null | cut -d= -f2- || true)"
  if [[ -z "$secret" ]]; then
    echo "KC_CADASTRO_CLIENT_SECRET não encontrado em ${KEYCLOAK_ENV}. Rode ../keycloak/scripts/generate-env.sh antes." >&2
    exit 2
  fi
  echo "KC_CADASTRO_CLIENT_SECRET=${secret}" >> .env
  echo "Chave adicionada ao .env: KC_CADASTRO_CLIENT_SECRET (copiada do Keycloak)"
fi
