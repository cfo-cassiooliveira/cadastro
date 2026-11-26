# Índice de docs

**Specs**
- 001 Migração Keycloak (em implementação) — specs/001-migracao-keycloak/summary.md
- 002 Importação em lote 1x1 (C2-C5 no Cadastro: upload, lote, consultas, outbox, relay e consumidor) — ../rabbitmq/docs/specs/002-uso-pelos-projetos/spec.md; contratos de API em docs/reference.md
- Importação em lote: guia, fluxo, como rodar e resultados — user-imports.md

**ADRs**
- 0001 BFF com sessão no servidor (aceito) — adr/0001-bff-com-sessao-no-servidor.md
- 0002 Dados no User Profile do Keycloak (aceito) — adr/0002-dados-do-usuario-no-keycloak.md
- 0007 Esquema `import` no banco do Legado, exceção ao Art. III.4 (aceito) — adr/0007-esquema-import-no-banco-do-legado.md
- 0009 Consumidor das linhas da importação no processo da API (aceito) — adr/0009-consumidor-no-processo-da-api.md
- 0010 OTP recente só nas ações da importação; roles atualizadas na renovação da sessão (aceito) — adr/0010-otp-recente-so-nas-acoes-da-importacao.md

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
