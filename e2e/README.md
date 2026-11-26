# E2E do Cadastro

Playwright (desktop, tablet e celular) + axe-core, rodando no container oficial do Playwright. Só o Docker é necessário.

## Como rodar

Com a stack de pé (`../keycloak`, `../legado` com `docker-compose.identity.yml`, `../graylog`, Mailpit do Keycloak e
este projeto em `http://localhost:4300`), na raiz do `cadastro`:

```bash
./scripts/e2e.sh                          # tudo, nos 3 dispositivos
./scripts/e2e.sh --project=desktop        # só um dispositivo
./scripts/e2e.sh --project=desktop migration.spec   # só um arquivo
```

O script lê `KC_AUTOMATION_CLIENT_SECRET` (`../keycloak/.env`) e `GRAYLOG_ROOT_PASSWORD` (`../graylog/.env`) e os repassa
ao container sem imprimir. Endereços e variáveis opcionais estão no cabeçalho de `scripts/e2e.sh`. Sem esses segredos, os
testes de migração são pulados e só os de telas públicas rodam.

## O que a migração cobre (`tests/migration.spec.js`, `tests/zz-logs-lgpd.spec.js`)

Login do Legado migrando para o Keycloak (login e e-mail), senha fora da política, senha errada e usuário inexistente,
reset de senha pelo Mailpit (e reset de inexistente sem e-mail), aviso e redirecionamento no Legado, segundo login sem
consultar o Legado (conferido pelo `e2e.sh` nos logs da API do Legado), bloqueio após 5 falhas e LGPD nos logs do Graylog.

## Dados de teste

- Cada teste cria os próprios usuários no Legado (`e2e.<execução>.<cenário><dispositivo>`, e-mail `@example.test`) com
  senha gerada na hora; nada fixo no repositório.
- No Keycloak, os usuários criados pela migração são apagados no final de cada teste (Admin API).
- O **Legado não tem exclusão**: as contas descartáveis `e2e.*` ficam no banco dele (e na lista do Legado).
- O Legado limita 10 chamadas de credenciais por minuto por IP; os testes se limitam a 8/min, por isso a execução é lenta.
- Artefatos locais (`e2e/.artifacts/`, `e2e/test-results/`) não são versionados.

## Não coberto

- Legado fora do ar/timeout: exigiria parar o container; coberto em `keycloak` (`LegacyLoginIT`).
- Admin do Legado configurando OTP: o Legado não cria Admin pela API; coberto em `LegacyLoginIT`.

## X01: pesquisa por token com eventos reais (`tests/zz-x01-log-search.spec.js`)

Só no projeto `desktop` (`./scripts/e2e.sh --project=desktop zz-x01`). Cria o usuário `x01qa<epoch>` no Legado, loga no Legado e
no Cadastro (migração) e cria um investigador (role `log-investigator` + OTP) no Keycloak; pesquisa pela tela `/log-search` o
token de login, e-mail e celular, confere os eventos reais no Graylog, a trilha de auditoria e que nenhum valor de teste aparece
em claro. O investigador e o usuário migrado são apagados do Keycloak; a conta do Legado precisa de limpeza por SQL
(`DELETE FROM AspNetUsers WHERE UserName LIKE 'x01qa%'`, com `sqlcmd -I`).

## Importação em lote (`tests/user-imports.spec.js`)

Telas `/imports`, `/imports/new` e `/imports/:id` (role `user-importer`). A sessão do BFF e a API são **simuladas no navegador**
(`support/import-api-mock.js`, `page.route`), então não exige Keycloak, ClamAV, SQL Server nem RabbitMQ, só o frontend em
`E2E_BASE_URL`; o polling de 3 s usa `page.clock`. Cobre envio, resumo, confirmação (com antiforgery), acompanhamento até
concluir com erro, relatório CSV, reprocessar, cancelar, lista, axe e rolagem horizontal nos 3 dispositivos, e a ausência do
menu/rota sem a role. `E2E_SHOTS_DIR=<pasta>` grava um screenshot por estado e dispositivo. A ponta a ponta real (OTP, ClamAV,
consumidor) fica para o E2E da stack completa.

## Importação em lote REAL (`import-real/`, `scripts/e2e-import.sh`)

Ponta a ponta com a stack real (BFF, ClamAV, SQL Server do Legado, RabbitMQ, Keycloak, Mailpit, Legado): CSV fictício de 5.000 linhas pela tela, conferência no Keycloak/Mailpit/Legado, reenvio (409), idempotência, Keycloak parado no meio de um lote, DLQ e varredura LGPD de logs e payloads AMQP. Roda no **host** (usa o CLI do Docker) e não faz parte de `./scripts/e2e.sh`. Resultados em `e2e/.artifacts/import-real/results.json`; relatório consolidado em `../rabbitmq/docs/specs/002-uso-pelos-projetos/e2e-report.md`. Leva ~14 min (10 criações/s).
