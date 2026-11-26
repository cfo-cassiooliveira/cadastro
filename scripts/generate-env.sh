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
LEGADO_ENV="../legado-cadastro-usuarios/.env"
# Lê o valor de uma chave do arquivo .env (remove CR e as aspas simples que write_env_value coloca quando preciso).
read_key() { grep -E "^$1=.+" "$2" 2>/dev/null | tail -n 1 | cut -d= -f2- | tr -d '\r' | sed -E "s/^'(.*)'\$/\1/" || true; }

# Grava NOME=valor no .env. O docker compose interpola "$" e trata aspas/"#" em valores sem aspas; por isso valores com
# esses caracteres vão entre aspas simples (literais). Aspas simples no próprio valor não têm escape possível: aborta.
write_env_value() {
  local name="$1" value="$2"
  if [[ "$value" == *"'"* ]]; then
    echo "O valor de ${name} contém aspas simples, que não cabem no .env; use outra senha no Legado." >&2
    exit 1
  fi
  local special='[$"#\`[:space:]]'
  if [[ "$value" =~ $special ]]; then
    printf "%s='%s'\n" "$name" "$value" >> .env
  else
    printf '%s=%s\n' "$name" "$value" >> .env
  fi
}

# Connection string do SQL Server. Senha com ; " ' = ou espaço nas pontas vai entre aspas duplas (a aspa interna é
# dobrada), conforme a sintaxe de connection string do ADO.NET; a barra invertida não precisa de escape.
sql_connection_string() {
  local host="$1" user="$2" password="$3"
  if [[ "$password" =~ [\;\"\'=] || "$password" =~ ^[[:space:]] || "$password" =~ [[:space:]]$ ]]; then
    password="\"${password//\"/\"\"}\""
  fi
  printf 'Server=%s,1433;Database=LegadoUsers;User Id=%s;Password=%s;Encrypt=True;TrustServerCertificate=True' \
    "$host" "$user" "$password"
}

# Escapa uma string para JSON (barra invertida, aspas e \n \r \t); outros caracteres de controle abortam.
json_escape() {
  local s="$1" bs='\'
  s="${s//"$bs"/"$bs$bs"}"; s="${s//\"/"$bs\""}"; s="${s//$'\n'/"${bs}n"}"; s="${s//$'\r'/"${bs}r"}"; s="${s//$'\t'/"${bs}t"}"
  if [[ "$s" =~ [[:cntrl:]] ]]; then
    echo "Valor com caractere de controle não suportado em JSON de user-secrets." >&2
    exit 1
  fi
  printf '%s' "$s"
}

# Monta {"chave":"valor",...} a partir de pares chave valor, sempre com escape (nada de JSON concatenado à mão).
json_object() {
  local out="" key value
  while (( $# >= 2 )); do
    key="$(json_escape "$1")"; value="$(json_escape "$2")"
    out+="${out:+,}\"${key}\":\"${value}\""
    shift 2
  done
  printf '{%s}' "$out"
}

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
  pairs=()
  for key_id in k1 k2; do
    key="$(read_key "LOG_PSEUDONYMIZATION_KEY_${key_id^^}" .env)"
    [[ -n "$key" ]] && pairs+=("Logging:Pseudonymization:Keys:${key_id}" "$key")
  done
  current="$(read_key LOG_PSEUDONYMIZATION_CURRENT_KEY_ID .env)"
  [[ -n "$current" ]] && pairs+=("Logging:Pseudonymization:CurrentKeyId" "$current")
  # JSON (com escape) pela entrada padrão: a chave não aparece na lista de processos.
  json_object "${pairs[@]}" | dotnet user-secrets set --project cadastro-backend/src/Cadastro.Api >/dev/null
  echo "user-secrets do Cadastro.Api atualizado (Logging:Pseudonymization)."
fi

# Persistência da importação (ADR-0007). A chave AES-256 dos dados pessoais é gerada aqui (uma vez; trocá-la torna
# ilegíveis as linhas já cifradas, por isso nunca é sobrescrita) e vai também ao user-secrets da API.
if [[ -z "$(read_key PERSONAL_DATA_KEY_K1 .env)" ]]; then
  command -v openssl >/dev/null || { echo "openssl não encontrado." >&2; exit 1; }
  printf 'PERSONAL_DATA_KEY_K1=%s\n' "$(openssl rand -base64 32)" >> .env
  echo "Chave adicionada ao .env: PERSONAL_DATA_KEY_K1 (gerada)"
fi

# Connection strings dos logins do esquema import. As senhas nascem no ../legado-cadastro-usuarios (que cria os logins
# e o esquema) e são copiadas do .env de lá; sem elas o script avisa e não grava nada (a API não sobe sem a string).
# Hosts: a API (container) alcança o SQL Server pelo alias "legado-sqlserver" da rede workspace-data
# (docker-compose.data.yml); as migrations rodam no host, pela porta publicada em 127.0.0.1.
LEGADO_DB_ENV="../legado-cadastro-usuarios/.env"
app_host="$(read_key CADASTRO_DB_HOST .env)"; app_host="${app_host:-legado-sqlserver}"
migrator_host="$(read_key CADASTRO_MIGRATOR_HOST .env)"; migrator_host="${migrator_host:-127.0.0.1}"
for entry in "CADASTRO_APP_CONNECTION:cadastro_app:CADASTRO_APP_PASSWORD:${app_host}" "CADASTRO_MIGRATOR_CONNECTION:cadastro_migrator:CADASTRO_MIGRATOR_PASSWORD:${migrator_host}"; do
  IFS=: read -r name db_user pass_var host <<< "$entry"
  [[ -n "$(read_key "$name" .env)" ]] && continue
  password="$(read_key "$pass_var" "$LEGADO_DB_ENV")"
  if [[ -z "$password" ]]; then
    echo "Aviso: ${pass_var} não encontrada em ${LEGADO_DB_ENV}; ${name} não foi gerada (o agente do Legado cria o login)." >&2
    continue
  fi
  write_env_value "$name" "$(sql_connection_string "$host" "$db_user" "$password")"
  echo "Chave adicionada ao .env: ${name} (senha copiada de ${LEGADO_DB_ENV})"
done

# user-secrets da API para "dotnet run" fora do compose (valores pela entrada padrão, fora da lista de processos).
if command -v dotnet >/dev/null; then
  pairs=()
  pd_key="$(read_key PERSONAL_DATA_KEY_K1 .env)"
  if [[ -n "$pd_key" ]]; then
    pairs+=("PersonalData:Protection:CurrentKeyVersion" "k1" "PersonalData:Protection:Keys:k1" "$pd_key")
  fi
  app_conn="$(read_key CADASTRO_APP_CONNECTION .env)"
  [[ -n "$app_conn" ]] && pairs+=("ConnectionStrings:ImportDb" "$app_conn")
  if (( ${#pairs[@]} > 0 )); then
    json_object "${pairs[@]}" | dotnet user-secrets set --project cadastro-backend/src/Cadastro.Api >/dev/null
  fi
  echo "user-secrets do Cadastro.Api atualizado (PersonalData e ConnectionStrings)."
fi

# Senha do usuário cadastro-app do RabbitMQ (relay da outbox, C4). Nasce no ../rabbitmq (generate-env.sh de lá) e é copiada
# de lá: uma única fonte. Sem ela o script avisa e não grava nada (a API não sobe sem a variável, fail fast).
RABBITMQ_ENV="../rabbitmq/.env"
if [[ -z "$(read_key RABBITMQ_CADASTRO_APP_PASSWORD .env)" ]]; then
  broker_password="$(read_key RABBITMQ_CADASTRO_APP_PASSWORD "$RABBITMQ_ENV")"
  if [[ -z "$broker_password" ]]; then
    echo "Aviso: RABBITMQ_CADASTRO_APP_PASSWORD não encontrada em ${RABBITMQ_ENV}; rode ../rabbitmq/scripts/generate-env.sh." >&2
  else
    write_env_value RABBITMQ_CADASTRO_APP_PASSWORD "$broker_password"
    echo "Chave adicionada ao .env: RABBITMQ_CADASTRO_APP_PASSWORD (copiada de ${RABBITMQ_ENV})"
  fi
fi

# user-secrets da API para "dotnet run" fora do compose (valor pela entrada padrão, fora da lista de processos).
if command -v dotnet >/dev/null; then
  broker_password="$(read_key RABBITMQ_CADASTRO_APP_PASSWORD .env)"
  if [[ -n "$broker_password" ]]; then
    json_object "RabbitMq:Password" "$broker_password" "RabbitMq:Host" "localhost" | dotnet user-secrets set --project cadastro-backend/src/Cadastro.Api >/dev/null
    echo "user-secrets do Cadastro.Api atualizado (RabbitMq)."
  fi
fi

# Chave HMAC do token do resumo do lote (importação em lote). Gerada uma vez e nunca sobrescrita; vai também ao user-secrets.
if [[ -z "$(read_key USER_IMPORT_SUMMARY_KEY .env)" ]]; then
  command -v openssl >/dev/null || { echo "openssl não encontrado." >&2; exit 1; }
  printf 'USER_IMPORT_SUMMARY_KEY=%s\n' "$(openssl rand -base64 32)" >> .env
  echo "Chave adicionada ao .env: USER_IMPORT_SUMMARY_KEY (gerada)"
fi
if command -v dotnet >/dev/null; then
  summary_key="$(read_key USER_IMPORT_SUMMARY_KEY .env)"
  json_object "UserImports:SummaryTokenKey" "$summary_key" | dotnet user-secrets set --project cadastro-backend/src/Cadastro.Api >/dev/null
  echo "user-secrets do Cadastro.Api atualizado (UserImports)."
fi

# Consumidor da importação (C5a-C5c). O segredo do client "cadastro-importer" nasce no ../keycloak (generate-env.sh de lá) e a
# chave HMAC do Cadastro nasce no ../legado-cadastro-usuarios (internal-credentials.sh): ambas são só copiadas, nunca geradas aqui.
if [[ -z "$(read_key KC_CADASTRO_IMPORTER_CLIENT_SECRET .env)" ]]; then
  importer_secret="$(read_key KC_CADASTRO_IMPORTER_CLIENT_SECRET "$KEYCLOAK_ENV")"
  if [[ -z "$importer_secret" ]]; then
    echo "Aviso: KC_CADASTRO_IMPORTER_CLIENT_SECRET não encontrada em ${KEYCLOAK_ENV}; rode ../keycloak/scripts/generate-env.sh." >&2
  else
    write_env_value KC_CADASTRO_IMPORTER_CLIENT_SECRET "$importer_secret"
    echo "Chave adicionada ao .env: KC_CADASTRO_IMPORTER_CLIENT_SECRET (copiada de ${KEYCLOAK_ENV})"
  fi
fi
if [[ -z "$(read_key LEGADO_INTERNAL_CADASTRO_KEY .env)" ]]; then
  legado_key="$(read_key LEGADO_INTERNAL_CADASTRO_KEY "$LEGADO_DB_ENV")"
  if [[ -z "$legado_key" ]]; then
    echo "Aviso: LEGADO_INTERNAL_CADASTRO_KEY não encontrada em ${LEGADO_DB_ENV}; rode ../legado-cadastro-usuarios/scripts/internal-credentials.sh." >&2
  else
    write_env_value LEGADO_INTERNAL_CADASTRO_KEY "$legado_key"
    echo "Chave adicionada ao .env: LEGADO_INTERNAL_CADASTRO_KEY (copiada de ${LEGADO_DB_ENV})"
  fi
fi
