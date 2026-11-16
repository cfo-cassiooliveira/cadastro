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

**Dado pessoal aprovado em ADR** (CPF, CNPJ, telefone, login, e-mail) só vai como campo estruturado com o nome do tipo
(`{Cpf}`, `{Cnpj}`, `{Phone}`/`{MobilePhone}`, `{UserName}`, `{Email}`) e é **pseudonimizado na origem**
(ADR-0003 do `../graylog`, mesmo algoritmo do `../legado`): o campo sai com a máscara (`***.456.789-**`,
`12.3**.***/***1-90`, `11*****4321`, `mar***`, `ana***@gmail`) e ganha `<Campo>_token` (`k1-` + HMAC-SHA256 truncado),
que acha todos os eventos do titular nos dois sistemas com igualdade exata (e-mail e login geram o mesmo token). Vale
também dentro de objetos destruturados, dicionários e listas: o campo mascarado é gravado no Graylog como
`Customer_Email` e o token vai no topo do evento como `Customer_Email_token` (nunca dentro do objeto, para não aparecer
no texto). O texto (`message`/`full_message`) sai só com a máscara; valor fora do formato vira `***`, sem token. No
texto dos escopos (`Scope`), o valor é trocado pela máscara ou por `***`, exceto o curto (login fora do formato, com menos de 4
caracteres; CPF/CNPJ/telefone fora do formato com menos de 8 dígitos no valor, contando só dígitos), por isso dado pessoal nunca vai em template de escopo. Coberto
por `tests/Cadastro.Api.IntegrationTests/Logging/PseudonymizationTests.cs` (vetores do plano, iguais aos do legado) e
`PseudonymizedGelfTests.cs` (servidor GELF TCP falso: nenhum campo ou texto com valor em claro, nenhum token no texto).
**Não é tratado na origem**: a exceção (mensagem e stack trace seguem como estão, por isso nunca devem conter dado
pessoal) e nomes de campo fora das listas; o Graylog continua mascarando o que escapar.

| Variável (`.env`) | Obrigatória | Para quê |
|---|---|---|
| `LOG_PSEUDONYMIZATION_KEY_K1` / `LOG_PSEUDONYMIZATION_KEY_K2` | A do `CURRENT_KEY_ID` (a API não sobe sem ela) | Chave HMAC (Base64, 32 bytes) dos `*_token`; a mesma do `../legado` no ambiente. `./scripts/generate-env.sh` copia do `../legado/.env` ou gera a `k1`, e grava as chaves no user-secrets da API (pela entrada padrão, nunca na linha de comando) |
| `LOG_PSEUDONYMIZATION_CURRENT_KEY_ID` | Não (padrão `k1`) | Id da chave que emite os tokens |

Rotação anual: `../legado/scripts/log-pseudonymization-key.sh k2`, depois `./scripts/generate-env.sh` (copia a `k2`) e
`LOG_PSEUDONYMIZATION_CURRENT_KEY_ID=k2` nos dois `.env`. O Cadastro pesquisa com todas as chaves configuradas, então
mantém a `k1` até o fim da retenção dos logs (30 dias) e só depois ela é apagada. Chave vazia conta como ausente; chave
inválida ou ausente derruba a subida com mensagem que cita só o id, nunca o valor.

O nginx (`cadastro-web`) envia o access log JSON pelo Docker GELF driver (UDP) para o stream `Cadastro - Web (nginx)`;
a regra `20-nginx-json` do `../graylog` interpreta o JSON (`http_status`, `http_path`...) e alimenta o alerta
`Cadastro - Erros 5xx no nginx`. Streams da API: `Cadastro - API`, `Cadastro - Erros`, `Cadastro - Navegador`,
`Cadastro - Segurança` e `Cadastro - Pesquisa em logs (LGPD)` (o stream e o alerta já existem no graylog; o Cadastro só passa a alimentá-los na C03, com `audit_event=log_search_query_issued`/`log_search_access_denied` e tokens como texto único).
Textos livres como `RequestPath` e `ClientSource` também passam pela máscara do pipeline (`LGPD_TEXT_FIELDS`).

## Health checks (rede interna)

| Endpoint | Verifica |
|---|---|
| `/health/live` | Só o processo |
| `/health/ready` | Dependências obrigatórias |
| `/health` | Relatório completo, com métricas do processo |
