using Cadastro.Application.UserImports;
using Cadastro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Messaging;

/// <summary>Resultado de um ciclo do relay.</summary>
/// <param name="Selected">Mensagens lidas.</param>
/// <param name="Published">Confirmadas pelo broker e marcadas como publicadas.</param>
/// <param name="BrokerUnavailable">Todas as falhas do ciclo foram de conexão/canal: o serviço deve pausar antes de tentar de novo.</param>
internal readonly record struct OutboxRelayCycle(int Selected, int Published, bool BrokerUnavailable);

/// <summary>
/// Um ciclo do relay da outbox: lê um lote pequeno de pendentes com <c>UPDLOCK, READPAST</c> dentro de uma transação (outra
/// instância pula as linhas travadas e nunca publica a mesma mensagem ao mesmo tempo), publica com confirms e só então marca
/// como publicada. Falha, nack ou return mantêm a mensagem pendente com backoff e jitter. Se o processo cair entre o confirm
/// e o commit a mensagem é republicada: o consumidor é idempotente por <c>messageId</c> (Art. VII.2).
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
