# Cadastro — Cadastro de Usuários com Keycloak

> Steering de **produto e estrutura** deste repositório. Herda a Constituição (`C:\Workspace\CLAUDE.md`)
> e o steering técnico (`C:\Workspace\.claude\rules\`). Pode restringir, nunca relaxar, as regras herdadas.

Índice de docs: [docs/INDEX.md](docs/INDEX.md) · detalhes (produto, glossário, stack, regras completas): [docs/reference.md](docs/reference.md).

## Produto

Sucessor do `legado` (autocadastro, perfil, listagem admin), autenticado só pelo **Keycloak**; usuários do Legado
migram no primeiro login. Sem banco (ADR-0002); dados pessoais → LGPD, nada disso em log.
Planejamento: [spec](docs/specs/001-migracao-keycloak/spec.md) · [plano](docs/specs/001-migracao-keycloak/plan.md)
· [tarefas](docs/specs/001-migracao-keycloak/tasks.md) · [resumo](docs/specs/001-migracao-keycloak/summary.md).

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
dotnet build Cadastro.slnx --nologo -v q -warnaserror      # silencioso; se falhar, repita sem -v q
dotnet test --solution Cadastro.slnx --no-logo -v q
dotnet format Cadastro.slnx --verify-no-changes -v q
cd cadastro-frontend && npm ci --silent --no-audit --no-fund && npx ng test --watch=false && npx ng lint && npm run lint:styles && npx ng build
./scripts/generate-env.sh && ./scripts/identity-network.sh # .env (segredo do ../keycloak/.env, chave de log do ../legado/.env) e rede
docker compose up -d --build                               # http://localhost:4300 (Keycloak de pé em ../keycloak)
docker compose -f docker-compose.yml -f docker-compose.graylog.yml up -d --build # idem, com logs no ../graylog
./scripts/smoke-graylog.sh                                  # smoke Cadastro -> Graylog (streams, LGPD, alertas); stack completa de pé
./scripts/e2e.sh                                           # E2E (Playwright + axe, 3 dispositivos) com a stack de pé
./scripts/sonar-analysis.sh                                # SonarQube local (../sonarqube) + Quality Gate
```

- Navegador local com antivírus que injeta script (ex.: Kaspersky) mostra erros de CSP no console que não são da
  aplicação; o E2E em container é a referência de console limpo.


## Regras que não podem regredir (resumo; texto completo em [docs/reference.md](docs/reference.md#regras-que-não-podem-regredir))

- **BFF**: navegador nunca recebe token; cookie `__Host-cadastro`, sessão no servidor, PKCE + PAR, `returnUrl` local,
  antiforgery em toda chamada que muda estado, `FallbackPolicy` autenticada.
- **Pesquisa em logs**: policy `logs:investigate` (role + `acr=otp` + `auth_time` <= 5 min), rate limit, auditoria sem valor.
- **Log sem dado pessoal** (Art. IV.6): só campo estruturado aprovado em ADR, pseudonimizado na origem; nunca concatenar.
- Serilog com `preserveStaticLogger: true`: não dependa do `Log.Logger` estático.
- Erros devolvem **códigos** (Angular traduz via `$localize`); VOs e adapters ViaCEP/IBGE são cópias do Legado.
- **Nunca** rode `./scripts/e2e.sh` com provisioner/smoke do `../keycloak` ou outro E2E em execução.
