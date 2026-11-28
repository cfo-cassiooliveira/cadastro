# ADR-0007: Esquema `import` no banco do Legado (exceção ao Art. III.4)

- **Status**: Aceito (2026-11-25; decisão Q10 da spec 002 do rabbitmq)
- **Data**: 2026-11-25
- **Decisores**: Cassio Oliveira (responsável técnico)
- **Artigos da Constituição relacionados**: Art. III.4 (exceção), Art. IV.4, Art. IV.6, Art. VII.2-VII.3, Art. VIII
- **Relacionados**: ADR-0002 (Cadastro sem banco; esta decisão o ajusta só para a importação), ADR-0005 e ADR-0006 do `rabbitmq`

## Contexto

A importação em lote (spec 002 do `rabbitmq`) precisa de estado durável: lote, linhas, Outbox, Inbox e auditoria
(Art. VII.2-VII.3). O ADR-0002 definiu que o Cadastro não tem banco, e o Art. III.4 proíbe banco compartilhado entre
serviços. O MVP vence em 04/12/2026 e já existe um SQL Server operado, com backup, na rede de dados do Legado
(`LegadoUsers`). Subir e operar um segundo SQL Server agora custa mais do que o prazo comporta.

## Decisão

1. O Cadastro persiste a importação no banco `LegadoUsers`, **somente no esquema `import`**, com EF Core 10 (SQL Server).
   Tabelas: `import_batches`, `import_lines`, `outbox_messages`, `inbox_messages`, `audit_entries` e o histórico de
   migrations `import.__EFMigrationsHistory`.
2. **Dois logins com privilégio mínimo**, criados pelo Legado junto com o esquema:
   - `cadastro_migrator`: cria/altera tabelas no esquema `import` (só nas migrations, projeto `Cadastro.Database`;
     variável `CADASTRO_MIGRATOR_CONNECTION`);
   - `cadastro_app`: apenas `SELECT/INSERT/UPDATE/DELETE` em `import`, **sem acesso ao `dbo`** (runtime;
     `ConnectionStrings:ImportDb`). Sem DDL em runtime e sem `Migrate()` no startup.
3. O Legado não lê nem escreve o esquema `import` (`legacy_app` com DENY); o Cadastro não lê nem escreve o `dbo`.
   A integração com os dados do Legado continua pela API interna (marcação de login migrado), nunca por SQL.
4. Dados pessoais da linha (nome, e-mail, CPF, telefone) ficam **cifrados** com AES-256-GCM (porta
   `IPersonalDataProtector`), chave em variável de ambiente (`PersonalData__Protection__Keys__<versão>`), versão da
   chave gravada em cada registro, nonce aleatório por valor e o contexto (`lineId` + campo) autenticado junto. A API
   não sobe sem a chave atual. O e-mail tem um índice cego (HMAC-SHA256 do e-mail normalizado em trim + NFC + minúsculas,
   subchave por HKDF) para busca por e-mail; a **versão da chave do índice é gravada na linha** (`EmailIndexKeyVersion`) e a
   busca recalcula o índice com a versão da linha. Versão desconhecida falha de forma explícita, nunca em silêncio.
5. Retenção: relatório por linha 90 dias (`ReportExpiresAtUtc`), auditoria 12 meses (`ExpiresAtUtc`). Dados pessoais
   apagados ao fim da linha **criada ou já existente**. **Linha com erro definitivo retém os dados cifrados** para o
   reprocessamento manual (Q8) até o job de limpeza ou 90 dias após o fim do lote; cada reprocesso reabre a linha
   (`Error -> Pending`, contador `ReprocessCount`), reabre o lote (`CompletedWithErrors -> Processing`) e grava auditoria.
   **`Cancel()` do lote apaga os dados das linhas pendentes** e as marca como `Cancelled`. A spec 002 (Q9) foi emendada.
6. Concorrência: `import_batches` tem `rowversion`; consumidores em paralelo que atualizam contadores/estado recebem
   `DbUpdateConcurrencyException` e repetem a leitura (nenhuma atualização perdida).
   **Revisão (2026-11-27)**: os contadores passaram a ser atualizados por `UPDATE` atômico no término de cada linha, não mais
   por `rowversion` com repetição.
7. Segredos (senhas dos logins, chave de cifra) só por variável de ambiente, user-secrets ou `.env` gerado por
   `scripts/generate-env.sh`; nunca versionados.

## Plano de saída

Esta exceção é temporária. O esquema `import` migra para um **banco próprio do Cadastro** quando ocorrer o primeiro de:
(a) o Legado deixar de existir (fim da migração de usuários); (b) o ADR-0008 (evento `identity.user.migrated.v1`) entrar,
removendo a necessidade de consultar/marcar o Legado. A migração é cópia das tabelas `import.*` para o novo banco e troca
da connection string; o código não muda, porque nada depende do banco do Legado além da connection string.

## Riscos

- **Rede `workspace-data` compartilhada**: o alias `legado-sqlserver` fica numa rede Docker externa; qualquer container
  ligado a ela alcança `sqlserver:1433` e pode tentar o login `sa` (e os dois logins do Cadastro). Mitigação: senha
  forte do `sa` (gerada pelo Legado, nunca padrão), rede dedicada e só local (nenhum serviço público entra nela; o
  `docker-compose.data.yml` do Cadastro põe só a `api` nela) e porta 1433 publicada apenas em `127.0.0.1`. Em produção,
  rede privada própria entre API e banco, com TLS validado (hoje `Encrypt=True;TrustServerCertificate=True`, que cifra mas não
  autentica o servidor).
- **Dado pessoal retido em linha com erro**: os dados cifrados ficam até o job de limpeza ou 90 dias do fim do lote
  (`ReportExpiresAtUtc`). Sem o job, ficariam indefinidamente: por isso o job de limpeza é obrigatório antes da
  produção (tarefa rastreada em `tasks.md` da spec 002).
- **Rotação da chave**: linhas antigas só decifram e só têm índice recalculável enquanto a chave da versão gravada
  continuar configurada; remover `k1` antes de esgotar a retenção torna essas linhas ilegíveis (falha explícita).

### Riscos aceitos e pendências da importação (revisão da Onda 2, C2-C4)

- **`TrustServerCertificate=True` na connection string** (`scripts/generate-env.sh`): o TLS cifra, mas não autentica o servidor
  (Art. IV, OWASP A02). Aceito só no ambiente local. **Ação de saída, antes de qualquer produção**: certificado confiável no SQL
  Server da rede `workspace-data` (CA própria instalada na imagem da API), `TrustServerCertificate=False` e `Encrypt=True`
  validado; rastreado junto da saída da rede compartilhada. Prazo: antes do primeiro deploy fora da máquina local.
- **CPF e e-mail em claro no arquivo temporário (L4)**: durante a requisição, o CSV existe em arquivo temporário sem cifra (apagado
  ao fechar, inclusive em erro). Mitigação já aplicada: o `/tmp` do container da API é `tmpfs` (memória, nunca disco).
  Pendente: varredura de resíduos na subida (crash deixa arquivo) e, fora do compose, apontar `UserImports:TempDirectory` para
  tmpfs/volume cifrado.
- **Transação aberta durante a publicação (L5)**: o relay segura a transação SQL (com `UPDLOCK, READPAST`) enquanto aguarda os
  confirms (até `ConfirmTimeoutSeconds` por mensagem, em lote). `READPAST` evita que outras instâncias bloqueiem, mas a transação é
  longa. Ação futura: reservar o lote (lease com `NextAttemptAtUtc`), publicar fora da transação e marcar em seguida; a
  republicação já é tolerada pelo consumidor idempotente.
- **Sem alerta nem dead-letter para outbox com muitas tentativas (L6)**: o backoff chega a 5 minutos e não há teto de tentativas
  nem alerta. Ação futura (junto do O1 da spec): métrica de mensagens pendentes/idade, alerta no Graylog por `Attempts` alto e
  destino de quarentena para mensagem que nunca publica (ex.: tipo sem rota).
- **Multipart lido antes do antiforgery (L8)**: sem o header `X-XSRF-TOKEN`, o `IAntiforgery` pode ler o corpo do formulário antes
  de recusar (até o limite de 6 MB). Só afeta usuários autenticados com a role e o OTP recente (a autorização roda antes); o
  rate limit por `sub` contém o abuso. Ação futura: validar o header sem tocar no corpo (recusar cedo se o header faltar).
- **Dono do lote**: só o criador (`sub`) confirma, cancela ou reprocessa o lote (403 `import.not_owner` e Warning para os demais);
  a leitura (lista, detalhe, linhas, relatório) é aberta a todo `user-importer`. O token do resumo é HMAC-SHA256 com chave do
  servidor (`UserImports:SummaryTokenKey`) sobre hash, contagens, `batchId` e `sub`, e só aparece na resposta do upload.

## Alternativas consideradas

| Alternativa | Prós | Contras | Motivo da rejeição |
|---|---|---|---|
| Banco próprio do Cadastro agora | Respeita o Art. III.4 | Novo SQL Server, backup, rede e segredos antes de 04/12 | Prazo; fica como destino do plano de saída |
| Estado em memória/arquivo | Sem banco | Perde lote no restart; sem Outbox/Inbox transacionais | Viola Art. VII.2-VII.3 |
| Estado no Keycloak | Sem banco novo | Não é transacional nem consultável por lote | Inadequado |
| Tabelas no `dbo` do Legado | Mais simples | Mistura donos e permissões | Esquema separado dá fronteira e DENY verificável |

## Consequências

- **Positivas**: Outbox/Inbox transacionais, relatório e auditoria duráveis, sem novo componente de infraestrutura.
- **Negativas / riscos**: acoplamento operacional ao SQL Server do Legado (disponibilidade e backup compartilhados); a API
  agora precisa do banco e da chave para subir; senhas dos dois logins precisam ser distribuídas entre repositórios.
- **Ações decorrentes**: Legado cria o esquema, os logins e a rede (L1/L2); `generate-env.sh`, `docker-compose.yml` e `docker-compose.data.yml`
  passam as connection strings e a chave; job de limpeza por retenção (pendente); ADR-0008 e a saída do banco compartilhado.
