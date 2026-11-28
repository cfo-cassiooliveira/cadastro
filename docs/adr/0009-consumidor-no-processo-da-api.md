# ADR-0009: Consumidor das linhas da importação no mesmo processo da API

- **Status**: Aceito (2026-11-25; tarefas K1/C5a, C5b e C5c da spec 002 do rabbitmq)
- **Data**: 2026-11-25
- **Decisores**: Cassio Oliveira (responsável técnico)
- **Artigos da Constituição relacionados**: Art. III.5, Art. VII.1-VII.4, Art. IV.4, Art. IV.6
- **Relacionados**: ADR-0007 (esquema `import`), ADR-0005 e ADR-0006 do `rabbitmq`

## Contexto

O plano da spec 002 prevê o consumidor como `BackgroundService` do Cadastro. A arquitetura-padrão (`architecture.md`) admite
um projeto `Worker` separado para consumidores. O MVP vence em 04/12/2026, o Cadastro já hospeda o relay da outbox como
`BackgroundService` na API e um segundo executável exigiria outra imagem, outro compose, mais segredos e health checks.

## Decisão

1. O consumidor roda **no mesmo processo da API**, como `BackgroundService` ligado por `ImportConsumer:Enabled` (desligado por
   padrão; `docker-compose.import.yml` liga). A lógica fica na Application (`ProcessUserImportLineHandler`) e a Infrastructure só
   traduz AMQP, então mover para um `Worker` depois é só trocar o composition root.
2. Idempotência pela inbox (`messageId` na mesma transação do efeito); retry por fila com TTL (30 s, 5 ciclos) e DLQ; conexão
   `cadastro-consumer` separada da `cadastro-api` do relay.
3. Usuário criado no Keycloak pelo client `cadastro-importer`; achado por `importLineId` retoma a linha (reenvia o e-mail), achado só
   por e-mail vira "já existente". A marcação no Legado nunca falha a linha.

## Alternativas consideradas

| Alternativa | Prós | Contras | Motivo da rejeição |
|---|---|---|---|
| Projeto `Cadastro.Worker` separado | Escala e falha independentes da API | Nova imagem, compose, segredos e health antes de 04/12 | Prazo; fica como destino se o volume exigir |
| Consumidor sem inbox (só atributo no Keycloak) | Menos código | Não cobre a marcação no Legado nem os contadores do lote | Viola Art. VII.2 |

## Consequências

- **Positivas**: um único artefato, mesma configuração e mesmos logs; relay e consumidor compartilham o banco e o composition root.
- **Negativas / riscos**: o consumo compete por CPU/memória com a API (teto de `mem_limit` do container); reiniciar a API
  interrompe o consumo (mensagens sem ack voltam à fila e a inbox torna a repetição segura). O consumidor não tem health check
  próprio: a queda da conexão é reconectada pelo serviço e logada em Error. Pendência: métrica/alerta de DLQ (tarefa O1).
- **Riscos aceitos**: `manage-users` do `cadastro-importer` permite mais que criar usuário e enviar e-mail (o código só usa essas
  duas operações e a busca); a API interna do Legado ainda não devolve vários candidatos (a regra de ambiguidade já existe).

## Revisão da Onda 3 (correções)

- **Credencial recusada não queima o lote**: 400/401/403 no token endpoint (segredo errado, scope removido) e 403 da Admin API
  (role removida) viram `IdentityAccessDeniedException`. A mensagem volta pela retry **sem consumir um dos 5 ciclos**
  (`LineProcessingResult.CountsAsCycle = false`; o `x-retry-count` não avança), com log Error por tentativa. Enquanto a configuração
  estiver errada as linhas ficam Pending (nunca Error) e o lote segue em Processing até alguém corrigir; o custo é uma volta a cada
  30 s por mensagem. Alternativa descartada: pausar o consumo (mais código e estado). Um 401 isolado descarta o token em cache e repete a
  chamada uma vez; só o segundo 401 vira "acesso recusado".
- **Falha ao publicar na retry**: o consumidor espera com backoff e jitter (`ImportConsumer:RepublishBackoffMs`) e devolve a
  mensagem à fila; depois de `MaxRepublishFailures` (padrão 3, menor que o `delivery-limit` 5 da fila; vale também o
  `x-delivery-count` do broker) a linha é encerrada como Error (`processing.failed`, ou `processing.retries_exhausted` na última
  volta) por `IFailUserImportLineHandler` **antes** do `nack requeue:false`. Se nem isso grava (banco fora), a mensagem
  continua viva em vez de ir à DLQ com a linha Pending. Risco residual: banco e broker fora por muito tempo podem esgotar o
  `delivery-limit` da fila antes de a marcação funcionar. O ack que falha com o canal aberto é repetido uma vez após uma pausa;
  a reentrega seguinte cai na inbox como duplicata.
- **Reenvio do e-mail na retomada**: usuário achado por `importLineId` só recebe o e-mail de novo se `UPDATE_PASSWORD` ainda está
  pendente, e o reenvio passa pelo mesmo limitador de 10/s da criação. Se o commit falhar depois do envio a ação continua pendente e
  um ciclo seguinte pode reenviar (no máximo uma vez por ciclo); aceito, pois a Admin API não diz se o e-mail saiu.
- **409 no POST**: já é desta linha = retomada; e-mail de outra pessoa (corrida) = `AlreadyExists`; só sem nenhum dos dois é `identity.rejected`.
- **Ciclos esgotados sem conseguir marcar a linha (DEV-021)**: antes, ao fim dos 5 ciclos o `LineDeliveryProcessor` recusava a
  mensagem (DLQ) mesmo quando `IFailUserImportLineHandler` lançava (banco fora) ou devolvia `RetryLater` (10 conflitos seguidos). A
  linha ficava Pending, o lote em Processing para sempre (sem Reprocessar nem cancelar pela tela) e a retenção nunca chegava aos dados
  cifrados (LGPD). Agora vale a mesma regra do caminho da retry não publicada: **a mensagem só vai à DLQ com a linha encerrada**. Se a
  marcação não grava, a mensagem volta pela fila de retry com `x-retry-count` = 6 (`MaxCycles + 1`, fixo) e a entrega seguinte
  **só tenta marcar a linha** (`processing.retries_exhausted`), sem reprocessar nem criar usuário. Cada volta passa pelo TTL de
  30 s da retry e é uma mensagem nova (o `x-delivery-count` da fila principal zera, então o `delivery-limit` 5 não é gasto), o
  contador não cresce e não há laço quente. Com o banco de volta, a linha vira Error e a mensagem vai à DLQ como antes. A linha
  que esgota os ciclos dentro do caso de uso (`RetryOrFailAsync`, conflito) e volta com o contador 6 também cai nesse caminho e é
  encerrada como `processing.retries_exhausted` sem nova tentativa, que já era a intenção no ciclo 5.
  **Riscos residuais**: (1) banco fora por muito tempo = uma volta a cada 30 s por mensagem presa, com log Error
  (`Could not mark import line ... as failed`) e Warning (`... is still pending after the retries were exhausted`) a cada volta,
  sem limite de tempo, como já acontece com a credencial recusada; (2) se a publicação na retry também falhar (broker instável),
  vale o risco já aceito acima do `delivery-limit` da fila; (3) mensagens que foram à DLQ **antes** desta correção deixaram linhas
  presas, que precisam do procedimento manual em `docs/user-imports.md` (seção "Linhas presas em Pendente"); (4) a volta da fila
  de retry para a principal é at-most-once (a policy `cadastro-retry`, no repositório `rabbitmq`, não usa
  `dead-letter-strategy: at-least-once`): como a mensagem pode agora dar muitas voltas, uma perda nesse trecho deixaria a linha
  presa. Correção pendente na policy do broker.
- **LGPD (Art. IV.6)**: a busca por e-mail usa `GET /admin/realms/{realm}/users?email=...&exact=true`, ou seja, o e-mail vai na query
  string da Admin API. O log do `HttpClient` fica em Warning (não registra a URL), mas é preciso **conferir e, se necessário,
  desligar/mascarar o access log do Keycloak e de qualquer proxy** à frente dele. Pendência operacional antes de produção.
