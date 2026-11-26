# Cadastro

Sistema de cadastro de usuários autenticado pelo **Keycloak**, sucessor do [`legado`](../legado). Os usuários do
Legado são migrados para o Keycloak **sob demanda**, no primeiro login, levando a senha e os dados.

Planejamento: [spec](docs/specs/001-migracao-keycloak/spec.md) · [plano](docs/specs/001-migracao-keycloak/plan.md)
· [tarefas](docs/specs/001-migracao-keycloak/tasks.md) · [ADRs](docs/adr/).

## Estrutura

| Pasta | O que é |
|---|---|
| `cadastro-backend/` | API .NET 10 (BFF) em Clean Architecture + testes |
| `cadastro-frontend/` | Angular 22 servido por nginx |
| `docs/` | Spec, plano, tarefas e ADRs |

## Desenvolvimento

Pré-requisitos: .NET SDK 10 e Docker.

```bash
dotnet build Cadastro.slnx -warnaserror
dotnet test --solution Cadastro.slnx
```

Os módulos com Testcontainers se serializam sozinhos (trava de arquivo em `tests/Shared/ModuleExclusivity.cs`); o CI ainda passa `--max-parallel-test-modules 1`.

Importação em lote de usuários (CSV, fila, Keycloak sem senha): [docs/user-imports.md](docs/user-imports.md).

## Como subir a stack completa

Pré-requisito: Docker. O Cadastro precisa do Keycloak de pé; a migração precisa também do Legado com a API interna.
Veja a ordem detalhada no README do [`keycloak`](../keycloak) (seção "Migração dos usuários do Legado"). Resumo:

```bash
./scripts/identity-network.sh                  # rede workspace-identity (idempotente; igual nos 3 projetos)
# Graylog (opcional): cd ../graylog && ./scripts/generate-env.sh && docker compose up -d --build
# Legado: cd ../legado && ./scripts/internal-credentials.sh
#         docker compose -f docker-compose.yml -f docker-compose.identity.yml [-f docker-compose.graylog.yml] up -d --build
# Keycloak: cd ../keycloak && ./scripts/generate-env.sh, copie LEGADO_INTERNAL_KEYCLOAK_KEY (.env do legado) para
#         KC_LEGADO_SIGNING_KEY (.env do keycloak) sem exibir o valor e suba com
#         docker compose -f docker-compose.yml -f docker-compose.graylog.yml up -d --build   (sem Graylog: só o primeiro -f)
./scripts/generate-env.sh                      # .env: KC_CADASTRO_CLIENT_SECRET (do ../keycloak/.env) e chave de log
docker compose up -d --build                   # com o Graylog: -f docker-compose.yml -f docker-compose.graylog.yml
```

| Endereço | Uso |
|---|---|
| http://localhost:4300 | Cadastro (nginx; login em `/bff/login`) |
| http://localhost:8180 | Keycloak (Admin Console e login) |
| http://localhost:4200 | Legado |
| http://127.0.0.1:8025 | Mailpit (e-mails de reset de senha do Keycloak) |
| http://localhost:9000 | Graylog |

Hoje o Cadastro entrega login, migração do Legado, reset de senha e "Meu perfil" (e a pesquisa em logs). As telas
"Criar conta" e "Usuários" (T21/T22) e o autocadastro (T19) **não** fazem parte da DEV-005.

### Testes de ponta a ponta

```bash
./scripts/e2e.sh                         # Playwright + axe nos 3 dispositivos; exige a stack acima de pé
./scripts/e2e.sh --project=desktop migration.spec   # só um dispositivo/arquivo
```

O script lê `KC_AUTOMATION_CLIENT_SECRET` (`../keycloak/.env`) e `GRAYLOG_ROOT_PASSWORD` (`../graylog/.env`) sem
imprimir e aceita `E2E_LEGADO_URL`, `E2E_KEYCLOAK_URL`, `E2E_MAILPIT_URL`, `E2E_GRAYLOG_URL` e `LEGADO_API_CONTAINER`
(padrão `legado-api-1`). Detalhes e escopo em [`e2e/README.md`](e2e/README.md). **Limitação conhecida**: não rode o E2E
enquanto um provisioner ou smoke do `../keycloak` estiver em execução (falha intermitente `Parent flow doesn't exist`
ou JSON inválido nos fluxos; causa não comprovada).

## Logs (Graylog)

A API usa Serilog: JSON compacto no console e, quando `Graylog:Host` está configurado, GELF por TCP para o Graylog
do projeto [`graylog`](../graylog). Todo evento leva `service_name=cadastro-api`, `service_version` e `environment`;
`trace_id`/`span_id` entram quando há uma Activity (ex.: dentro de uma requisição HTTP) e `correlation_id` só nos
eventos de requisição (`X-Correlation-Id`). Sem `Graylog:Host` os logs ficam só no console.

```bash
# Graylog de pé antes (cria a rede "observability")
docker compose -f docker-compose.yml -f docker-compose.graylog.yml up -d --build
./scripts/smoke-graylog.sh   # com a stack completa de pé: streams, nenhum valor em claro e alertas (carga sintética GELF)
```

**Dado pessoal aprovado em ADR** (CPF, CNPJ, telefone, login, e-mail) só vai como campo estruturado com o nome do tipo
(`{Cpf}`, `{Cnpj}`, `{Phone}`/`{MobilePhone}`, `{UserName}`, `{Email}`) e é **pseudonimizado na origem**
(ADR-0003 do `../graylog`, mesmo algoritmo do `../legado`): o campo sai com a máscara (`***.456.789-**`,
`12.3**.***/***1-90`, `11*****4321`, `mar***`, `ana***@gmail`) e ganha `<Campo>_token` (`k1-` + HMAC-SHA256 truncado),
que acha todos os eventos do titular nos dois sistemas com igualdade exata (e-mail e login geram o mesmo token). Vale
também dentro de objetos destruturados, dicionários e listas: o campo mascarado é gravado no Graylog como
`Customer_Email` e o token vai no topo do evento como `Customer_Email_token` (nunca dentro do objeto, para não aparecer
no texto). O texto (`message`/`full_message`) sai só com a máscara; valor fora do formato vira `***`, sem token. No
texto dos escopos (`Scope`), só valores com máscara válida são trocados pela máscara, e CPF/CNPJ/telefone fora do formato com 8 ou mais dígitos (só dígitos contados) viram `***`; login/e-mail curto ou inválido (ex.: `@gmail.com`) não é trocado, de qualquer tamanho. Por isso dado pessoal nunca vai em template de escopo. Coberto
por `tests/Cadastro.Api.IntegrationTests/Logging/PseudonymizationTests.cs` (vetores do plano, iguais aos do legado) e
`PseudonymizedGelfTests.cs` (servidor GELF TCP falso: nenhum campo ou texto com valor em claro, nenhum token no texto).
**Não é tratado na origem**: a exceção (mensagem e stack trace seguem como estão, por isso nunca devem conter dado
pessoal) e nomes de campo fora das listas; o Graylog continua mascarando o que escapar.

| Variável (`.env`) | Obrigatória | Para quê |
|---|---|---|
| `LOG_PSEUDONYMIZATION_KEY_K1` / `LOG_PSEUDONYMIZATION_KEY_K2` | A do `CURRENT_KEY_ID` (a API não sobe sem ela) | Chave HMAC (Base64, 32 bytes) dos `*_token`; a mesma do `../legado` no ambiente. `./scripts/generate-env.sh` copia do `../legado/.env` ou gera a `k1`, e grava as chaves no user-secrets da API (pela entrada padrão, nunca na linha de comando) |
| `LOG_PSEUDONYMIZATION_CURRENT_KEY_ID` | Não (padrão `k1`) | Id da chave que emite os tokens |

Rotação anual: `../legado/scripts/log-pseudonymization-key.sh k2`, depois `./scripts/generate-env.sh` (copia a `k2`) e
`LOG_PSEUDONYMIZATION_CURRENT_KEY_ID=k2` nos dois `.env`. O Cadastro pesquisa com todas as chaves configuradas, então
mantém a `k1` até o fim da retenção dos logs (30 dias) e só depois ela é apagada. Chave vazia conta como ausente; chave
inválida ou ausente derruba a subida com mensagem que cita só o id, nunca o valor.

O nginx (`cadastro-web`) envia o access log JSON pelo Docker GELF driver (UDP) para o stream `Cadastro - Web (nginx)`;
a regra `20-nginx-json` do `../graylog` interpreta o JSON (`http_status`, `http_path`...) e alimenta o alerta
`Cadastro - Erros 5xx no nginx`. Streams da API: `Cadastro - API`, `Cadastro - Erros`, `Cadastro - Navegador`,
`Cadastro - Segurança` e `Cadastro - Pesquisa em logs (LGPD)`, alimentado pela auditoria da pesquisa abaixo.
Textos livres como `RequestPath` e `ClientSource` também passam pela máscara do pipeline (`LGPD_TEXT_FIELDS`); os client-logs já saem da API mascarados (ADR-0002) e sem query string no `ClientSource`.

### Pesquisa em logs por token

`POST /api/v1/log-search/queries` (o valor vai só no corpo, nunca em URL), corpo `{ "dataType": "cpf|cnpj|phone|email|login",
"value": "...", "caseReference": "INC-1234" }` (`caseReference`: 3 a 40 caracteres `[A-Za-z0-9._/-]` com menos de 8 dígitos no total, para um CPF, CNPJ ou telefone digitado por engano não ir em claro à auditoria; `email` exige `@`, `login` aceita o que o canônico do login aceita). Resposta 200:
`{ dataType, tokens: [{ keyId, token }], graylogQuery }`, com um token por chave configurada e a consulta
`<campo>_token:("k1-…" OR "k2-…")` unida por `OR` sobre todos os nomes de campo do tipo (e-mail e login usam a mesma
lista, derivada de `PersonalDataFields`). Erros em Problem Details: 400 com `errors` por campo (`dataType`, `value`,
`caseReference`) só com códigos `log_search.*`, **sem eco do valor**; antiforgery inválido também 400; 401 sem sessão;
403 sem a policy; 429 acima de 30 pesquisas por hora por `sub` (`RateLimiting:LogSearchPermitsPerHour`), registrado como `Warning` "Rate limit exceeded on {RequestPath} for user {UserId}" (sem `audit_event`: não faz parte do contrato do stream).

- **Policy `logs:investigate`**: role `log-investigator` **e** `acr=otp` **e** `auth_time` de no máximo 5 minutos
  (contrato do `../keycloak`: SSO e refresh trazem a role sem OTP). O BFF abre o step-up com
  `GET /bff/login?returnUrl=...&acr_values=otp` (só `otp` é aceito; outro valor é 400) e, no retorno, recusa a sessão se o
  token não vier com `acr=otp` e `auth_time` recente. A sessão guarda `acr` e `auth_time` (id técnico, não dado pessoal). O `ClaimActions` do OIDC mantém o `acr` (o padrão do ASP.NET o apagaria da identidade, `Auth/OidcClaimActionsTests`); `acr` e `auth_time` vêm sempre do mesmo token.
- **Auditoria** (stream `Cadastro - Pesquisa em logs (LGPD)`): cada pesquisa atendida gera um log `Information` com os
  campos `audit_event=log_search_query_issued`, `investigator_id` (o `sub`), `data_type`, `case_reference`, `trace_id` e
  `tokens` como **texto único** separado por vírgula (uma lista viraria `tokens_0`, `tokens_1`... no sink GELF); nunca o
  valor nem a máscara. O 403 (por falta de role, OTP ausente ou vencido) gera um `Warning` com `audit_event=log_search_access_denied` e só `investigator_id` e
  `trace_id` (sem `tokens`, `data_type` nem `case_reference`). Esses nomes ficam fora de `PersonalDataFields`.
- **Tela `/log-search` (Pesquisa em logs)**: rota protegida por `logSearchGuard` (sobre `roleGuard('log-investigator')`); visitante vai ao login e
  volta; quem entrou sem a role vai a `/forbidden` e antes disso o guard chama `GET /api/v1/log-search/access` (sem corpo nem dado digitado; policy `logs:investigate`, 204 ou 403 auditado como `log_search_access_denied`, mesmo rate limit; falha da chamada é ignorada e a navegação segue; sem redirecionamento ao step-up). O item do menu só aparece com a role. Formulário (tipo, valor,
  referência do caso) com validação espelhando o backend e 4 estados: inicial, carregando, sucesso (consulta copiável,
  chaves usadas, aviso de `*_token`) e erro (por campo, falha geral com "Tentar de novo", 429, step-up). Contrato de
  segurança: `POST /api/v1/log-search/queries` com `X-XSRF-TOKEN` do `/bff/user`; 403 com a role = OTP ausente ou vencido,
  e a tela mostra o aviso de step-up e oferece "Confirmar com código", que abre `/bff/login?acr_values=otp&returnUrl=%2Flog-search`;
  ao voltar do Keycloak a tela abre vazia (o valor foi descartado antes de sair), e o usuário digita de novo. O valor pesquisado nunca vai para URL, `localStorage`,
  `sessionStorage`, `AppLogger` ou console e é limpo da tela e do estado após o sucesso; os erros do servidor são lidos só
  pelos códigos `log_search.*`. Cobertura: Vitest (`log-search/*.spec.ts`, `auth.guard.spec.ts`); a verificação visual/axe nos 3
  dispositivos foi manual (sem spec em `e2e/`).
- **Script local `scripts/log-search-token.sh`** (só desenvolvimento: lê a chave do `.env`; em produção a pesquisa é
  pela tela `/log-search`, com role, OTP e auditoria). Imprime o token de cada chave do `.env` (`k1`, `k2`...) e a
  consulta do Graylog (OR de `<campo>_token:("tok1" OR "tok2")` sobre todos os nomes de campo do tipo):

  ```bash
  ./scripts/log-search-token.sh cpf                            # pede o valor sem eco (não vai ao histórico do shell)
  printf '%s' "$VALOR" | ./scripts/log-search-token.sh phone   # ou pela entrada padrão
  ./scripts/log-search-token.sh --self-test                    # reproduz os 5 vetores do plano com a chave fictícia
  ```

  Tipos: `cpf`, `cnpj`, `phone`, `email`, `login`. Passar o valor como 2º argumento funciona, mas o deixa no histórico
  do shell e na lista de processos: evite com dado real. Recusa rodar sem `.env`, sem chave preenchida ou com valor
  inválido pelo canônico do tipo. Exige locale `C.UTF-8` (o canônico de login/e-mail usa minúsculas Unicode, como o `ToLowerInvariant` do .NET; sem o locale o script para com mensagem clara). Aceita chave de 32 bytes ou mais, como o backend (acima de 64 bytes ela é trocada pelo SHA-256, RFC 2104). O `--self-test` cobre os 5 vetores, chaves de 48 e 80 bytes, canônico não-ASCII e as recusas (sem `.env`, CPF/CNPJ/telefone inválidos, e-mail sem `@`, login curto). Só `bash` e `openssl`: o HMAC-SHA256 é montado (RFC 2104) com `openssl dgst -sha256`
  pela entrada padrão, para a chave nunca aparecer em linha de comando (`-macopt hexkey:` a exporia aos outros
  processos) e nunca ser impressa. Para achar eventos do `../legado` a chave precisa ser a mesma:
  `./scripts/generate-env.sh` copia a `k1` do `../legado/.env`; no sentido inverso, rode
  `../legado/scripts/log-pseudonymization-key.sh` (copia a `k1` deste `.env` para o do legado e para o user-secrets do
  `Legado.Api`).

## Health checks (rede interna)

| Endpoint | Verifica |
|---|---|
| `/health/live` | Só o processo |
| `/health/ready` | Dependências obrigatórias |
| `/health` | Relatório completo, com métricas do processo |
