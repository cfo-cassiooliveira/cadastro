# Resumo — Spec 001 Cadastro com Keycloak e migração sob demanda

**Objetivo**: sistema novo (Cadastro) autenticado só pelo Keycloak, com as funções do Legado (autocadastro, perfil,
listagem admin); usuários do Legado migram sob demanda no primeiro login/reset, sem redefinir senha nem migração em lote.

**Decisões-chave**
- BFF com sessão em memória no servidor, PKCE + PAR (ADR-0001 do cadastro).
- Dados do usuário no User Profile do Keycloak, Cadastro sem banco (ADR-0002).
- Migração: Authenticator no Keycloak consulta API interna do Legado (HTTPS + HMAC); marcação após commit
  (keycloak ADR-0006/0007, legado ADR-0005).
- Nenhum e-mail/login completo em log; identificadores mascarados.

**Contratos** (plan.md §4): `/bff/login|signin-oidc|logout|user`; `GET /api/v1/users/me` (`users:self`);
`POST /api/v1/users` (anônimo, 201/400/409/503); `GET /api/v1/users` (`users:read`, admin, pageSize <= 50);
`/api/v1/addresses/...` (ViaCEP/IBGE); `POST /api/v1/client-logs` (202). Erros em ProblemDetails com códigos estáveis.

**Estado das tarefas** (tasks.md + código)
- Concluídas: T01-T17, T24 (E2E migração), T25 (docs/ADRs), T05b.
- Pendentes: T18 endereços/VOs, T19 `POST /users`, T20 `GET /users`, T21 tela Criar conta, T22 tela Usuários
  (código: só `GET /users/me`, sem Addresses/RegisterUser).
- T23 consta `[ ]` em tasks.md, mas o código já tem `docker-compose.graylog.yml`, Serilog→GELF (`LoggingSetup.cs`),
  pseudonimização e client-logs; tasks.md provavelmente desatualizado (confirmar com o responsável).
- Extra fora das tarefas listadas: pesquisa em logs (`/api/v1/log-search`, DEV-004).

Links: [spec.md](spec.md) · [plan.md](plan.md) · [tasks.md](tasks.md)
