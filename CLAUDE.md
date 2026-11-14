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
- Health checks: `/health/live`, `/health/ready` e `/health`, só na rede interna.
- Testes de arquitetura carregam cada camada pelo primeiro tipo dela: o Smart App Control do Windows bloqueia
  DLL sem tipos.

## Estrutura

```
cadastro-backend/src/    Cadastro.Domain, .Application, .Infrastructure, .Api (BFF)
cadastro-backend/tests/  Api.IntegrationTests, ArchitectureTests (+ Domain.Tests e Application.Tests com o 1º tipo)
cadastro-frontend/       Angular 22 + nginx
docs/adr/                ADR-0001 (BFF), ADR-0002 (dados no Keycloak)
docs/specs/              Spec 001 (migração para o Keycloak)
```

## Comandos

```bash
dotnet build Cadastro.slnx -warnaserror
dotnet test --solution Cadastro.slnx
dotnet format Cadastro.slnx --verify-no-changes
cd cadastro-frontend && npm ci && npx ng test --watch=false && npx ng lint && npm run lint:styles && npx ng build
./scripts/generate-env.sh && ./scripts/identity-network.sh # .env (segredo copiado do ../keycloak/.env) e rede compartilhada
docker compose up -d --build                               # http://localhost:4300 (Keycloak de pé em ../keycloak)
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
  `preferred_username`, `sid` e roles. Cobertos por `tests/Cadastro.Api.IntegrationTests/Auth/`.
- `Bff:LocalSecureContext` só no compose local (http://localhost é contexto seguro para o navegador).
- **Dado pessoal em log, quando inevitável, só como campo estruturado** com o nome do tipo: `{Cpf}`, `{Cnpj}`,
  `{Phone}`/`{MobilePhone}`, `{UserName}`, `{Email}` no message template (`[LoggerMessage]`), **nunca**
  concatenado ou interpolado no texto. O Graylog mascara pelo nome do campo e troca o mesmo valor dentro do texto,
  sem adivinhar o tipo (ADR-0002 do `../graylog`). Nome fora dessa lista não tem máscara garantida: nome novo entra
  antes nas listas `LGPD_*` do `../graylog/provisioning/provision.sh`.

## Decisões e particularidades

- O contrato de erros devolve **códigos** e o Angular traduz via `$localize` (mesmo padrão do Legado).
- Value Objects e adapters ViaCEP/IBGE são **cópias** dos do Legado de propósito: não há biblioteca compartilhada.
