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

## Health checks (rede interna)

| Endpoint | Verifica |
|---|---|
| `/health/live` | Só o processo |
| `/health/ready` | Dependências obrigatórias |
| `/health` | Relatório completo, com métricas do processo |
