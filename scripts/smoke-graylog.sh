#!/usr/bin/env bash
# Smoke test da integração do Cadastro com o Graylog (T23), com Keycloak, Legado, Cadastro e Graylog reais de pé:
#   1) evento do navegador (POST /api/v1/client-logs, log_source=browser) chega ao Graylog nos streams
#      "Cadastro - Navegador", "Cadastro - API" e "Cadastro - Erros", com CPF/e-mail/celular de teste já mascarados;
#   2) login inválido de usuário inexistente (migração) chega ao stream "Identidade - Migração" com o login mascarado;
#   3) nenhum valor em claro (CPF, e-mail, celular e login de teste) em nenhum log do Graylog;
#   4) alertas do provisionamento disparam com carga sintética (GELF HTTP) e com o evento real do passo 1.
# Alertas ausentes do provisionamento NÃO são inventados: o script lista a lacuna e segue.
# Efeito colateral aceito (ambiente local): os alertas disparados ficam registrados em "Alertas" do Graylog e as
# mensagens sintéticas (host "smoke-cadastro") ficam no índice até a retenção. Os alertas ignoram só mensagens com
# smoke_test=true, por isso a carga sintética NÃO usa esse campo (senão não dispararia nada).
# Uso (na raiz do projeto, com ../graylog, ../keycloak, ../legado e este projeto de pé, todos com o override do Graylog):
#   ./scripts/smoke-graylog.sh
# Requer apenas bash, curl e openssl (nada de jq/nc: roda no Git Bash do Windows).
set -euo pipefail

cd "$(dirname "$0")/.."

CADASTRO_URL="${CADASTRO_URL:-http://localhost:4300}"
KEYCLOAK_URL="${KEYCLOAK_URL:-http://localhost:8180}"
GRAYLOG_URL="${GRAYLOG_URL:-http://127.0.0.1:9000}"
GELF_HTTP_URL="${GELF_HTTP_URL:-http://127.0.0.1:12202/gelf}"
RUN_ID="$(date +%s)"
MARKER="smokecad${RUN_ID}"
# Dados de teste fictícios (e-mail @exemplo.com; CPF gerado com dígitos verificadores válidos).
PROBE_EMAIL="smoke.${RUN_ID}@exemplo.com"
PROBE_PHONE_RAW="11987654321"
PROBE_PHONE="(11) 98765-4321"
INVESTIGATOR="smoke-investigator-${RUN_ID}"
ALERT_WAIT_SECONDS="${ALERT_WAIT_SECONDS:-360}"
failures=0
work_dir="$(mktemp -d)"

check() {
  local description="$1" ok="$2"
  if [[ "$ok" == "true" ]]; then echo "✅ ${description}"; else echo "❌ ${description}"; failures=$((failures + 1)); fi
}
contains() { grep -qF -- "$2" <<<"$1" && echo true || echo false; }
# Contagem de resultados de uma busca (linhas de dados do JSON): 0 quando "datarows":[]
has_rows() { grep -q '"datarows":\[\[' <<<"$1" && echo true || echo false; }

cleanup() { rm -rf "$work_dir"; }
trap cleanup EXIT

if [[ ! -f ../graylog/.env ]]; then
  echo "Graylog .env não encontrado em ../graylog. Suba o projeto graylog antes." >&2
  exit 2
fi
graylog_password="$(grep -E '^GRAYLOG_ROOT_PASSWORD=' ../graylog/.env | cut -d= -f2- | tr -d '\r' || true)"
if [[ -z "$graylog_password" ]]; then
  echo "GRAYLOG_ROOT_PASSWORD não encontrado em ../graylog/.env." >&2
  exit 2
fi
# Credencial via arquivo (-K), não na linha de comando.
printf 'user = "admin:%s"\n' "$graylog_password" > "${work_dir}/graylog.curlrc"
graylog_api() { curl -s -K "${work_dir}/graylog.curlrc" -H "X-Requested-By: smoke-test" -H "Content-Type: application/json" "$@"; }

# Busca nos últimos 10 min; $2 (opcional) lista de campos. Devolve o JSON cru (schema + datarows).
graylog_search() {
  graylog_api -X POST "${GRAYLOG_URL}/api/search/messages" \
    --data "{\"query\": \"$1\", \"timerange\": {\"type\": \"relative\", \"range\": 600}, \"size\": 20, \"fields\": [\"timestamp\", \"message\", \"streams\", \"service_name\", \"log_source\", \"type\", \"masked_login\", \"level\"]}"
}
await_graylog() {
  local query="$1" marker="$2" result
  for _ in $(seq 1 30); do
    result="$(graylog_search "$query")"
    if grep -qF -- "$marker" <<<"$result"; then echo "$result"; return 0; fi
    sleep 2
  done
  echo ""
}

# CPF válido (dígitos verificadores calculados) a partir de uma base de 9 dígitos derivada do RUN_ID.
generate_cpf() {
  local base="$1" sum=0 i d1 d2
  for i in $(seq 0 8); do sum=$((sum + ${base:i:1} * (10 - i))); done
  d1=$(((sum * 10) % 11 % 10))
  base+="$d1"; sum=0
  for i in $(seq 0 9); do sum=$((sum + ${base:i:1} * (11 - i))); done
  d2=$(((sum * 10) % 11 % 10))
  echo "${base}${d2}"
}
cpf_base="$(printf '%09d' $((RUN_ID % 1000000000)))"
# Evita sequência repetida (CPF inválido por regra): troca o primeiro dígito se todos forem iguais.
[[ "$cpf_base" =~ ^(.)\1{8}$ ]] && cpf_base="1${cpf_base:1}"
PROBE_CPF_RAW="$(generate_cpf "$cpf_base")"
PROBE_CPF="${PROBE_CPF_RAW:0:3}.${PROBE_CPF_RAW:3:3}.${PROBE_CPF_RAW:6:3}-${PROBE_CPF_RAW:9:2}"

# Envia uma mensagem GELF (HTTP) sem smoke_test, para que os alertas a considerem. $1 = JSON extra (campos _x).
send_gelf() {
  local code
  code="$(curl -s -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' \
    --data "{\"version\": \"1.1\", \"host\": \"smoke-cadastro\", $1}" "$GELF_HTTP_URL")"
  [[ "$code" == "202" ]]
}
send_gelf_times() { # $1 = quantidade, $2 = JSON extra; devolve "true" se todas foram aceitas
  local n ok=true
  for n in $(seq 1 "$1"); do send_gelf "$2" || ok=false; done
  echo "$ok"
}

echo "== Preparação"
check "Graylog responde com as credenciais do .env" \
  "$(graylog_api -o /dev/null -w '%{http_code}' "${GRAYLOG_URL}/api/system" | grep -q '^200$' && echo true || echo false)"
check "frontend do Cadastro responde em ${CADASTRO_URL}" \
  "$(curl -s -o /dev/null -w '%{http_code}' "${CADASTRO_URL}/" | grep -q '^200$' && echo true || echo false)"

echo "== 1) Evento do navegador (client-logs)"
browser_status="$(curl -s -o /dev/null -w '%{http_code}' -X POST "${CADASTRO_URL}/api/v1/client-logs" \
  -H 'Content-Type: application/json' \
  --data "{\"level\": \"error\", \"event\": \"smoke.probe\", \"source\": \"/smoke\", \"status\": 0, \"message\": \"${MARKER} cpf ${PROBE_CPF} cpf2 ${PROBE_CPF_RAW} mail ${PROBE_EMAIL} tel ${PROBE_PHONE} tel2 ${PROBE_PHONE_RAW}\"}")"
check "POST /api/v1/client-logs devolve 204 (anônimo, por requisito)" "$([[ "$browser_status" == "204" ]] && echo true || echo false)"
browser_result="$(await_graylog "${MARKER}" "${MARKER}")"
check "evento chegou ao Graylog com log_source=browser e service_name=cadastro-api" \
  "$([[ "$(contains "$browser_result" '"browser"')" == true && "$(contains "$browser_result" '"cadastro-api"')" == true ]] && echo true || echo false)"
check "evento está nos streams Cadastro - Navegador, Cadastro - API e Cadastro - Erros" \
  "$([[ "$(contains "$browser_result" 'Cadastro - Navegador')" == true && "$(contains "$browser_result" 'Cadastro - API')" == true && "$(contains "$browser_result" 'Cadastro - Erros')" == true ]] && echo true || echo false)"
check "evento do navegador NÃO está em Cadastro - Segurança (forjável: nunca vira trilha)" \
  "$([[ "$(contains "$browser_result" 'Cadastro - Segurança')" == false ]] && echo true || echo false)"

echo "== 2) Migração: login inválido de usuário inexistente"
# Login pelo formulário do realm workspace (fluxo real do navegador) com usuário fora do Keycloak e do Legado.
jar="${work_dir}/cookies"
login_page="$(curl -fs -c "$jar" -b "$jar" -G "${KEYCLOAK_URL}/realms/workspace/protocol/openid-connect/auth" \
  --data-urlencode "client_id=account-console" --data-urlencode "response_type=code" --data-urlencode "scope=openid" \
  --data-urlencode "redirect_uri=${KEYCLOAK_URL}/realms/workspace/account/" \
  --data-urlencode "code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM" --data-urlencode "code_challenge_method=S256" || true)"
login_action="$(grep -o 'action="[^"]*"' <<<"$login_page" | head -1 | sed -e 's/^action="//' -e 's/"$//' -e 's/&amp;/\&/g' || true)"
if [[ -n "$login_action" ]]; then
  curl -s -o /dev/null -c "$jar" -b "$jar" -X POST "$login_action" \
    --data-urlencode "username=${PROBE_EMAIL}" --data-urlencode "password=wrong-password-${RUN_ID}"
fi
check "formulário de login do Keycloak (workspace) acessível" "$([[ -n "$login_action" ]] && echo true || echo false)"
migration_result="$(await_graylog "service_name:keycloak AND type:LEGACY_USER_NOT_FOUND AND masked_login:smo*" "smo***")"
check "LEGACY_USER_NOT_FOUND chegou ao Graylog com o login mascarado" "$(contains "$migration_result" 'LEGACY_USER_NOT_FOUND')"
check "evento está no stream Identidade - Migração" "$(contains "$migration_result" 'Identidade - Migração')"

echo "== 3) Nenhum valor em claro no Graylog"
sleep 5
for value in "$PROBE_CPF" "$PROBE_CPF_RAW" "$PROBE_EMAIL" "$PROBE_PHONE" "$PROBE_PHONE_RAW" "smoke.${RUN_ID}"; do kind="${value:0:3}***"
  found="$(has_rows "$(graylog_search "\\\"${value}\\\"")")"
  check "busca pelo valor de teste (${kind}) devolve 0 resultados" "$([[ "$found" == false ]] && echo true || echo false)"
done

echo "== 4) Alertas com carga sintética"
# Título do alerta -> verificação de que ele existe no provisionamento do Graylog (GET /events/definitions).
definitions="$(graylog_api "${GRAYLOG_URL}/api/events/definitions?per_page=200")"
declare -A ALERT_LOAD=(
  ["Cadastro - Erros na aplicação"]="$(send_gelf_times 1 "\"short_message\": \"smoke synthetic error ${RUN_ID}\", \"level\": 3, \"_service_name\": \"cadastro-api\"")"
  ["Cadastro - Erros 5xx no nginx"]="$(send_gelf_times 1 "\"short_message\": \"{\\\"method\\\":\\\"GET\\\",\\\"path\\\":\\\"/smoke\\\",\\\"status\\\":500,\\\"request_time\\\":0.01}\", \"level\": 6, \"_tag\": \"cadastro-web\"")"
  ["Cadastro - Excesso de pesquisas em logs"]="$(send_gelf_times 11 "\"short_message\": \"smoke log search ${RUN_ID}\", \"level\": 6, \"_service_name\": \"cadastro-api\", \"_audit_event\": \"log_search_query_issued\", \"_investigator_id\": \"${INVESTIGATOR}\"")"
  ["Cadastro - Acessos negados à pesquisa em logs"]="$(send_gelf_times 3 "\"short_message\": \"smoke log search denied ${RUN_ID}\", \"level\": 4, \"_service_name\": \"cadastro-api\", \"_audit_event\": \"log_search_access_denied\", \"_investigator_id\": \"${INVESTIGATOR}\"")"
  ["Identidade - Legado indisponível"]="$(send_gelf_times 5 "\"short_message\": \"smoke legacy unavailable ${RUN_ID}\", \"level\": 4, \"_service_name\": \"keycloak\", \"_type\": \"LEGACY_UNAVAILABLE\"")"
  ["Identidade - Falha ao marcar migração"]="$(send_gelf_times 1 "\"short_message\": \"smoke mark failed ${RUN_ID}\", \"level\": 4, \"_service_name\": \"keycloak\", \"_type\": \"LEGACY_MIGRATION_MARK_FAILED\"")"
  ["Identidade - Tentativas de migração inválidas"]="$(send_gelf_times 31 "\"short_message\": \"smoke invalid attempt ${RUN_ID}\", \"level\": 6, \"_service_name\": \"keycloak\", \"_type\": \"LEGACY_USER_NOT_FOUND\"")"
)
# "Cadastro - Erros no navegador" dispara com o evento REAL do passo 1 (level 3, log_source=browser), sem carga sintética.
alerts=("Cadastro - Erros no navegador" "${!ALERT_LOAD[@]}")
fired=()
for title in "${alerts[@]}"; do
  if [[ "$(contains "$definitions" "\"title\":\"${title}\"")" == true ]]; then
    [[ -n "${ALERT_LOAD[$title]:-}" ]] && check "carga sintética aceita pelo GELF HTTP: ${title}" "${ALERT_LOAD[$title]}"
  else
    echo "⚠️  LACUNA: alerta \"${title}\" não existe no provisionamento do Graylog (não inventado)"
    failures=$((failures + 1))
  fi
done

# Os alertas rodam a cada 60 s: espera até ALERT_WAIT_SECONDS pelos eventos de todos.
events_json=""
pending=1
for _ in $(seq 1 $((ALERT_WAIT_SECONDS / 10))); do
  events_json="$(graylog_api -X POST "${GRAYLOG_URL}/api/events/search" \
    --data "$(printf '{"query": "", "page": 1, "per_page": 100, "timerange": {"type": "relative", "range": %d}, "filter": {"alerts": "only", "event_definitions": []}, "sort_by": "timestamp", "sort_direction": "desc"}' $(($(date +%s) - RUN_ID + 30)))")"
  pending=0
  for title in "${alerts[@]}"; do
    [[ "$(contains "$events_json" "\"message\":\"${title}:")" == true ]] || pending=1
  done
  [[ "$pending" == 0 ]] && break
  sleep 10
done
for title in "${alerts[@]}"; do
  check "alerta disparou: ${title}" "$(contains "$events_json" "\"message\":\"${title}:")"
done

echo
if [[ "$failures" -gt 0 ]]; then
  echo "Smoke test do Graylog (Cadastro) falhou: ${failures} verificação(ões)."
  exit 1
fi
echo "Smoke test do Graylog (Cadastro) OK."
