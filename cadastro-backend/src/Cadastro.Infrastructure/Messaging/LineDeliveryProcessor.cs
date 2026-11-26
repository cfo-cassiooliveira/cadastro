using System.Diagnostics;
using Cadastro.Application.UserImports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cadastro.Infrastructure.Messaging;

/// <summary>O que fazer no broker com a entrega.</summary>
internal enum DeliveryAction
{
    /// <summary>Confirmar (ack).</summary>
    Ack = 0,

    /// <summary>Publicar na fila de retry com o contador do próximo ciclo e confirmar a original.</summary>
    Retry = 1,

    /// <summary>Recusar sem reenfileirar (a policy cadastro-dlx envia à DLQ).</summary>
    Reject = 2,
}

/// <summary>Decisão do processamento de uma entrega.</summary>
/// <param name="Action">Ação no broker.</param>
/// <param name="NextAttempt">Contador do próximo ciclo (só em <see cref="DeliveryAction.Retry"/>).</param>
/// <param name="Code">Código do motivo, para log.</param>
internal readonly record struct DeliveryDecision(DeliveryAction Action, int NextAttempt = 0, string? Code = null);

/// <summary>
/// Processa uma entrega fora do AMQP: valida o envelope, continua o trace do <c>traceparent</c>, chama o caso de uso num escopo
/// próprio e traduz o resultado em ack, retry ou rejeição. Falha inesperada é logada uma vez aqui e vira retry; esgotados os
/// ciclos, a linha é encerrada como erro e a mensagem vai à DLQ.
/// </summary>
internal sealed partial class LineDeliveryProcessor(IServiceScopeFactory scopeFactory, ILogger<LineDeliveryProcessor> logger)
{
    /// <summary>Processa a entrega e devolve o que o consumidor deve fazer.</summary>
    public async Task<DeliveryDecision> ProcessAsync(ReadOnlyMemory<byte> body, int attempt, string? traceParent, CancellationToken cancellationToken)
    {
        if (!LineAcceptedMessage.TryParse(body.Span, out var message) || message is null)
        {
            LogInvalidMessage();
            return new DeliveryDecision(DeliveryAction.Reject, Code: ImportFailureCodes.MessageInvalid);
        }

        using var activity = StartActivity(traceParent);
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["correlation_id"] = message.CorrelationId,
            ["message_id"] = message.MessageId,
        });
        await using var scope = scopeFactory.CreateAsyncScope();
        var command = new ProcessUserImportLineCommand(message.MessageId, message.BatchId, message.LineId, attempt);
        try
        {
            var result = await scope.ServiceProvider.GetRequiredService<IProcessUserImportLineHandler>().HandleAsync(command, cancellationToken);
            return result.Disposition switch
            {
                LineProcessingDisposition.Completed or LineProcessingDisposition.Duplicate => new DeliveryDecision(DeliveryAction.Ack),
                LineProcessingDisposition.RetryLater => new DeliveryDecision(
                    DeliveryAction.Retry, result.CountsAsCycle ? ImportRetryPolicy.NextAttempt(attempt) : attempt, result.Code),
                _ => new DeliveryDecision(DeliveryAction.Reject, Code: result.Code),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogUnexpected(ex, message.LineId, attempt);
            return await RetryOrGiveUpAsync(scope.ServiceProvider, message, attempt, cancellationToken);
        }
    }

    /// <summary>
    /// Encerra a linha da mensagem como erro (ex.: a retry não pôde ser publicada), para que nenhuma mensagem chegue à DLQ com a
    /// linha ainda pendente. Devolve <c>false</c> se não conseguiu gravar (quem chamou deve manter a mensagem viva).
    /// </summary>
    public async Task<bool> TryFailLineAsync(ReadOnlyMemory<byte> body, string code, CancellationToken cancellationToken)
    {
        // Corpo inválido não tem linha para marcar.
        if (!LineAcceptedMessage.TryParse(body.Span, out var message) || message is null)
        {
            return true;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IFailUserImportLineHandler>().HandleAsync(
                new FailUserImportLineCommand(message.MessageId, message.BatchId, message.LineId, code), cancellationToken);
            return result.Disposition != LineProcessingDisposition.RetryLater;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogGiveUpFailed(ex, message.LineId);
            return false;
        }
    }

    private async Task<DeliveryDecision> RetryOrGiveUpAsync(
        IServiceProvider services, LineAcceptedMessage message, int attempt, CancellationToken cancellationToken)
    {
        if (!ImportRetryPolicy.IsExhausted(attempt))
        {
            return new DeliveryDecision(DeliveryAction.Retry, ImportRetryPolicy.NextAttempt(attempt), ImportFailureCodes.ProcessingFailed);
        }

        try
        {
            await services.GetRequiredService<IFailUserImportLineHandler>().HandleAsync(
                new FailUserImportLineCommand(message.MessageId, message.BatchId, message.LineId, ImportFailureCodes.RetriesExhausted), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Não deu para marcar a linha (banco fora): a mensagem vai à DLQ mesmo assim, para não girar para sempre.
            LogGiveUpFailed(ex, message.LineId);
        }

        return new DeliveryDecision(DeliveryAction.Reject, Code: ImportFailureCodes.RetriesExhausted);
    }

    private static Activity? StartActivity(string? traceParent)
    {
        var activity = new Activity("UserImportLine consume");
        if (traceParent is not null && ActivityContext.TryParse(traceParent, null, out _))
        {
            activity.SetParentId(traceParent);
        }

        return activity.Start();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejecting a message that is not a valid line-accepted envelope")]
    private partial void LogInvalidMessage();

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected failure processing import line {LineId} (retry cycle {Attempt})")]
    private partial void LogUnexpected(Exception exception, Guid lineId, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not mark import line {LineId} as failed after the retries were exhausted")]
    private partial void LogGiveUpFailed(Exception exception, Guid lineId);
}
