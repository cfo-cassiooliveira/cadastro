# Cadastro — Cadastro de Usuários com Keycloak

> Steering de **produto e estrutura** deste repositório. Herda a Constituição (`C:\Workspace\CLAUDE.md`)
> e o steering técnico (`C:\Workspace\.claude\rules\`). Pode restringir, nunca relaxar, as regras herdadas.

Índice de docs: [docs/INDEX.md](docs/INDEX.md) · detalhes (produto, glossário, stack, regras completas): [docs/reference.md](docs/reference.md).

## Produto

Sucessor do `legado` (autocadastro, perfil, listagem admin), autenticado só pelo **Keycloak**; usuários do Legado
migram no primeiro login. Perfil no Keycloak (ADR-0002); só a importação em lote persiste, no esquema `import` do banco do Legado (ADR-0007). Dados pessoais → LGPD, nada disso em log.
Planejamento: [spec](docs/specs/001-migracao-keycloak/spec.md) · [plano](docs/specs/001-migracao-keycloak/plan.md)
· [tarefas](docs/specs/001-migracao-keycloak/tasks.md) · [resumo](docs/specs/001-migracao-keycloak/summary.md).

## Estrutura

```
cadastro-backend/src/    Cadastro.Domain, .Application, .Infrastructure, .Api (BFF), .Database (migrations do esquema import)
cadastro-backend/tests/  Api.IntegrationTests, ArchitectureTests, Domain.Tests, Infrastructure.Tests (Testcontainers SQL Server, RabbitMQ e ClamAV), Application.Tests
cadastro-frontend/       Angular 22 + nginx (telas: home, profile, log-search, user-imports = importação em lote, role user-importer)
docs/adr/                ADR-0001 (BFF), ADR-0002 (dados no Keycloak), ADR-0007 (esquema import no banco do Legado), ADR-0009 (consumidor no processo da API)
docs/specs/              Spec 001 (migração para o Keycloak)
```

## Comandos

```bash
dotnet build Cadastro.slnx --nologo -v q -warnaserror      # silencioso; se falhar, repita sem -v q
dotnet test --solution Cadastro.slnx --no-logo -v q
dotnet format Cadastro.slnx --verify-no-changes -v q
cd cadastro-frontend && npm ci --silent --no-audit --no-fund && npx ng test --watch=false && npx ng lint && npm run lint:styles && npx ng build
./scripts/generate-env.sh && ./scripts/identity-network.sh # .env (segredo do ../keycloak/.env, chave de log do ../legado/.env) e rede
./scripts/migrate-import-db.sh                              # migrations do esquema import (login cadastro_migrator; ADR-0007)
docker compose up -d --build                               # http://localhost:4300 (Keycloak de pé em ../keycloak)
docker compose -f docker-compose.yml -f docker-compose.data.yml up -d --build    # idem, com a API na rede workspace-data (SQL Server do Legado, alias legado-sqlserver; importação)
docker compose -f docker-compose.yml -f docker-compose.data.yml -f docker-compose.messaging.yml up -d --build   # idem, com a API na rede workspace-messaging (RabbitMQ do ../rabbitmq; relay da outbox) e o ClamAV do compose
docker compose -f docker-compose.yml -f docker-compose.data.yml -f docker-compose.messaging.yml -f docker-compose.import.yml up -d --build   # idem, com o consumidor das linhas (Keycloak importer + API interna do Legado em https://legado-api:8443)
docker compose -f docker-compose.yml -f docker-compose.graylog.yml up -d --build # idem, com logs no ../graylog
./scripts/smoke-graylog.sh                                  # smoke Cadastro -> Graylog (streams, LGPD, alertas); stack completa de pé
./scripts/e2e.sh                                           # E2E (Playwright + axe, 3 dispositivos) com a stack de pé
./scripts/e2e-import.sh                                    # E2E REAL da importação (5.000 linhas, stack completa de pé, roda no host, ~14 min)
./scripts/sonar-analysis.sh                                # SonarQube local (../sonarqube) + Quality Gate
```

- Testes com Testcontainers (`Infrastructure.Tests` com SQL Server, RabbitMQ e ClamAV, `Api.IntegrationTests` com Keycloak) rodam em
  processos separados; `tests/Shared/ContainerStartLock.cs` serializa o start dos containers entre eles (trava de arquivo).
- `dotnet test --solution` roda os módulos em paralelo e SQL Server + Keycloak + RabbitMQ + ClamAV juntos estouram a memória do Docker
  (~9,7 GiB; o SQL Server cai ao subir). `tests/Shared/ModuleExclusivity.cs` (assembly fixture nos módulos com containers) segura uma trava
  de arquivo durante todo o módulo, então o comando padrão já roda um por vez; `--max-parallel-test-modules 1` (usado no CI) é o mesmo
  efeito explícito. O fixture do SQL Server tenta subir uma segunda vez se o container sair antes de ficar saudável.
- Navegador local com antivírus que injeta script (ex.: Kaspersky) mostra erros de CSP no console que não são da
  aplicação; o E2E em container é a referência de console limpo.


## Regras que não podem regredir (resumo; texto completo em [docs/reference.md](docs/reference.md#regras-que-não-podem-regredir))

- **BFF**: navegador nunca recebe token; cookie `__Host-cadastro`, sessão no servidor, PKCE + PAR, `returnUrl` local,
  antiforgery em toda chamada que muda estado, `FallbackPolicy` autenticada.
- **Pesquisa em logs**: policy `logs:investigate` (role + `acr=otp` + `auth_time` <= 5 min), rate limit, auditoria sem valor.
- **Log sem dado pessoal** (Art. IV.6): só campo estruturado aprovado em ADR, pseudonimizado na origem; nunca concatenar.
- **Dados pessoais da importação** só cifrados (AES-256-GCM, `IPersonalDataProtector`), chave só por ambiente; API não sobe sem ela; runtime usa `cadastro_app` (sem `dbo`), migrations só `cadastro_migrator`.
- **Importação em lote**: `/api/v1/user-imports` (policy `user-imports:manage` = role `user-importer` + OTP recente), ClamAV fail-closed, arquivo só em streaming, linha inválida nunca vira outbox, confirmação em 2 passos com token HMAC do resumo (chave `USER_IMPORT_SUMMARY_KEY`), só o criador altera o lote, outbox na mesma transação e relay com confirms; consumidor idempotente (inbox na transação do efeito, retry por fila até 5 ciclos, DLQ) cria o usuário sem senha com e-mail de ativação **sempre com `?lifespan=259200`**; marcação no Legado só com candidato único de e-mail exato e nunca desfaz o Keycloak; `cadastro-app` do broker nunca declara topologia nem publica em `cadastro.dlx` (detalhes em `docs/reference.md`).
- Serilog com `preserveStaticLogger: true`: não dependa do `Log.Logger` estático.
- Erros devolvem **códigos** (Angular traduz via `$localize`); VOs e adapters ViaCEP/IBGE são cópias do Legado.
- **Nunca** rode `./scripts/e2e.sh` com provisioner/smoke do `../keycloak` ou outro E2E em execução.
