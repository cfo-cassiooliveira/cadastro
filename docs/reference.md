# Referência do Cadastro (movida do CLAUDE.md)

> Texto verbatim das seções de referência do `CLAUDE.md`. As regras resumidas ficam no CLAUDE.md; os detalhes, aqui.

## Produto

- **Propósito**: sucessor do `legado`, com as mesmas funcionalidades (autocadastro, perfil e listagem
  administrativa), autenticado só pelo **Keycloak**; os usuários do Legado migram sob demanda no primeiro login.
- **Usuários**: visitante (autocadastro), usuário (perfil), administrador (listagem, OTP obrigatório).
- **Bounded context**: Users (apresentação). Dono dos dados: **nenhum** — o usuário vive no User Profile do
  Keycloak (ADR-0002); o Cadastro não tem banco.
- **Fora de escopo**: edição de perfil, troca de e-mail, login social, produção.
- **Requisitos não funcionais**: dados pessoais (nome, e-mail, celular, endereço) → LGPD; nada disso em log.
- Planejamento: [spec](specs/001-migracao-keycloak/spec.md) · [plano](specs/001-migracao-keycloak/plan.md)
  · [tarefas](specs/001-migracao-keycloak/tasks.md).

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
- Stack completa (ordem no README, "Como subir a stack completa"): `identity-network.sh` → Graylog (opcional) → `../legado`
  com `-f docker-compose.yml -f docker-compose.identity.yml` após `internal-credentials.sh` → `../keycloak` (sempre com
  `-f docker-compose.graylog.yml` se o Graylog estiver no ar; `KC_LEGADO_SIGNING_KEY` copiada de
  `LEGADO_INTERNAL_KEYCLOAK_KEY` do `../legado/.env`, sem exibir) → Cadastro. O Legado nunca depende do Keycloak nem do
  Cadastro.
- **Nunca** rode `./scripts/e2e.sh` enquanto o provisioner ou o smoke do `../keycloak` estiver rodando, nem dois ao
  mesmo tempo: houve falha intermitente `Parent flow doesn't exist`/JSON inválido com execuções concorrentes (causa não comprovada).
- E2E de migração (`e2e/tests/migration.spec.js`, `zz-logs-lgpd.spec.js`; `e2e/README.md`): contas descartáveis `e2e.*` no
  Legado (que não tem exclusão), usuários migrados apagados do Keycloak no fim de cada teste, nenhum e-mail completo no
  Graylog (a conferência `zz-` roda por último). Escopo da DEV-005 sem T21/T22: cenários de Legado fora do ar e admin
  migrado configurando OTP ficam nos ITs do `../keycloak` (`LegacyLoginIT`).
- Health checks: `/health/live`, `/health/ready` e `/health`, só na rede interna.
- Testes de arquitetura carregam cada camada pelo primeiro tipo dela: o Smart App Control do Windows bloqueia
  DLL sem tipos.

## Regras que não podem regredir

- BFF: o navegador nunca recebe token; cookie `__Host-cadastro` (`HttpOnly; Secure; SameSite=Strict`), sessão no
  servidor, PKCE + PAR, `returnUrl` só local, antiforgery (`X-XSRF-TOKEN`) em toda chamada que muda estado — o token
  vem no corpo de `/bff/user` e fica só em memória (nenhum cookie legível por JavaScript),
  `FallbackPolicy` autenticada (health/OpenAPI/endpoints de entrada com `AllowAnonymous` comentado).
- Tokens só com id, login e roles (client `cadastro` sem scopes `profile`/`email`); a sessão guarda só `sub`,
  `preferred_username`, `sid`, `acr`, `auth_time` e roles. Cobertos por `tests/Cadastro.Api.IntegrationTests/Auth/`.
- Pesquisa em logs (`POST /api/v1/log-search/queries` e `GET /api/v1/log-search/access`, `Cadastro.Api/LogSearch`): policy `logs:investigate` = role
  `log-investigator` + `acr=otp` + `auth_time` <= 5 min (`OtpStepUpRequirement`; o BFF pede `/bff/login?acr_values=otp` e
  confere o retorno), antiforgery, rate limit `log-search` (30/h por `sub`), 400 sem eco do valor. Auditoria: um log por
  pesquisa com `audit_event`, `investigator_id` (o `sub`), `data_type`, `case_reference`, `trace_id` e `tokens` como
  **texto único** separado por vírgula (nunca lista: `ParseArrayValues=true`), nunca o valor nem a máscara; o 403 loga
  `log_search_access_denied` só com `investigator_id`. Esses nomes não podem entrar em `PersonalDataFields` (contrato com o
  stream do `../graylog`). `LogSearchFields` deriva de `PersonalDataFields` (sem lista própria). Cobertos por
  `tests/Cadastro.Api.IntegrationTests/LogSearch/` e `Auth/OtpStepUpTests.cs`. A tela `/log-search` (frontend:
  `app/log-search/`, guard `logSearchGuard`, que registra o acesso negado via `GET /api/v1/log-search/access` antes de ir a `/forbidden`) oferece o botão "Confirmar com código" que abre
  `/bff/login?acr_values=otp&returnUrl=%2Flog-search` quando o 403 indica OTP ausente ou vencido; ao confirmar o OTP, o
  formulário volta vazio (o valor é descartado antes de sair da tela); o valor pesquisado nunca vai para URL, storage, `AppLogger` nem console (README).
- **Importação em lote (spec 002 do `../rabbitmq`, C2-C4)**: `/api/v1/user-imports`. **Duas policies**: o grupo inteiro exige
  `user-imports:read` (role `user-importer` + sessão com `acr=otp`, sem limite de idade do `auth_time`) e todo POST (upload,
  confirmar, cancelar, reprocessar e qualquer escrita nova) soma `user-imports:manage` (role + `acr=otp` com `auth_time`
  <= 5 min). GET que passe a devolver dado pessoal tem de ir para `user-imports:manage`. Os 403 das duas vão ao log como
  Warning `user_import_access_denied` só com `actor_id`, um por pedido, pelo `AccessDeniedAuditHandler`
  (`UserImports/UserImportEndpointTests.Authorization.cs` cobre a matriz; ADR-0010). **Renovação da sessão**: o
  `SessionTokenRefresher` valida o access token novo (assinatura, emissor, validade, `azp` e `sub`; falha encerra a sessão) e troca as
  roles da sessão, para todas as áreas (chave desconhecida, como na rotação do realm, busca discovery/JWKS aguardando, no máximo
  uma vez a cada 30 s; a recusa é logada com o motivo, sem o token); `auth_time` é o do login e `acr=otp` só se mantém se o token novo o trouxer. Rotas: `POST /` (multipart, campo `file`; 201 com resumo, 409 `import.duplicate_file`
  com o lote existente, 413/415/422/503 em Problem Details com `code`), `GET /`, `GET /{id}`, `GET /{id}/lines`, `GET /{id}/report`
  (CSV sem dado pessoal), `POST /{id}/confirm` (corpo `{ summaryToken }`: HMAC-SHA256 com chave do servidor `UserImports:SummaryTokenKey` sobre hash, contagens, `batchId` e `sub` do autor, devolvido só no 201 do upload; a API não sobe sem a chave),
  `POST /{id}/cancel`, `POST /{id}/lines/{lineId}/reprocess`, `POST /{id}/reprocess-errors`. **Dono do lote**: só o criador (`sub`) confirma, cancela ou reprocessa (403 `import.not_owner` + Warning); a leitura é aberta a todo `user-importer`. Todo POST exige antiforgery;
  o upload tem rate limit por `sub` (`RateLimiting:UserImportsPermitsPerHour`, padrão 30). Upload em streaming (`MultipartReader`
  para arquivo temporário em `UserImports:TempDirectory` (padrão: temporário do sistema; `/tmp` é tmpfs no compose) apagado ao fechar, inclusive em erro; corpo acima do limite = 413; limite 5 MB também no Kestrel e em `FormOptions`), ClamAV fail-closed
  (`IVirusScanner` -> `ClamdVirusScanner`, INSTREAM, timeout; indisponível/erro = 503 `scanner.unavailable`), CSV (relatório com escape de injeção de fórmula) e CSV de entrada UTF-8 estrito lido em blocos, com cabeçalho
  exato `nome,email,cpf,telefone`, máx. 5.000 linhas, CPF com dígitos verificadores, duplicados por e-mail normalizado e CPF
  (extrator atrás de `IUserExtractor`; no código usamos o nome em inglês do `IExtratorDeUsuarios` da spec). Linha inválida ou
  duplicada **não é gravada** (só contagem e `issues` com número da linha e código, até 200); linha válida grava os dados
  cifrados (contexto AES `{lineId}:{campo}`). Confirmar grava 1 `outbox_messages` por pessoa na **mesma transação** que muda
  o lote (envelope com `data={batchId,lineId}`, UUIDv7, `traceparent` do pedido); reprocessar também enfileira nova mensagem.
- **Relay da outbox (C4)**: `OutboxRelayService` (BackgroundService, só com `RabbitMq:Enabled`) -> `OutboxRelay` lê lotes
  pequenos com `UPDLOCK, READPAST` numa transação, publica por `IOutboxPublisher` (`RabbitMqOutboxPublisher`: uma `IConnection`
  singleton `cadastro-api` com recovery, canal com confirms, persistente, `mandatory`, exchange `cadastro.events`) e só marca
  publicada após o confirm; falha/nack/return mantém pendente com `NextAttemptAtUtc` (backoff exponencial com jitter). Crash entre
  confirm e commit republica (consumidor idempotente). `cadastro-app` não tem configure: **nunca** declarar exchange/fila/binding
  (teste varre o código). Credencial: `RabbitMq__Password` <- `RABBITMQ_CADASTRO_APP_PASSWORD` (`generate-env.sh` copia do
  `../rabbitmq/.env`); rede `workspace-messaging` por `docker-compose.messaging.yml`. O broker aparece só no `/health`
  (tag `external`, `Degraded`), nunca no `/health/ready`. O publicador recria a conexão fechada depois de `RabbitMq:ConnectionRecreateAfterSeconds` (padrão 60), sem vazar a antiga. Riscos aceitos e pendências: ADR-0007. ClamAV: serviço `clamav` do compose (imagem por tag + digest, `mem_limit`, sem porta publicada).
- **Consumidor das linhas (C5a-C5c)**: `UserImportLineConsumerService` (BackgroundService no mesmo processo da API, ADR-0009; só com
  `ImportConsumer:Enabled`, ligado por `docker-compose.import.yml`) consome `cadastro.user-import-lines` (conexão `cadastro-consumer`
  com recovery, canal próprio, `BasicQos` 10, ack manual **depois** do commit; segundo canal com confirms para `cadastro.retry`).
  Por entrega: `LineDeliveryProcessor` (continua o `traceparent`) -> `IProcessUserImportLineHandler`: checa a inbox, decifra os dados,
  `KeycloakUserProvisioner` (client `cadastro-importer`, token em cache, `AddStandardResilienceHandler` 10 s/30 s, limite de 10
  criações/s por `CreationRateLimiter`) busca por `importLineId` e por e-mail exato, cria **sem senha** (`UPDATE_PASSWORD`, atributo
  `importLineId`, username não pessoal `imp.<24 hex>`; o login real é o e-mail) e chama `execute-actions-email?lifespan=259200`
  (72 h, **sempre** com o parâmetro). Achado por `importLineId` = retomada de tentativa interrompida (volta como Created e reenvia o
  e-mail); achado só por e-mail = `AlreadyExists` (nada criado). Marcação no Legado (só quando Created, antes de apagar os dados):
  `LegadoUserDirectory` (HTTPS só com a CA do Legado `Legado:CaCertificatePath`, HMAC `X-Legado-Signature` com nonce novo por
  tentativa porque a assinatura fica DENTRO do pipeline de resiliência, `user-lookups` + `PUT users/{id}/migration`) e
  `LegacyMarkDecision`: um candidato de e-mail exato = `Marked`; zero = `NoCandidate`; vários = `Ambiguous` (aviso); API fora =
  `Failed` (não desfaz o Keycloak nem falha a linha). O contrato atual do Legado devolve no máximo um candidato; a regra de vários já está pronta.
  Efeito, contadores do lote e inbox gravam na **mesma transação** (`UserImportLineCompletion`, retry em `DbUpdateConcurrencyException`
  recarregando; unique da inbox = duplicata, ack sem repetir). Falha transitória (Keycloak 5xx/timeout/429/401/403/circuito aberto) ->
  republica em `cadastro.retry` (mesmo `messageId`/corpo, header `x-retry-count` +1, `traceparent`) e ack; após 5 ciclos
  (`ImportRetryPolicy`) a linha vai a `Error` (`processing.retries_exhausted`) e `BasicNack(requeue:false)` leva à DLQ. Falha
  definitiva (400/422/409 de outra pessoa, mensagem inválida, linha inexistente, dado ilegível) -> linha `Error` com código + nack
  para a DLQ; dados pessoais ficam para o reprocessamento. O consumidor **nunca** declara topologia nem publica em `cadastro.dlx`.
  Falhas de credencial do importador (400/401/403) são operacionais: retry sem consumir ciclo (ADR-0009); falha ao publicar na retry
  espera com backoff e, após 3 vezes, marca a linha `Error` antes de ir à DLQ; reenvio do e-mail só com `UPDATE_PASSWORD` pendente.
  Nome com caractere do `person-name-prohibited-characters` do Keycloak (`<>&"$%!#?§;*~/\|^=[]{}()`) vira `row.name_invalid` já no upload, assim como primeiro nome acima de 100 ou sobrenome acima de 150 caracteres (`PersonNameRules`, limites do User Profile).
  Categoria `Microsoft.EntityFrameworkCore` em Warning no `appsettings.json` (o polling do relay gerava dezenas de milhares de linhas Information).
  Segredos: `KC_CADASTRO_IMPORTER_CLIENT_SECRET` e `LEGADO_INTERNAL_CADASTRO_KEY` (`generate-env.sh` copia do `../keycloak` e do
  `../legado-cadastro-usuarios`). Testes: `Cadastro.Infrastructure.Tests/UserImports/` (RabbitMQ + Keycloak + Mailpit + SQL Server reais).
- `Bff:LocalSecureContext` só no compose local (http://localhost é contexto seguro para o navegador).
- Log **sem dado pessoal** (Constituição, Art. IV.6). Só um dado **aprovado em ADR** vai ao log, e apenas como
  campo estruturado com o nome do tipo (`{Cpf}`, `{Cnpj}`, `{Phone}`/`{MobilePhone}`, `{UserName}`, `{Email}`) no
  message template (`[LoggerMessage]`), **nunca** concatenado ou interpolado. Objeto destruturado (`{@Obj}`), dicionário e lista são tratados (máscara no próprio nível e token de topo `Obj_Campo_token`), mas dado pessoal não deve ir em log.
  O Cadastro usa Serilog (`Cadastro.Api/Common/LoggingSetup.cs`, mesmo padrão do Legado): JSON no console e, com
  `Graylog:Host` configurado (`docker-compose.graylog.yml`), GELF TCP com cada propriedade como campo próprio e
  `service_name=cadastro-api`. Por isso a máscara por nome de campo do `../graylog` (ADR-0002, listas `LGPD_*` do
  `provisioning/provision.sh`) vale para os logs do Cadastro; nome fora dessa lista não tem máscara garantida.
  Esses campos são **pseudonimizados na origem** (ADR-0003 do `../graylog`): `PseudonymizationEnricher`
  (`Cadastro.Api/Common`, último enricher) troca o valor pela máscara e soma `<Campo>_token` pela porta
  `ILogPseudonymizer` (`Cadastro.Application/LogSearch`; HMAC em `Cadastro.Infrastructure/Logging/LogPseudonymizer.cs`,
  mesmo algoritmo e mesma chave do `../legado`). Token aninhado vai como propriedade de topo (`Obj_Campo_token`), nunca
  no texto. Chave em `Logging:Pseudonymization` (user-secrets/`.env` via `./scripts/generate-env.sh`), validada na
  subida sem expor o valor. Cobertos por `Logging/PseudonymizationTests.cs` (vetores do plano) e
  `Logging/PseudonymizedGelfTests.cs` (GELF TCP real). Único campo pessoal em fluxo real hoje: `UserName` (claim
  `preferred_username`) no login ok (Information) e no acesso negado (Warning) de `BffAuthenticationSetup`; sai só
  mascarado + `UserName_token` (sem CPF/e-mail/telefone), coberto por `Logging/AuthFlowPseudonymizationTests.cs`
  (login e acesso negado reais, GELF, fora de formato e contrato de nomes). No Graylog, `ClientSource`, `RequestPath`,
  `case_reference` e `http_path` estão em `LGPD_TEXT_FIELDS` (texto varrido pela máscara). Os client-logs
  (`POST /api/v1/client-logs`, `ClientLogs/`) também são mascarados na origem: `ClientMessage`, `ClientSource` e
  `ClientStack` passam por `PersonalDataTextMasker` (e-mail inclusive `%40`, CNPJ, CPF e celular com DDD, mesmos padrões
  do `lgpd-text.rule.tmpl` do `../graylog`) usando a máscara do ADR-0002 via `ILogPseudonymizer` (sem token em texto
  livre); trecho fora do formato vira `***`, estouro de tempo da regex (100 ms) troca o campo por `***` com Warning sem o
  texto, e o `ClientSource` perde query string e fragmento. Cobertos por `Logging/ClientLogEndpointTests.cs` e
  `Logging/PersonalDataTextMaskerTests.cs`.
- Serilog com `preserveStaticLogger: true`: nunca dependa do `Log.Logger` estático. Campos padrão e Graylog opcional
  cobertos por `tests/Cadastro.Api.IntegrationTests/Logging/` (os testes leem os eventos reais pelo sink
  `CollectingLogSink`; o `FakeLogCollector` recebe os mesmos eventos pelo `FakeLogCollectorSink`).

## Decisões e particularidades

- O contrato de erros devolve **códigos** e o Angular traduz via `$localize` (mesmo padrão do Legado).
- Value Objects e adapters ViaCEP/IBGE são **cópias** dos do Legado de propósito: não há biblioteca compartilhada.
