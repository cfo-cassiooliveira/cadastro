# Índice de docs

**Specs**
- 001 Migração Keycloak (em implementação) — specs/001-migracao-keycloak/summary.md

**ADRs**
- 0001 BFF com sessão no servidor (aceito) — adr/0001-bff-com-sessao-no-servidor.md
- 0002 Dados no User Profile do Keycloak (aceito) — adr/0002-dados-do-usuario-no-keycloak.md

**Comandos** (na raiz; detalhes no CLAUDE.md)
- `dotnet build Cadastro.slnx --nologo -v q -warnaserror`
- `dotnet test --solution Cadastro.slnx --no-logo -v q`
- `cd cadastro-frontend && npx ng test --watch=false`
- `./scripts/e2e.sh` · `./scripts/sonar-analysis.sh`

**Onde procurar**
- Regras, produto, glossário, stack: reference.md
- Contratos de API: specs/*/plan.md §4
- Critérios de aceite/UX: specs/*/spec.md
- Tarefas: specs/*/tasks.md
