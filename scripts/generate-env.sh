#!/usr/bin/env bash
# Gera o .env local do cadastro (nunca versionado). Idempotente: só acrescenta as chaves que faltam.
# O segredo do client "cadastro" nasce no Keycloak (../keycloak/scripts/generate-env.sh) e é copiado de lá:
# uma única fonte, sem digitar segredo à mão.
# Também grava a chave de pseudonimização dos logs (abaixo) no .env e no user-secrets da API.
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

# Chave de pseudonimização dos logs (ADR-0003 do ../graylog): a MESMA do ../legado no ambiente. Copiada do
# ../legado/.env quando já existe lá; a k1 é gerada se não existir em nenhum dos dois (a k2 só nasce na rotação,
# por ../legado/scripts/log-pseudonymization-key.sh k2). Nunca imprime o valor nem o passa em linha de comando.
LEGADO_ENV="../legado/.env"
read_key() { grep -E "^$1=.+" "$2" 2>/dev/null | tail -n 1 | cut -d= -f2- || true; }

for key_id in k1 k2; do
  name="LOG_PSEUDONYMIZATION_KEY_${key_id^^}"
  [[ -n "$(read_key "$name" .env)" ]] && continue
  key="$(read_key "$name" "$LEGADO_ENV")"
  if [[ -n "$key" ]]; then
    origin="copiada de ${LEGADO_ENV}"
  elif [[ "$key_id" == k1 ]]; then
    command -v openssl >/dev/null || { echo "openssl não encontrado." >&2; exit 1; }
    key="$(openssl rand -base64 32)"
    origin="gerada; o ../legado/scripts/log-pseudonymization-key.sh a copia para o legado"
  else
    continue
  fi
  sed -i "/^${name}=/d" .env
  printf '%s=%s\n' "$name" "$key" >> .env
  echo "Chave adicionada ao .env: ${name} (${origin})"
done

# Mesmas chaves no user-secrets da API, para "dotnet run" fora do compose (se o dotnet estiver no PATH).
if command -v dotnet >/dev/null; then
  json=""
  for key_id in k1 k2; do
    key="$(read_key "LOG_PSEUDONYMIZATION_KEY_${key_id^^}" .env)"
    [[ -n "$key" ]] && json+="${json:+,}\"Logging:Pseudonymization:Keys:${key_id}\":\"${key}\""
  done
  current="$(read_key LOG_PSEUDONYMIZATION_CURRENT_KEY_ID .env)"
  [[ -n "$current" ]] && json+="${json:+,}\"Logging:Pseudonymization:CurrentKeyId\":\"${current}\""
  # JSON pela entrada padrão (printf é builtin): a chave não aparece na lista de processos.
  printf '{%s}' "$json" | dotnet user-secrets set --project cadastro-backend/src/Cadastro.Api >/dev/null
  echo "user-secrets do Cadastro.Api atualizado (Logging:Pseudonymization)."
fi
