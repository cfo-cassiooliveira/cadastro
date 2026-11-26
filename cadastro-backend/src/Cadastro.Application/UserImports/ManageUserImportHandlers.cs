using Cadastro.Domain.UserImports;
using Microsoft.Extensions.Logging;

namespace Cadastro.Application.UserImports;

/// <summary>Desfecho de um comando sobre um lote existente.</summary>
public enum ImportCommandOutcome
{
    /// <summary>Aplicado.</summary>
    Done = 0,

    /// <summary>Lote ou linha inexistente.</summary>
    NotFound = 1,

    /// <summary>O estado atual do lote ou da linha não permite a operação.</summary>
    InvalidState = 2,

    /// <summary>O token do resumo não confere.</summary>
    InvalidToken = 3,

    /// <summary>Outro processo alterou o lote ao mesmo tempo; o cliente pode tentar de novo.</summary>
    Conflict = 4,

    /// <summary>O lote pertence a outro administrador: só o criador confirma, cancela ou reprocessa.</summary>
    NotOwner = 5,
}

/// <summary>Resultado de um comando de gestão do lote.</summary>
/// <param name="Outcome">Desfecho.</param>
/// <param name="Count">Quantidade afetada (linhas reabertas, mensagens gravadas); 0 quando não se aplica.</param>
public sealed record ImportCommandResult(ImportCommandOutcome Outcome, int Count = 0);

/// <summary>Confirma o lote (segundo passo) com o token do resumo.</summary>
/// <param name="BatchId">Lote.</param>
/// <param name="SummaryToken">Token devolvido no upload.</param>
/// <param name="Context">Quem confirma.</param>
public sealed record ConfirmUserImportCommand(Guid BatchId, string? SummaryToken, ImportRequestContext Context);

/// <summary>Cancela um lote que aguarda confirmação.</summary>
/// <param name="BatchId">Lote.</param>
/// <param name="Context">Quem cancela.</param>
public sealed record CancelUserImportCommand(Guid BatchId, ImportRequestContext Context);

/// <summary>Reprocessa uma linha com erro definitivo.</summary>
/// <param name="BatchId">Lote.</param>
/// <param name="LineId">Linha.</param>
/// <param name="Context">Quem pediu.</param>
public sealed record ReprocessUserImportLineCommand(Guid BatchId, Guid LineId, ImportRequestContext Context);

/// <summary>Reprocessa todas as linhas com erro definitivo do lote.</summary>
/// <param name="BatchId">Lote.</param>
/// <param name="Context">Quem pediu.</param>
public sealed record ReprocessUserImportErrorsCommand(Guid BatchId, ImportRequestContext Context);

/// <summary>Caso de uso "Confirmar lote": muda o lote e grava 1 mensagem de outbox por pessoa na mesma transação.</summary>
public interface IConfirmUserImportHandler
{
    /// <summary>Confirma o lote e enfileira as mensagens.</summary>
    Task<ImportCommandResult> HandleAsync(ConfirmUserImportCommand command, CancellationToken cancellationToken);
}

/// <summary>Caso de uso "Cancelar lote".</summary>
public interface ICancelUserImportHandler
{
    /// <summary>Cancela o lote e apaga os dados pessoais das linhas pendentes.</summary>
    Task<ImportCommandResult> HandleAsync(CancelUserImportCommand command, CancellationToken cancellationToken);
}

/// <summary>Caso de uso "Reprocessar uma linha".</summary>
public interface IReprocessUserImportLineHandler
{
    /// <summary>Reabre a linha, audita e enfileira uma nova mensagem.</summary>
    Task<ImportCommandResult> HandleAsync(ReprocessUserImportLineCommand command, CancellationToken cancellationToken);
}

/// <summary>Caso de uso "Reprocessar todos os erros do lote".</summary>
public interface IReprocessUserImportErrorsHandler
{
    /// <summary>Reabre as linhas com erro, audita cada uma e enfileira as mensagens.</summary>
    Task<ImportCommandResult> HandleAsync(ReprocessUserImportErrorsCommand command, CancellationToken cancellationToken);
}

/// <summary>Confirma o lote conferindo o token e grava as mensagens de outbox junto, numa única transação (Art. VII.3).</summary>
internal sealed partial class ConfirmUserImportHandler(
    IUserImportWriter writer, IImportSummarySigner signer, TimeProvider timeProvider, ILogger<ConfirmUserImportHandler> logger)
    : IConfirmUserImportHandler
{
    /// <inheritdoc />
    public async Task<ImportCommandResult> HandleAsync(ConfirmUserImportCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var batch = await writer.FindBatchAsync(command.BatchId, cancellationToken);
        if (batch is null)
        {
            return new ImportCommandResult(ImportCommandOutcome.NotFound);
        }

        if (batch.CreatedBy != command.Context.ActorId)
        {
            LogNotOwner(batch.Id, command.Context.ActorId);
            return new ImportCommandResult(ImportCommandOutcome.NotOwner);
        }

        if (!signer.Matches(command.SummaryToken, batch.Id, batch.CreatedBy, batch.FileHash, batch.ValidLines, batch.InvalidLines, batch.DuplicateLines))
        {
            LogInvalidToken(batch.Id);
            return new ImportCommandResult(ImportCommandOutcome.InvalidToken);
        }

        var now = timeProvider.GetUtcNow();
        var lines = await writer.GetLinesAsync(batch.Id, cancellationToken);
        try
        {
            batch.Confirm(now);
        }
        catch (ImportDomainException)
        {
            // Estado inválido é fluxo esperado (reenvio da confirmação), não falha: vira resultado tipado.
            LogInvalidState(batch.Id, batch.Status.ToString());
            return new ImportCommandResult(ImportCommandOutcome.InvalidState);
        }

        var context = command.Context;
        writer.EnqueueEvents(lines.Where(line => line.Status == ImportLineStatus.Pending)
            .Select(line => NewMessage(batch.Id, line.Id, context, now)).ToList());
        writer.AddAudit(ImportAuditEntry.Create(Guid.CreateVersion7(), context.ActorId, "batch.confirmed", batch.Id, context.CorrelationId, now));

        if (await writer.SaveChangesAsync(cancellationToken) != ImportSaveResult.Saved)
        {
            LogConflict(batch.Id);
            return new ImportCommandResult(ImportCommandOutcome.Conflict);
        }

        LogConfirmed(batch.Id, batch.ValidLines);
        return new ImportCommandResult(ImportCommandOutcome.Done, batch.ValidLines);
    }

    internal static IntegrationEvent NewMessage(Guid batchId, Guid lineId, ImportRequestContext context, DateTimeOffset now)
    {
        var messageId = Guid.CreateVersion7();
        var payload = EventEnvelope.CreateLineAccepted(messageId, batchId, lineId, context.CorrelationId, batchId.ToString("D"), now);
        return new IntegrationEvent(messageId, ImportLimits.LineAcceptedEventType, payload, context.TraceParent, now);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import batch {BatchId} confirmation refused: {ActorId} is not the batch owner")]
    private partial void LogNotOwner(Guid batchId, string actorId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import batch {BatchId} confirmation refused: summary token does not match")]
    private partial void LogInvalidToken(Guid batchId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import batch {BatchId} confirmation refused: batch is {Status}")]
    private partial void LogInvalidState(Guid batchId, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import batch {BatchId} confirmation lost a concurrent update")]
    private partial void LogConflict(Guid batchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Import batch {BatchId} confirmed; {Lines} messages queued in the outbox")]
    private partial void LogConfirmed(Guid batchId, int lines);
}

/// <summary>Cancela o lote aguardando confirmação (apaga os dados pessoais das linhas pendentes).</summary>
internal sealed partial class CancelUserImportHandler(IUserImportWriter writer, TimeProvider timeProvider, ILogger<CancelUserImportHandler> logger)
    : ICancelUserImportHandler
{
    /// <inheritdoc />
    public async Task<ImportCommandResult> HandleAsync(CancelUserImportCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var batch = await writer.FindBatchAsync(command.BatchId, cancellationToken);
        if (batch is null)
        {
            return new ImportCommandResult(ImportCommandOutcome.NotFound);
        }

        if (batch.CreatedBy != command.Context.ActorId)
        {
            LogNotOwner(batch.Id, command.Context.ActorId);
            return new ImportCommandResult(ImportCommandOutcome.NotOwner);
        }

        var now = timeProvider.GetUtcNow();
        var lines = await writer.GetLinesAsync(batch.Id, cancellationToken);
        try
        {
            batch.Cancel(lines, now);
        }
        catch (ImportDomainException)
        {
            LogInvalidState(batch.Id, batch.Status.ToString());
            return new ImportCommandResult(ImportCommandOutcome.InvalidState);
        }

        var context = command.Context;
        writer.AddAudit(ImportAuditEntry.Create(Guid.CreateVersion7(), context.ActorId, "batch.cancelled", batch.Id, context.CorrelationId, now));
        if (await writer.SaveChangesAsync(cancellationToken) != ImportSaveResult.Saved)
        {
            return new ImportCommandResult(ImportCommandOutcome.Conflict);
        }

        LogCancelled(batch.Id);
        return new ImportCommandResult(ImportCommandOutcome.Done);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import batch {BatchId} cancellation refused: {ActorId} is not the batch owner")]
    private partial void LogNotOwner(Guid batchId, string actorId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import batch {BatchId} cancellation refused: batch is {Status}")]
    private partial void LogInvalidState(Guid batchId, string status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Import batch {BatchId} cancelled and pending personal data erased")]
    private partial void LogCancelled(Guid batchId);
}

/// <summary>Reabre uma linha com erro (<see cref="ImportBatch.Reprocess"/> grava a auditoria) e enfileira a nova mensagem.</summary>
internal sealed partial class ReprocessUserImportLineHandler(
    IUserImportWriter writer, TimeProvider timeProvider, ILogger<ReprocessUserImportLineHandler> logger) : IReprocessUserImportLineHandler
{
    /// <inheritdoc />
    public async Task<ImportCommandResult> HandleAsync(ReprocessUserImportLineCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var batch = await writer.FindBatchAsync(command.BatchId, cancellationToken);
        var line = batch is null ? null : await writer.FindLineAsync(batch.Id, command.LineId, cancellationToken);
        if (batch is null || line is null)
        {
            return new ImportCommandResult(ImportCommandOutcome.NotFound);
        }

        if (batch.CreatedBy != command.Context.ActorId)
        {
            LogNotOwner(batch.Id, command.Context.ActorId);
            return new ImportCommandResult(ImportCommandOutcome.NotOwner);
        }

        var now = timeProvider.GetUtcNow();
        var context = command.Context;
        try
        {
            writer.AddAudit(batch.Reprocess(line, context.ActorId, context.CorrelationId, now));
        }
        catch (ImportDomainException)
        {
            LogInvalidState(batch.Id);
            return new ImportCommandResult(ImportCommandOutcome.InvalidState);
        }

        writer.EnqueueEvents([ConfirmUserImportHandler.NewMessage(batch.Id, line.Id, context, now)]);
        if (await writer.SaveChangesAsync(cancellationToken) != ImportSaveResult.Saved)
        {
            return new ImportCommandResult(ImportCommandOutcome.Conflict);
        }

        LogReprocessed(batch.Id, 1);
        return new ImportCommandResult(ImportCommandOutcome.Done, 1);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import batch {BatchId} reprocess refused: {ActorId} is not the batch owner")]
    private partial void LogNotOwner(Guid batchId, string actorId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import batch {BatchId} reprocess refused: the line or batch state does not allow it")]
    private partial void LogInvalidState(Guid batchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Import batch {BatchId} reopened {Lines} line(s) for reprocessing")]
    private partial void LogReprocessed(Guid batchId, int lines);
}

/// <summary>Reabre todas as linhas com erro definitivo do lote numa única transação.</summary>
internal sealed partial class ReprocessUserImportErrorsHandler(
    IUserImportWriter writer, TimeProvider timeProvider, ILogger<ReprocessUserImportErrorsHandler> logger) : IReprocessUserImportErrorsHandler
{
    /// <inheritdoc />
    public async Task<ImportCommandResult> HandleAsync(ReprocessUserImportErrorsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var batch = await writer.FindBatchAsync(command.BatchId, cancellationToken);
        if (batch is null)
        {
            return new ImportCommandResult(ImportCommandOutcome.NotFound);
        }

        if (batch.CreatedBy != command.Context.ActorId)
        {
            LogNotOwner(batch.Id, command.Context.ActorId);
            return new ImportCommandResult(ImportCommandOutcome.NotOwner);
        }

        var lines = await writer.GetErrorLinesAsync(batch.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var context = command.Context;
        var reopened = new List<IntegrationEvent>();
        try
        {
            foreach (var line in lines.Where(line => line.HasPersonalData))
            {
                writer.AddAudit(batch.Reprocess(line, context.ActorId, context.CorrelationId, now));
                reopened.Add(ConfirmUserImportHandler.NewMessage(batch.Id, line.Id, context, now));
            }
        }
        catch (ImportDomainException)
        {
            LogInvalidState(batch.Id);
            return new ImportCommandResult(ImportCommandOutcome.InvalidState);
        }

        if (reopened.Count == 0)
        {
            return new ImportCommandResult(ImportCommandOutcome.Done);
        }

        writer.EnqueueEvents(reopened);
        if (await writer.SaveChangesAsync(cancellationToken) != ImportSaveResult.Saved)
        {
            return new ImportCommandResult(ImportCommandOutcome.Conflict);
        }

        LogReprocessed(batch.Id, reopened.Count);
        return new ImportCommandResult(ImportCommandOutcome.Done, reopened.Count);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import batch {BatchId} reprocess refused: {ActorId} is not the batch owner")]
    private partial void LogNotOwner(Guid batchId, string actorId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import batch {BatchId} reprocess refused: the batch state does not allow it")]
    private partial void LogInvalidState(Guid batchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Import batch {BatchId} reopened {Lines} line(s) for reprocessing")]
    private partial void LogReprocessed(Guid batchId, int lines);
}
