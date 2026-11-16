#!/usr/bin/env bash
# Gera os tokens de pseudonimização (ADR-0003 do ../graylog) e a consulta do Graylog para um dado pessoal.
# SÓ PARA DESENVOLVIMENTO LOCAL: lê a chave do .env. Em produção a pesquisa é pela tela /log-search
# (POST /api/v1/log-search/queries), que exige role, OTP e deixa trilha de auditoria.
#
# Uso:
#   ./scripts/log-search-token.sh <cpf|cnpj|phone|email|login>          # valor por prompt sem eco (ou stdin)
#   printf '%s' "$VALOR" | ./scripts/log-search-token.sh cpf            # valor por stdin
#   ./scripts/log-search-token.sh <tipo> <valor>                        # evite: o valor fica no histórico do shell
#   ./scripts/log-search-token.sh --self-test                           # reproduz os 5 vetores do plano (chave fictícia)
#
# Segurança da chave: o HMAC-SHA256 é calculado aqui com a fórmula padrão (RFC 2104) usando só
# "openssl dgst -sha256" sobre a entrada padrão. Assim a chave NUNCA aparece em linha de comando
# (o que "openssl ... -mac HMAC -macopt hexkey:..." exporia na lista de processos) nem em arquivo temporário.
# O script nunca imprime a chave. Os tokens e a consulta são impressos (não são segredo, mas identificam a pessoa
# para quem tem acesso ao Graylog: não cole em chamados).
set -euo pipefail
shopt -s inherit_errexit
# O canônico de login/e-mail usa minúsculas Unicode (como o ToLowerInvariant do .NET): exige locale UTF-8.
export LC_ALL=C.UTF-8
_probe="ÁÉ"
[[ "${_probe,,}" == "áé" ]] || { echo "Locale C.UTF-8 indisponível: instale-o (ex.: locales-all) para o canônico não-ASCII bater com o .NET." >&2; exit 1; }

cd "$(dirname "$0")/.."
ENV_FILE=".env"
# Chave fictícia dos vetores de teste (bytes 00..1f). Não é chave real.
SELF_TEST_KEY="AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="
BLOCK=64
MIN_KEY_BYTES=32

die() { echo "$*" >&2; exit 1; }

# Mesmas listas de PersonalDataFields (cadastro) e LGPD_*_FIELDS (provision.sh do graylog).
fields_for() {
  case "$1" in
    cpf) echo "cpf Cpf CPF" ;;
    cnpj) echo "cnpj Cnpj CNPJ" ;;
    phone) echo "phone Phone mobilePhone MobilePhone mobile_phone celular Celular telefone Telefone" ;;
    email | login) echo "username user_name UserName Username login Login preferred_username email Email" ;;
  esac
}

# Normaliza o valor pelo canônico do tipo e devolve o domínio e o canônico; falha se o formato é inválido.
canonicalize() {
  local type="$1" raw="$2" v
  case "$type" in
    cpf)
      v="$(LC_ALL=C tr -cd '0-9' <<<"$raw")"
      [[ "$v" =~ ^[0-9]{11}$ ]] || return 1
      DOMAIN=cpf ;;
    cnpj)
      v="${raw#"${raw%%[![:space:]]*}"}"; v="${v%"${v##*[![:space:]]}"}"
      v="$(LC_ALL=C tr -d './-' <<<"$v")"; v="${v^^}"
      [[ "$v" =~ ^[0-9A-Z]{12}[0-9]{2}$ ]] || return 1
      DOMAIN=cnpj ;;
    phone)
      v="$(LC_ALL=C tr -cd '0-9' <<<"$raw")"
      if ((${#v} >= 12)) && [[ "$v" == 55* ]]; then v="${v:2}"; fi
      [[ "$v" =~ ^[0-9]{10,11}$ ]] || return 1
      DOMAIN=phone ;;
    email | login)
      v="${raw#"${raw%%[![:space:]]*}"}"; v="${v%"${v##*[![:space:]]}"}"; v="${v,,}"
      ((${#v} >= 4)) && [[ "$v" != @* ]] || return 1
      [[ "$type" == email && "$v" != *@* ]] && return 1
      DOMAIN=login ;;
    *) return 1 ;;
  esac
  CANONICAL="$v"
}

hex_to_bin() { # $1 = hex (par de dígitos por byte) -> bytes na saída padrão
  local hex="$1" fmt="" i
  for ((i = 0; i < ${#hex}; i += 2)); do fmt+="\x${hex:i:2}"; done
  printf '%b' "$fmt"
}

sha256_hex_of() { openssl dgst -sha256 -binary | od -An -v -tx1 | tr -d ' \n'; }

# HMAC-SHA256 (RFC 2104). $1 = chave em Base64 (>= 32 bytes, como o backend), $2 = mensagem. Saída: hex minúsculo.
hmac_sha256() {
  local key_hex ipad="" opad="" i byte inner
  key_hex="$(printf '%s' "$1" | openssl base64 -d -A | od -An -v -tx1 | tr -d ' \n')"
  ((${#key_hex} >= MIN_KEY_BYTES * 2)) || die "Chave inválida: mínimo de ${MIN_KEY_BYTES} bytes em Base64."
  # RFC 2104: chave maior que o bloco do SHA-256 (64 bytes) é trocada pelo próprio SHA-256 antes do padding.
  if ((${#key_hex} > BLOCK * 2)); then key_hex="$(hex_to_bin "$key_hex" | sha256_hex_of)"; fi
  key_hex+="$(printf '0%.0s' $(seq 1 $(((BLOCK * 2) - ${#key_hex}))))" # completa com zeros até o bloco de 64 bytes
  for ((i = 0; i < BLOCK * 2; i += 2)); do
    byte=$((16#${key_hex:i:2}))
    ipad+="$(printf '%02x' $((byte ^ 0x36)))"
    opad+="$(printf '%02x' $((byte ^ 0x5c)))"
  done
  inner="$({ hex_to_bin "$ipad"; printf '%s' "$2"; } | sha256_hex_of)"
  { hex_to_bin "$opad"; hex_to_bin "$inner"; } | sha256_hex_of
}

# token = "k" + id + "-" + hex(HMAC(chave, domínio + "|" + canônico))[0..32]
make_token() { # $1 = id numérico, $2 = chave Base64
  local mac
  mac="$(hmac_sha256 "$2" "${DOMAIN}|${CANONICAL}")"
  printf 'k%s-%s' "$1" "${mac:0:32}"
}

# Chaves de 48 e 80 bytes (>= 32; a de 80 passa pelo SHA-256 da RFC 2104), canônico não-ASCII e recusas.
# Chaves fictícias derivadas de bytes sequenciais, comparadas com "openssl dgst -mac HMAC" (só aqui, nunca com chave real).
self_test_extras() {
  local bad=0 size hex b64 expected got tmp
  DOMAIN=cpf CANONICAL=12345678909
  for size in 48 80; do
    hex="$(seq 1 "$size" | while read -r n; do printf '%02x' "$n"; done)"
    b64="$(hex_to_bin "$hex" | openssl base64 -A)"
    expected="$(printf '%s' "${DOMAIN}|${CANONICAL}" | openssl dgst -sha256 -mac HMAC -macopt "hexkey:${hex}" | sed 's/^.*= //')"
    got="$(hmac_sha256 "$b64" "${DOMAIN}|${CANONICAL}")"
    if [[ "$got" == "$expected" ]]; then echo "ok   chave de ${size} bytes"; else echo "FALHA chave de ${size} bytes" >&2; bad=1; fi
  done
  if canonicalize login " ÁÉ.Silva " && [[ "$CANONICAL" == "áé.silva" ]]; then echo "ok   canônico não-ASCII"; else echo "FALHA canônico não-ASCII" >&2; bad=1; fi
  local -a refusals=("cpf|1" "cpf|123.456.789-0" "cnpj|123" "phone|123" "email|abcd" "login|abc" "login|@abcd")
  local refusal
  for refusal in "${refusals[@]}"; do
    if canonicalize "${refusal%%|*}" "${refusal#*|}"; then echo "FALHA recusa de ${refusal}" >&2; bad=1; else echo "ok   recusa ${refusal}"; fi
  done
  # Sem .env o script deve recusar: roda uma cópia numa árvore temporária sem .env.
  tmp="$(mktemp -d)"; mkdir "$tmp/scripts"; cp "$0" "$tmp/scripts/"
  if bash "$tmp/scripts/$(basename "$0")" cpf 12345678909 >/dev/null 2>&1; then echo "FALHA recusa sem .env" >&2; bad=1; else echo "ok   recusa sem .env"; fi
  rm -rf "$tmp"
  return "$bad"
}

self_test() {
  local failures=0 type input expected got
  local -a vectors=(
    "cpf|123.456.789-09|k1-b81c27bb5f38080e6ca0ed38b828f262"
    "cnpj|12.345.678/0001-90|k1-f528c9e51a504dbaf6a9f245ba2cea10"
    "phone|+55 (11) 98765-4321|k1-412838d1c85d64c0ed202ae3b1546615"
    "login| Maria.Silva |k1-c5b5c3feef6c408ecea5b17d95af9793"
    "email|Ana.Souza@Gmail.com|k1-c362504bb56a3e35cc245073244c3da6"
  )
  local vector
  for vector in "${vectors[@]}"; do
    IFS='|' read -r type input expected <<<"$vector"
    if canonicalize "$type" "$input"; then got="$(make_token 1 "$SELF_TEST_KEY")"; else got="(valor rejeitado)"; fi
    if [[ "$got" == "$expected" ]]; then echo "ok   $type"; else echo "FALHA $type: esperado $expected, obtido $got" >&2; failures=$((failures + 1)); fi
  done
  self_test_extras || failures=$((failures + 1))
  ((failures == 0)) || die "self-test: $failures vetor(es) divergente(s)."
  echo "self-test: vetores, chaves longas, canônico não-ASCII e recusas conferem."
}

[[ "${1:-}" == "--self-test" ]] && { command -v openssl >/dev/null || die "openssl não encontrado."; self_test; exit 0; }

TYPE="${1:-}"
case "$TYPE" in cpf | cnpj | phone | email | login) ;; *) die "Uso: $0 <cpf|cnpj|phone|email|login> [valor]  |  $0 --self-test" ;; esac
command -v openssl >/dev/null || die "openssl não encontrado."
[[ -f "$ENV_FILE" ]] || die "Arquivo .env não encontrado: rode ./scripts/generate-env.sh antes."

# Chaves do .env (k1, k2...), sem imprimir o valor.
declare -A KEYS=()
while IFS= read -r line || [[ -n "$line" ]]; do
  name="${line%%=*}"; value="${line#*=}"
  value="${value%$'\r'}"; value="${value%\"}"; value="${value#\"}"
  [[ -n "$value" && "$name" =~ ^LOG_PSEUDONYMIZATION_KEY_K([1-9][0-9]*)$ ]] && KEYS["${BASH_REMATCH[1]}"]="$value"
done <"$ENV_FILE"
((${#KEYS[@]} > 0)) || die "Nenhuma chave LOG_PSEUDONYMIZATION_KEY_K<N> preenchida no .env."

if [[ $# -ge 2 ]]; then
  VALUE="$2"
elif [[ -t 0 ]]; then
  read -rsp "Valor (${TYPE}), sem eco: " VALUE; echo >&2
else
  IFS= read -r VALUE || true
fi
[[ -n "${VALUE:-}" ]] || die "Valor vazio."
((${#VALUE} <= 254)) || die "Valor acima de 254 caracteres."
canonicalize "$TYPE" "$VALUE" || die "Valor inválido para o tipo '${TYPE}'."

TOKENS=()
for id in $(printf '%s\n' "${!KEYS[@]}" | sort -n); do
  token="$(make_token "$id" "${KEYS[$id]}")"
  TOKENS+=("$token")
  echo "k${id}: ${token}"
done

values=""
for token in "${TOKENS[@]}"; do values+="${values:+ OR }\"${token}\""; done
query=""
for field in $(fields_for "$TYPE" | tr ' ' '\n' | LC_ALL=C sort); do
  query+="${query:+ OR }${field}_token:(${values})"
done
echo
echo "Consulta do Graylog:"
echo "$query"
