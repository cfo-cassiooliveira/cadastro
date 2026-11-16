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

## Logs (Graylog)

A API usa Serilog: JSON compacto no console e, quando `Graylog:Host` está configurado, GELF por TCP para o Graylog
do projeto [`graylog`](../graylog). Todo evento leva `service_name=cadastro-api`, `service_version` e `environment`;
`trace_id`/`span_id` entram quando há uma Activity (ex.: dentro de uma requisição HTTP) e `correlation_id` só nos
eventos de requisição (`X-Correlation-Id`). Sem `Graylog:Host` os logs ficam só no console.

```bash
# Graylog de pé antes (cria a rede "observability")
docker compose -f docker-compose.yml -f docker-compose.graylog.yml up -d --build
```

O nginx (`cadastro-web`) envia o access log pelo Docker GELF driver (UDP) e cai no stream `Cadastro - Web (nginx)`
como texto. **Pendente (G02 da spec 001 do `../graylog`):** o pipeline ainda não interpreta o JSON do `cadastro-web`
(a regra `20-nginx-json` trata só o `legado-web`), então campos como `http_status` e o alerta de 5xx do nginx ainda
não funcionam para o Cadastro. Streams da API: `Cadastro - API`, `Cadastro - Erros`, `Cadastro - Navegador` e
`Cadastro - Segurança`.

## Health checks (rede interna)

| Endpoint | Verifica |
|---|---|
| `/health/live` | Só o processo |
| `/health/ready` | Dependências obrigatórias |
| `/health` | Relatório completo, com métricas do processo |
