# Cadastro — Cadastro de Usuários com Keycloak

> Steering de **produto e estrutura** deste repositório. Herda a Constituição (`C:\Workspace\CLAUDE.md`)
> e o steering técnico (`C:\Workspace\.claude\rules\`). Pode restringir, nunca relaxar, as regras herdadas.

## Produto

- **Propósito**: sucessor do `legado`, com as mesmas funcionalidades (autocadastro, perfil e listagem
  administrativa), autenticado só pelo **Keycloak**; os usuários do Legado migram sob demanda no primeiro login.
- **Usuários**: visitante (autocadastro), usuário (perfil), administrador (listagem, OTP obrigatório).
- **Bounded context**: Users (apresentação). Dono dos dados: **nenhum** — o usuário vive no User Profile do
  Keycloak (ADR-0002); o Cadastro não tem banco.
- **Fora de escopo**: edição de perfil, troca de e-mail, login social, produção.
- **Requisitos não funcionais**: dados pessoais (nome, e-mail, celular, endereço) → LGPD; nada disso em log.
- Planejamento: [spec](docs/specs/001-migracao-keycloak/spec.md) · [plano](docs/specs/001-migracao-keycloak/plan.md)
  · [tarefas](docs/specs/001-migracao-keycloak/tasks.md).

## Glossário do domínio

| Termo de negócio | Nome no código | Definição |
|---|---|---|
| Login | `UserName` | 4–30 caracteres `[a-z0-9._-]`, minúsculo (mesma regra do Legado) |
| Celular | `MobilePhone` | DDD válido + 9 dígitos iniciando em 9, salvo em E.164 |
| CEP | `PostalCode` | 8 dígitos, validado no ViaCEP |
| Município | `City` | Identificado pelo código IBGE |

## Stack deste projeto (diferenças do padrão)

- Sem banco (ADR-0002). Autenticação: **BFF** com sessão no servidor em memória (ADR-0001); realm `workspace`,
  client `cadastro`.
- Chamadas ao Legado só por HTTPS (CA do Legado) com assinatura HMAC (ADR-0005 do legado).
- Testes .NET: xUnit v3 no **Microsoft.Testing.Platform** (`global.json`), Shouldly, NSubstitute.
- Stack completa (ordem no README, "Como subir a stack completa"): `identity-network.sh` → Graylog (opcional) → `../legado`
  com `-f docker-compose.yml -f docker-compose.identity.yml` após `internal-credentials.sh` → `../keycloak` (sempre com
  `-f docker-compose.graylog.yml` se o Graylog estiver no ar; `KC_LEGADO_SIGNING_KEY` copiada de
  `LEGADO_INTERNAL_KEYCLOAK_KEY` do `../legado/.env`, sem exibir) → Cadastro. O Legado nunca depende do Keycloak nem do
  Cadastro.
- **Nunca** rode `./scripts/e2e.sh` enquanto o provisioner ou o smoke do `../keycloak` estiver rodando, nem dois ao
  mesmo tempo: houve falha intermitente `Parent flow doesn't exist`/JSON inválido com execuções concorrentes (causa não comprovada).
- E2E de migração (`e2e/tests/migration.spec.js`, `zz-logs-lgpd.spec.js`; `e2e/README.md`): contas descartáveis `e2e.*` no
  Legado (que não tem exclusão), usuários migrados apagados do Keycloak no fim de cada teste, nenhum e-mail completo no
  Graylog (a conferência `zz-` roda por último). Escopo da DEV-005 sem T21/T22: cenários de Legado fora do ar e admin
  migrado configurando OTP ficam nos ITs do `../keycloak` (`LegacyLoginIT`).
- Health checks: `/health/live`, `/health/ready` e `/health`, só na rede interna.
- Testes de arquitetura carregam cada camada pelo primeiro tipo dela: o Smart App Control do Windows bloqueia
  DLL sem tipos.

## Estrutura

```
cadastro-backend/src/    Cadastro.Domain, .Application, .Infrastructure, .Api (BFF)
cadastro-backend/tests/  Api.IntegrationTests, ArchitectureTests (+ Domain.Tests e Application.Tests com o 1º tipo)
cadastro-frontend/       Angular 22 + nginx
docs/adr/                ADR-0001 (BFF, aceito), ADR-0002 (dados no Keycloak, aceito)
docs/specs/              Spec 001 (migração para o Keycloak)
```

## Comandos

```bash
dotnet build Cadastro.slnx -warnaserror
dotnet test --solution Cadastro.slnx
dotnet format Cadastro.slnx --verify-no-changes
cd cadastro-frontend && npm ci && npx ng test --watch=false && npx ng lint && npm run lint:styles && npx ng build
./scripts/generate-env.sh && ./scripts/identity-network.sh # .env (segredo do ../keycloak/.env, chave de log do ../legado/.env) e rede
docker compose up -d --build                               # http://localhost:4300 (Keycloak de pé em ../keycloak)
docker compose -f docker-compose.yml -f docker-compose.graylog.yml up -d --build # idem, com logs no ../graylog
./scripts/e2e.sh                                           # E2E (Playwright + axe, 3 dispositivos) com a stack de pé
./scripts/sonar-analysis.sh                                # SonarQube local (../sonarqube) + Quality Gate
```

- Navegador local com antivírus que injeta script (ex.: Kaspersky) mostra erros de CSP no console que não são da
  aplicação; o E2E em container é a referência de console limpo.

## Regras que não podem regredir

- BFF: o navegador nunca recebe token; cookie `__Host-cadastro` (`HttpOnly; Secure; SameSite=Strict`), sessão no
  servidor, PKCE + PAR, `returnUrl` só local, antiforgery (`X-XSRF-TOKEN`) em toda chamada que muda estado — o token
  vem no corpo de `/bff/user` e fica só em memória (nenhum cookie legível por JavaScript),
  `FallbackPolicy` autenticada (health/OpenAPI/endpoints de entrada com `AllowAnonymous` comentado).
- Tokens só com id, login e roles (client `cadastro` sem scopes `profile`/`email`); a sessão guarda só `sub`,
  `preferred_username`, `sid`, `acr`, `auth_time` e roles. Cobertos por `tests/Cadastro.Api.IntegrationTests/Auth/`.
- Pesquisa em logs (`POST /api/v1/log-search/queries` e `GET /api/v1/log-search/access`, `Cadastro.Api/LogSearch`): policy `logs:investigate` = role
  `log-investigator` + `acr=otp` + `auth_time` <= 5 min (`OtpStepUpRequirement`; o BFF pede `/bff/login?acr_values=otp` e
  confere o retorno), antiforgery, rate limit `log-search` (30/h por `sub`), 400 sem eco do valor. Auditoria: um log por
  pesquisa com `audit_event`, `investigator_id` (o `sub`), `data_type`, `case_reference`, `trace_id` e `tokens` como
  **texto único** separado por vírgula (nunca lista: `ParseArrayValues=true`), nunca o valor nem a máscara; o 403 loga
  `log_search_access_denied` só com `investigator_id`. Esses nomes não podem entrar em `PersonalDataFields` (contrato com o
  stream do `../graylog`). `LogSearchFields` deriva de `PersonalDataFields` (sem lista própria). Cobertos por
  `tests/Cadastro.Api.IntegrationTests/LogSearch/` e `Auth/OtpStepUpTests.cs`. A tela `/log-search` (frontend:
  `app/log-search/`, guard `logSearchGuard`, que registra o acesso negado via `GET /api/v1/log-search/access` antes de ir a `/forbidden`) oferece o botão "Confirmar com código" que abre
  `/bff/login?acr_values=otp&returnUrl=%2Flog-search` quando o 403 indica OTP ausente ou vencido; ao confirmar o OTP, o
  formulário volta vazio (o valor é descartado antes de sair da tela); o valor pesquisado nunca vai para URL, storage, `AppLogger` nem console (README).
- `Bff:LocalSecureContext` só no compose local (http://localhost é contexto seguro para o navegador).
- Log **sem dado pessoal** (Constituição, Art. IV.6). Só um dado **aprovado em ADR** vai ao log, e apenas como
  campo estruturado com o nome do tipo (`{Cpf}`, `{Cnpj}`, `{Phone}`/`{MobilePhone}`, `{UserName}`, `{Email}`) no
  message template (`[LoggerMessage]`), **nunca** concatenado ou interpolado. Objeto destruturado (`{@Obj}`), dicionário e lista são tratados (máscara no próprio nível e token de topo `Obj_Campo_token`), mas dado pessoal não deve ir em log.
  O Cadastro usa Serilog (`Cadastro.Api/Common/LoggingSetup.cs`, mesmo padrão do Legado): JSON no console e, com
  `Graylog:Host` configurado (`docker-compose.graylog.yml`), GELF TCP com cada propriedade como campo próprio e
  `service_name=cadastro-api`. Por isso a máscara por nome de campo do `../graylog` (ADR-0002, listas `LGPD_*` do
  `provisioning/provision.sh`) vale para os logs do Cadastro; nome fora dessa lista não tem máscara garantida.
  Esses campos são **pseudonimizados na origem** (ADR-0003 do `../graylog`): `PseudonymizationEnricher`
  (`Cadastro.Api/Common`, último enricher) troca o valor pela máscara e soma `<Campo>_token` pela porta
  `ILogPseudonymizer` (`Cadastro.Application/LogSearch`; HMAC em `Cadastro.Infrastructure/Logging/LogPseudonymizer.cs`,
  mesmo algoritmo e mesma chave do `../legado`). Token aninhado vai como propriedade de topo (`Obj_Campo_token`), nunca
  no texto. Chave em `Logging:Pseudonymization` (user-secrets/`.env` via `./scripts/generate-env.sh`), validada na
  subida sem expor o valor. Cobertos por `Logging/PseudonymizationTests.cs` (vetores do plano) e
  `Logging/PseudonymizedGelfTests.cs` (GELF TCP real). No Graylog, `ClientSource`, `RequestPath`,
  `case_reference` e `http_path` estão em `LGPD_TEXT_FIELDS` (texto varrido pela máscara). Os client-logs
  (`POST /api/v1/client-logs`, `ClientLogs/`) também são mascarados na origem: `ClientMessage`, `ClientSource` e
  `ClientStack` passam por `PersonalDataTextMasker` (e-mail inclusive `%40`, CNPJ, CPF e celular com DDD, mesmos padrões
  do `lgpd-text.rule.tmpl` do `../graylog`) usando a máscara do ADR-0002 via `ILogPseudonymizer` (sem token em texto
  livre); trecho fora do formato vira `***`, estouro de tempo da regex (100 ms) troca o campo por `***` com Warning sem o
  texto, e o `ClientSource` perde query string e fragmento. Cobertos por `Logging/ClientLogEndpointTests.cs` e
  `Logging/PersonalDataTextMaskerTests.cs`.
- Serilog com `preserveStaticLogger: true`: nunca dependa do `Log.Logger` estático. Campos padrão e Graylog opcional
  cobertos por `tests/Cadastro.Api.IntegrationTests/Logging/` (os testes leem os eventos reais pelo sink
  `CollectingLogSink`; o `FakeLogCollector` recebe os mesmos eventos pelo `FakeLogCollectorSink`).

## Decisões e particularidades

- O contrato de erros devolve **códigos** e o Angular traduz via `$localize` (mesmo padrão do Legado).
- Value Objects e adapters ViaCEP/IBGE são **cópias** dos do Legado de propósito: não há biblioteca compartilhada.
