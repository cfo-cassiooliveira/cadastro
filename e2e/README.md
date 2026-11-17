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
