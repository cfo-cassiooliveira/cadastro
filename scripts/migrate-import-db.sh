#!/usr/bin/env bash
# Aplica as migrations do esquema import (ADR-0007) no banco LegadoUsers com o login cadastro_migrator.
# A connection string vem de CADASTRO_MIGRATOR_CONNECTION (ambiente) ou do .env; nunca é impressa nem versionada.
# Uso: ./scripts/migrate-import-db.sh
set -euo pipefail

cd "$(dirname "$0")/.."

if [[ -z "${CADASTRO_MIGRATOR_CONNECTION:-}" && -f .env ]]; then
  CADASTRO_MIGRATOR_CONNECTION="$(grep -E '^CADASTRO_MIGRATOR_CONNECTION=.+' .env | tail -n 1 | cut -d= -f2- | tr -d '\r' | sed -E "s/^'(.*)'\$/\1/" || true)"
fi
if [[ -z "${CADASTRO_MIGRATOR_CONNECTION:-}" ]]; then
  echo "CADASTRO_MIGRATOR_CONNECTION não definida (ambiente ou .env). Rode ./scripts/generate-env.sh." >&2
  exit 2
fi
export CADASTRO_MIGRATOR_CONNECTION

dotnet tool restore >/dev/null
dotnet dotnet-ef database update \
  --project cadastro-backend/src/Cadastro.Database \
  --startup-project cadastro-backend/src/Cadastro.Database \
  --context CadastroDbContext
