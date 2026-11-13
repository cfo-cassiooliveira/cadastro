# Tarefas — Spec 001 Cadastro com Keycloak e migração sob demanda do Legado

Legenda: `[P]` pode rodar em paralelo · cada tarefa = 1 PR pequeno no repositório indicado · testes **sempre**
inclusos (Art. V.1) · `[KC]` keycloak · `[LG]` legado · `[CD]` cadastro · `[GL]` graylog · `[SQ]` sonarqube.

Cada repositório trabalha na sua branch `DEV-NNN` (criada da `master` atualizada). A ordem abaixo entrega primeiro
um **walking skeleton**: um usuário nativo do Keycloak entra no Cadastro pelo BFF e vê o perfil (T01–T08). Depois
vêm a migração e o restante.

## Fase 0 — Fundação

- [x] **T01** [CD] — Scaffolding do repositório `cadastro` (checklist de projeto novo)
  - Arquivos: `.gitignore`, `.gitattributes`, `README.md`, `CLAUDE.md` (de `_templates/project-CLAUDE.md`),
    `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `Cadastro.slnx` com
    `cadastro-backend/src/{Domain,Application,Infrastructure,Api}` e `tests/{Domain.Tests,Application.Tests,Api.IntegrationTests,ArchitectureTests}`,
    `docs/adr/0001-*.md` e `0002-*.md` (já propostos)
  - Testes inclusos: `ArchitectureTests` (regra de dependência + convenções CQRS), smoke `GET /health/live` = 200
  - Pronto quando: `dotnet build Cadastro.slnx -warnaserror` e `dotnet test --solution Cadastro.slnx` verdes

- [x] **T02** [P] [CD] — Scaffolding do `cadastro-frontend` (Angular 22) e nginx
  - Arquivos: `ng new` standalone/zoneless/strict, Vitest, ESLint + a11y, Stylelint, tokens do Workspace em
    `styles.scss`, `core/` (`AppLogger`, `GlobalErrorHandler`, interceptor de correlação), `nginx.conf` com CSP sem
    `unsafe-inline` + Trusted Types e proxy `/api` e `/bff`, `playwright.config` com 3 projetos
  - Testes inclusos: Vitest do componente raiz; E2E + axe da página inicial nos 3 dispositivos
  - Pronto quando: `npx ng test --watch=false && npx ng lint && npm run lint:styles && npx ng build` verdes

- [x] **T03** [KC] — Infra de identidade: rede compartilhada, Mailpit e hostname de backchannel
  - Arquivos: `scripts/identity-network.sh` (cria `workspace-identity` se não existir; mesmo script em cada repo),
    `docker-compose.yml` (rede `external`, serviço `mailpit` por tag+digest em `127.0.0.1:8025`,
    `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`), `scripts/smoke-test.sh`, `README.md`
  - Testes inclusos: smoke test valida Mailpit no ar, discovery via `http://keycloak:8080` com `issuer` =
    `http://localhost:8180/realms/workspace` e `token_endpoint` acessível pela rede interna
  - Pronto quando: um container de teste na `workspace-identity` obtém token client credentials com o issuer
    correto (risco nº 2 do plano eliminado)

- [x] **T04** [KC] — Realm: roles, User Profile, clients, reset e SMTP (sem os fluxos customizados ainda)
  - Depende de: T03
  - Arquivos: `realm/realm-workspace.json` (roles `admin`/`user`, `user` padrão, User Profile da §3 do plano,
    client `cadastro`, `resetPasswordAllowed`, `smtpServer`), `provisioning/provision.sh` (reconciliação
    idempotente + segredo do client), `scripts/generate-env.sh` (novo segredo), `.env.example`
  - Testes inclusos: `WorkspaceRealmIT` (PKCE S256 no `cadastro`, Direct Grant off, redirect exato, service
    account com roles mínimas, User Profile e permissões, reset habilitado, nenhum client do Legado);
    provisioner rodado 2x sem diff
  - Pronto quando: `./scripts/build.sh` e `./scripts/smoke-test.sh` verdes

- [x] **T05** [P] [SQ] [GL] — Registrar o `cadastro` no SonarQube e streams/alertas no Graylog
  - Arquivos: `sonarqube/provisioning/projects.json`; `graylog/provisioning/provision.sh` (streams `Cadastro - *`,
    `Identidade - Migração`, 3 alertas do plano §6, retenção 30 dias); `cadastro/scripts/sonar-analysis.sh`
  - Testes inclusos: smoke dos dois projetos (reprovisionamento idempotente; stream recebe mensagem de teste)
  - Pronto quando: `./scripts/sonar-analysis.sh` do cadastro passa no Quality Gate com o esqueleto

- [x] **T05b** [CD] — Pipeline de CI do cadastro (incluída na revisão de 2026-11-13)
  - Arquivos: `.github/workflows/ci.yml` (gitleaks, Trivy `fs`/`image`, backend, frontend com Prettier, Sonar
    opcional, imagens), `.trivyignore.yaml`, `.gitleaksignore`
  - Verificado localmente: gitleaks (nada versionável), Trivy `fs` e `image` sem HIGH/CRITICAL
  - Pendente: E2E no CI depende de um Keycloak compartilhado (hoje roda local, `./scripts/e2e.sh`)

## Fase 1 — Walking skeleton (usuário nativo do Keycloak)

- [x] **T06** [CD] — BFF: login, callback, logout, `/bff/user` e sessão no servidor
  - Depende de: T01, T04
  - Arquivos: `Cadastro.Api/Auth/` (OIDC + cookie `__Host-cadastro`, `InMemoryTicketStore`, renovação por
    refresh token, antiforgery, `returnUrl` local, `IClaimsTransformation` de `realm_access.roles`, policies
    `users:self` e `users:read`, `FallbackPolicy`), `docker-compose.yml` (api + web na `workspace-identity`)
  - Testes inclusos: integração com Keycloak em Testcontainers e realm do repo — login completo, `returnUrl`
    externo = 400, logout chama `end_session`, POST sem antiforgery = 400, cookie com atributos corretos,
    refresh falhando encerra a sessão; teste de log (sem token/e-mail)
  - Pronto quando: usuário criado no Admin Console entra em `http://localhost:4300/bff/login` e `/bff/user` = 200

- [x] **T07** [CD] — Admin API e `GET /api/v1/users/me`
  - Depende de: T06
  - Arquivos: `Application/Users/GetMyProfileHandler`, porta `IIdentityUserReadService`,
    `Infrastructure/Keycloak/KeycloakAdminClient` (conta de serviço, token em cache, resiliência, timeout)
  - Testes inclusos: unitário do handler; integração (perfil com e sem endereço, Keycloak fora → 503 ProblemDetails, 401)
  - Pronto quando: `/api/v1/users/me` devolve os atributos do User Profile do usuário da sessão

- [x] **T08** [CD] — Telas Início e Meu perfil + `AuthService`, guards e interceptor 401
  - Depende de: T02, T07
  - Testes inclusos: Vitest (4 estados das duas telas, guard redireciona ao login com `returnUrl`, 401 → login
    com "Sua sessão expirou. Entre novamente.", "Sair" faz POST com XSRF); E2E + axe
  - Pronto quando: jornada entrar → perfil → sair funcionando; microtextos da spec; **verificação visual com zero
    defeitos** em mobile/tablet/desktop (matriz + varredura de redimensionamento, `frontend.md` §3)

## Fase 2 — Legado: API interna de migração

- [ ] **T09** [LG] — Migration `AddUserMigrationColumns` e escrita da marcação
  - Arquivos: `legado-database/src/Legado.Database/Migrations/*`, `ApplicationUser` (`MigratedAt`,
    `KeycloakUserId`), `Infrastructure/Users/UserMigrationWriter` (grava e renova o `SecurityStamp`)
  - Testes inclusos: integração Testcontainers (migration aplica no banco existente; marcação idempotente; outro
    `keycloakUserId` → conflito; `SecurityStamp` muda na 1ª marcação)
  - Pronto quando: migrator aplica sem erro sobre o volume atual (expand, sem quebra da versão em execução)

- [ ] **T10** [LG] — Porta interna HTTPS + autenticação HMAC e grupo `/api/v1/internal`
  - Depende de: T09
  - Arquivos: `scripts/internal-credentials.sh` (CA local, certificado `legado-api` com SAN e validade de 1 ano,
    chave HMAC de 256 bits por chamador; idempotente; privados em `secrets/` no `.gitignore`/`.dockerignore`),
    `scripts/identity-network.sh`, `Api/Internal/` (endpoint Kestrel `:8443` só com `InternalApi:Enabled`, grupo
    restrito a esse endpoint, esquema `LegadoHmac`, cache anti-reenvio, permissões `keycloak`/`cadastro`),
    `docker-compose.identity.yml`, `web/nginx.conf` (`/api/v1/internal/` → 404)
  - Testes inclusos: unitários do validador (vetores de teste do ADR-0005, janela, comparação em tempo constante,
    2 chaves ativas); integração — sem cabeçalho 401; `keyId` desconhecido 401; `t` fora da janela 401; corpo
    alterado 401; reenvio 401; chamador sem permissão 403; rota interna pela porta 8080 = 404; token HS256 do
    legado não vale no grupo interno; log de assinatura recusada sem chave/assinatura
  - Pronto quando: `docker compose up` **sem** o override sobe o Legado como hoje (sem 8443, sem rede externa) e os
    testes de `tests/.../Security/` continuam verdes

- [ ] **T11** [LG] — `credential-verifications`, `user-lookups`, `users/{id}/migration`, `user-existence-checks`
  - Depende de: T10
  - Arquivos: `Application/Users/Migration/*Handler`, `Infrastructure/Users/IdentityLegacyCredentialVerifier`
    (lockout ligado, hash fictício para inexistente), `LegacyUserReadService`
  - Testes inclusos: unitários dos handlers; integração dos 5 outcomes de verify, lookup (found/not_found/migrated),
    existence (conta migrada = livre), PUT idempotente/409; teste de tempo (inexistente × senha errada com diferença
    < 30% na mediana de 20 chamadas); teste de log (sem login/e-mail/senha)
  - Pronto quando: OpenAPI de Development lista os 4 endpoints com ProblemDetails

- [ ] **T12** [P] [LG] — Login recusa conta migrada e autocadastro fechado (backend + frontend)
  - Depende de: T09
  - Arquivos: `Infrastructure/Auth/*` (`auth.account_migrated` só após senha correta), `Api/Users` (410
    `registration.closed` com `Features:SelfRegistrationEnabled`), frontend: login e cadastro com os microtextos
    e links configuráveis para o Cadastro
  - Testes inclusos: integração (senha certa → `auth.account_migrated`; errada → genérica; sessão anterior
    revogada; 410); Vitest das mensagens; E2E existente atualizado
  - Pronto quando: verificação visual com zero defeitos nas telas alteradas (matriz + varredura)

## Fase 3 — Keycloak: SPI de migração

- [ ] **T13** [KC] — `MaskedIdentifier` e eventos de migração na auditoria
  - Arquivos: `legacy/MaskedIdentifier.java`, `audit/AuditEventMapper.java`, `audit/AuditRecord.java`,
    `doc/adr/0007-*.md` (aceite)
  - Testes inclusos: `MaskedIdentifierTest` (exemplos da RN-10, ≤ 3 caracteres, domínio composto, maiúsculas,
    espaços, entrada vazia/nula → `***`); `AuditEventMapperTest` (`masked_login` só nos tipos novos; e-mail
    completo nunca aparece)
  - Pronto quando: JaCoCo ≥ 80% e regras do ADR-0002/0007 cobertas

- [ ] **T14** [P] [KC] — `LegacyUserClient` (HTTPS + assinatura HMAC)
  - Arquivos: `legacy/LegacyUserClient.java`, `HttpLegacyUserClient.java`, `LegadoRequestSigner.java`,
    `LegacyUser.java`, `LegacyClientSettings.java`; `docker-compose.yml` monta a CA pública do Legado
  - Testes inclusos: unitários — assinador confere com os vetores do ADR-0005 do legado; com servidor HTTPS local:
    cada outcome, timeout 2 s, 5xx, 401, corpo inválido, certificado de outra CA → `Unavailable`; URL `http://`
    recusada na inicialização; senha e chave nunca em `toString`/log
  - Pronto quando: nenhuma dependência nova embutida no JAR (só JDK `HttpClient`/`javax.crypto` e o Jackson do Keycloak)

- [ ] **T15** [KC] — `LegacyMigrationService` + `LegacyUsernamePasswordForm` + fluxo `workspace-browser`
  - Depende de: T13, T14
  - Arquivos: `legacy/LegacyMigrationService.java`, `LegacyUserMapper.java`, `LegacyUsernamePasswordForm(Factory).java`,
    `META-INF/services/...AuthenticatorFactory`, realm (fluxo `workspace-browser` com OTP condicional `admin`) +
    `provision.sh`
  - Testes inclusos: unitários (RN-01..RN-05, política → `UPDATE_PASSWORD`, `ModelDuplicateException`, usuário
    casca com `legacyUserId`); `LegacyLoginIT` com Keycloak real e Legado simulado (cada cenário da US-01 e US-02,
    incluindo admin → configurar OTP)
  - Pronto quando: login de usuário nativo não chama o Legado (verificado no IT)

- [ ] **T16** [KC] — `LegacyResetChooseUser` + fluxo `workspace-reset-credentials` + mensagens do tema
  - Depende de: T15
  - Arquivos: `legacy/LegacyResetChooseUser(Factory).java`, realm + `provision.sh`,
    `theme/workspace/login/messages/messages_{pt_BR,en}.properties`, versão do `pom.xml` (cache-busting, se
    mudar recurso do tema)
  - Testes inclusos: `LegacyResetIT` (cenários da US-03: e-mail enviado para Keycloak e só Legado, nenhum e-mail
    para inexistente, Legado fora, mensagem idêntica em todos); E2E do tema para "Esqueci minha senha" e
    "Atualizar senha"
  - Pronto quando: verificação visual com zero defeitos das telas do tema alteradas (matriz + varredura)

- [ ] **T17** [KC] — `LegacyMigrationCompletionListener` (marcação após commit)
  - Depende de: T15, T11
  - Arquivos: `legacy/LegacyMigrationCompletionListener(Factory).java`, `META-INF/services/...EventListenerProviderFactory`,
    realm (listener habilitado) + `provision.sh`
  - Testes inclusos: unitários (só usuários com `legacyUserId`; falha do Legado → `LEGACY_MIGRATION_MARK_FAILED`
    sem propagar exceção); IT (marca após login migrado e após reset concluído; não marca se a transação falhar)
  - Pronto quando: `./scripts/build.sh`, `smoke-test.sh` e `smoke-graylog.sh` verdes

## Fase 4 — Cadastro: autocadastro e administração

- [ ] **T18** [CD] — Endereços (ViaCEP/IBGE) e Value Objects
  - Arquivos: `Domain/` (cópia dos VOs do Legado), `Infrastructure/Addresses/*`, `Api/Addresses/*`
  - Testes inclusos: unitários dos VOs; integração com fake HTTP (sucesso, 404, timeout → 503, cache de cidades)
  - Pronto quando: contratos iguais aos do Legado

- [ ] **T19** [CD] — `POST /api/v1/users` (RegisterUser)
  - Depende de: T07, T11, T18
  - Arquivos: `Application/Users/RegisterUserCommand/Handler`, portas `IIdentityUserWriter` e
    `ILegacyUserDirectory`, `Infrastructure/Legado/LegadoUserDirectory` + `LegadoSigningHandler` (HTTPS só com a CA
    do Legado, HMAC com a chave `cadastro`)
  - Testes inclusos: unitários (sucesso, conflito no Legado, conflito no Keycloak, Legado fora → nada criado;
    assinador confere com os vetores do ADR-0005 do legado; certificado de outra CA recusado);
    integração (201; 409 com os 3 códigos; 503; rate limit; antiforgery; senha fora da política → 400 com o código
    da política); teste de log
  - Pronto quando: usuário criado aparece no Admin Console com atributos e role `user`

- [ ] **T20** [P] [CD] — `GET /api/v1/users` (admin)
  - Depende de: T07
  - Testes inclusos: unitário; integração (paginação, `pageSize` > 50 → 400, `user` → 403, anônimo → 401)

- [ ] **T21** [CD] — Tela Criar conta (4 passos)
  - Depende de: T08, T19
  - Testes inclusos: Vitest (validações, checklist da política, CEP preenche endereço, 4 estados, envio único,
    foco no erro, mensagens de conflito); E2E + axe
  - Pronto quando: microtextos da spec; verificação visual com zero defeitos (matriz + varredura)

- [ ] **T22** [P] [CD] — Tela Usuários (admin)
  - Depende de: T08, T20
  - Testes inclusos: Vitest (4 estados, 403, paginação, cartões no mobile); E2E + axe
  - Pronto quando: verificação visual com zero defeitos (matriz + varredura)

## Fase 5 — Observabilidade, ponta a ponta e entrega

- [ ] **T23** [CD] — `docker-compose.graylog.yml`, Serilog→GELF e logs do Cadastro
  - Já feito na revisão de 2026-11-13: `POST /api/v1/client-logs` (anônimo, rate limit, saneado) e rate limit por IP
    em `/bff/login` e `/bff/user`; falta levar o `log_source=browser` ao GELF (Serilog) e o `docker-compose.graylog.yml`
  - Testes inclusos: testes de log (evento, nível, sem dado pessoal); `smoke-graylog` (mensagens nos streams
    `Cadastro - *` e `Identidade - Migração`; alertas disparam com carga sintética)

- [ ] **T24** [CD] — E2E de migração com a stack completa (keycloak + legado + cadastro + Mailpit)
  - Depende de: T12, T16, T17, T21, T22
  - Arquivos: `cadastro/e2e/` + `scripts/e2e.sh`
  - Testes inclusos: Playwright (3 dispositivos) — usuário do Legado entra no Cadastro e é migrado; senha curta →
    atualizar senha; senha errada/inexistente → mensagem genérica; reset de usuário só do Legado pelo Mailpit;
    login no Legado recusado depois; admin migrado configura OTP; Graylog sem e-mail completo
  - Independência: com o Keycloak parado, o Legado sobe e passa no próprio E2E; com o Legado parado, usuário já
    migrado entra no Cadastro e o não migrado recebe a mensagem genérica

- [ ] **T25** — Documentação e ADRs aceitos
  - Arquivos: `README.md` e `CLAUDE.md` dos três repositórios (comandos, rede `workspace-identity`, regras que não
    podem regredir), `keycloak/README.md` (remove a pendência "Migração dos usuários do legado"), ADRs → Aceito
  - Pronto quando: um clone limpo sobe tudo seguindo só os READMEs

## Definition of Done da feature

- [ ] Todos os cenários Gherkin da spec cobertos por testes (unitário, integração ou E2E)
- [ ] Quality Gate do SonarQube aprovado em `keycloak`, `legado` e `cadastro`
- [ ] Revisão via skill `code-review-qa` sem itens bloqueantes em cada PR
- [ ] Busca no Graylog por `@gmail.com` nos streams novos retorna 0 resultados após o E2E
