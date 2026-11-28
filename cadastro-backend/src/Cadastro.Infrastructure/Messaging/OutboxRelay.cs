using Cadastro.Application.UserImports;
using Cadastro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Messaging;

/// <summary>Resultado de um ciclo do relay.</summary>
/// <param name="Selected">Mensagens lidas.</param>
/// <param name="Published">Confirmadas pelo broker e marcadas como publicadas.</param>
/// <param name="BrokerUnavailable">Nada foi publicado e todas as falhas foram de conexão/canal: o serviço deve pausar antes de tentar de novo.</param>
internal readonly record struct OutboxRelayCycle(int Selected, int Published, bool BrokerUnavailable);

/// <summary>
/// Relay da outbox: a confirmação do lote grava lote e mensagens na mesma transação, e este ciclo as publica depois (sem dual write).
/// <para>
/// Lê pendentes com <c>UPDLOCK, READPAST</c> (outra instância pula as linhas travadas) e só marca como publicada o que o broker
/// confirmou; o resto volta com backoff. Uma queda entre o confirm e o commit republica, e o consumidor descarta pelo <c>messageId</c>.
/// </para>
/// <para>
/// Custos (ADR-0007, riscos L5 e L6): a transação fica aberta à espera dos confirms; não há teto de tentativas nem alerta.
/// </para>
/// </summary>
internal sealed partial class OutboxRelay(
    CadastroDbContext db,
    IOutboxPublisher publisher,
    TimeProvider timeProvider,
    IOptions<RabbitMqOptions> options,
    ILogger<OutboxRelay> logger)
{
    /// <summary>Executa um ciclo; devolve quantas mensagens foram lidas e publicadas.</summary>
    public async Task<OutboxRelayCycle> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var batchSize = settings.RelayBatchSize;

        // SQL parametrizado por interpolação do EF; a tabela/colunas são fixas.
        var pending = await db.OutboxMessages
            .FromSql($"""
                SELECT TOP ({batchSize}) * FROM [import].[outbox_messages] WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE [PublishedAtUtc] IS NULL AND [NextAttemptAtUtc] <= {now}
                ORDER BY [OccurredAtUtc]
                """)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return new OutboxRelayCycle(0, 0, false);
        }

        var outcomes = await publisher.PublishAsync(
            [.. pending.Select(m => new OutboundMessage(m.Id, m.Type, m.Payload, m.TraceParent, m.OccurredAtUtc))], cancellationToken);
        var byId = outcomes.ToDictionary(outcome => outcome.MessageId);

        var published = 0;
        foreach (var message in pending)
        {
            var outcome = byId[message.Id];
            if (outcome.Confirmed)
            {
                message.MarkPublished(timeProvider.GetUtcNow());
                published++;
                continue;
            }

            var errorCode = outcome.ErrorCode ?? PublishErrorCodes.PublishFailed;
            var delay = OutboxBackoff.Next(
                message.Attempts + 1,
                TimeSpan.FromMilliseconds(settings.BackoffBaseMs),
                TimeSpan.FromMilliseconds(settings.BackoffMaxMs),
                Random.Shared.NextDouble());
            message.MarkAttemptFailed(errorCode, timeProvider.GetUtcNow() + delay);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var brokerDown = published == 0 && outcomes.All(outcome => outcome.IsBrokerLevelFailure);
        LogCycle(pending.Count, published);
        return new OutboxRelayCycle(pending.Count, published, brokerDown);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox relay cycle: {Selected} selected, {Published} published")]
    private partial void LogCycle(int selected, int published);
}
