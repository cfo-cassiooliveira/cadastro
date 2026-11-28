using System.Security.Cryptography;
using Cadastro.Application.Users;
using Cadastro.Domain.UserImports;
using Microsoft.Extensions.Logging;

namespace Cadastro.Application.UserImports;

/// <summary>Mensagem de linha aceita a processar.</summary>
/// <param name="MessageId">Id do envelope (chave da inbox).</param>
/// <param name="BatchId">Lote.</param>
/// <param name="LineId">Linha.</param>
/// <param name="Attempt">Ciclos de retentativa já feitos (0 na primeira entrega).</param>
public sealed record ProcessUserImportLineCommand(Guid MessageId, Guid BatchId, Guid LineId, int Attempt);

/// <summary>Pedido para encerrar a linha como erro definitivo (retentativas esgotadas por falha inesperada).</summary>
/// <param name="MessageId">Id do envelope.</param>
/// <param name="BatchId">Lote.</param>
/// <param name="LineId">Linha.</param>
/// <param name="Code">Código do motivo.</param>
public sealed record FailUserImportLineCommand(Guid MessageId, Guid BatchId, Guid LineId, string Code);

/// <summary>O que o consumidor deve fazer com a mensagem.</summary>
public enum LineProcessingDisposition
{
    /// <summary>Efeito gravado: confirmar (ack).</summary>
    Completed = 0,

    /// <summary>Duplicata ou linha já encerrada: confirmar (ack) sem repetir o efeito.</summary>
    Duplicate = 1,

    /// <summary>Falha transitória: republicar na fila de retry e confirmar a original.</summary>
    RetryLater = 2,

    /// <summary>Falha definitiva: recusar sem reenfileirar (DLQ); a linha já foi marcada quando existia.</summary>
    Failed = 3,
}

/// <summary>Resultado do processamento de uma mensagem.</summary>
/// <param name="Disposition">Destino da mensagem.</param>
/// <param name="Code">Código do motivo em <see cref="LineProcessingDisposition.RetryLater"/> e <see cref="LineProcessingDisposition.Failed"/>.</param>
/// <param name="CountsAsCycle">Se a retentativa consome um dos 5 ciclos; falha operacional (credencial recusada) não consome.</param>
public sealed record LineProcessingResult(LineProcessingDisposition Disposition, string? Code = null, bool CountsAsCycle = true);

/// <summary>Caso de uso "Processar linha aceita": cria o usuário, marca o Legado e fecha a linha e o lote.</summary>
public interface IProcessUserImportLineHandler
{
    /// <summary>Processa uma entrega da mensagem de forma idempotente.</summary>
    Task<LineProcessingResult> HandleAsync(ProcessUserImportLineCommand command, CancellationToken cancellationToken);
}

/// <summary>Caso de uso "Encerrar linha com erro": usado quando o consumidor desiste de uma mensagem.</summary>
public interface IFailUserImportLineHandler
{
    /// <summary>Marca a linha como erro e conta no lote, na mesma transação da inbox.</summary>
    Task<LineProcessingResult> HandleAsync(FailUserImportLineCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// Grava o término de uma linha (linha, contadores do lote e inbox) numa única transação, para a reentrega ser segura.
/// Os contadores usam <c>UPDATE</c> atômico (<see cref="IUserImportWriter.SaveLineOutcomeAsync"/>); repetir em conflito é salvaguarda.
/// </summary>
internal sealed partial class UserImportLineCompletion(IUserImportWriter writer, TimeProvider timeProvider, ILogger<UserImportLineCompletion> logger)
{
    private const int MaxConcurrencyAttempts = 10;

    /// <summary>Encerra a linha como criada ou já existente, com o resultado da marcação no Legado.</summary>
    public Task<LineProcessingResult> CompleteAsync(
        Guid messageId, Guid batchId, Guid lineId, ImportLineStatus outcome, LegacyMarkStatus legacyMark, CancellationToken cancellationToken) =>
        ApplyAsync(
            messageId, batchId, lineId, outcome,
            (line, now) =>
            {
                if (outcome == ImportLineStatus.Created)
                {
                    line.MarkCreated(now);
                }
                else
                {
                    line.MarkAlreadyExists(now);
                }

                line.RecordLegacyMark(legacyMark);
            },
            new LineProcessingResult(LineProcessingDisposition.Completed),
            cancellationToken);

    /// <summary>Encerra a linha como erro definitivo (os dados pessoais ficam para o reprocessamento).</summary>
    public Task<LineProcessingResult> FailAsync(Guid messageId, Guid batchId, Guid lineId, string code, CancellationToken cancellationToken) =>
        ApplyAsync(
            messageId, batchId, lineId, ImportLineStatus.Error,
            (line, now) => line.MarkFailed(code, now),
            new LineProcessingResult(LineProcessingDisposition.Failed, code),
            cancellationToken);

    /// <summary>Falha transitória: pede nova tentativa ou, esgotados os ciclos, encerra a linha como erro.</summary>
    public Task<LineProcessingResult> RetryOrFailAsync(ProcessUserImportLineCommand command, string code, CancellationToken cancellationToken) =>
        ImportRetryPolicy.IsExhausted(command.Attempt)
            ? FailAsync(command.MessageId, command.BatchId, command.LineId, ImportFailureCodes.RetriesExhausted, cancellationToken)
            : Task.FromResult(new LineProcessingResult(LineProcessingDisposition.RetryLater, code));

    private async Task<LineProcessingResult> ApplyAsync(
        Guid messageId,
        Guid batchId,
        Guid lineId,
        ImportLineStatus outcome,
        Action<ImportLine, DateTimeOffset> finishLine,
        LineProcessingResult success,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxConcurrencyAttempts; attempt++)
        {
            var line = await writer.FindLineAsync(batchId, lineId, cancellationToken);
            var batch = await writer.FindBatchAsync(batchId, cancellationToken);
            if (line is null || batch is null)
            {
                LogLineMissing(batchId, lineId);
                return new LineProcessingResult(LineProcessingDisposition.Failed, ImportFailureCodes.LineNotFound);
            }

            if (line.Status != ImportLineStatus.Pending)
            {
                return new LineProcessingResult(LineProcessingDisposition.Duplicate);
            }

            if (batch.Status != ImportBatchStatus.Processing)
            {
                LogBatchNotProcessing(batchId, batch.Status);
                return new LineProcessingResult(LineProcessingDisposition.Failed, ImportFailureCodes.BatchInvalidState);
            }

            var now = timeProvider.GetUtcNow();
            finishLine(line, now);
            writer.AddInboxMessage(messageId, now);
            switch (await writer.SaveLineOutcomeAsync(batchId, outcome, now, cancellationToken))
            {
                case ImportSaveResult.Saved:
                    return success;
                case ImportSaveResult.DuplicateMessage:
                    return new LineProcessingResult(LineProcessingDisposition.Duplicate);
                case ImportSaveResult.BatchNotProcessing:
                    LogBatchNotProcessing(batchId, batch.Status);
                    return new LineProcessingResult(LineProcessingDisposition.Failed, ImportFailureCodes.BatchInvalidState);
                default:
                    LogConflict(batchId, lineId, attempt);
                    break;
            }
        }

        return new LineProcessingResult(LineProcessingDisposition.RetryLater, ImportFailureCodes.ConcurrencyConflict);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import line {LineId} of batch {BatchId} not found; message will be rejected")]
    private partial void LogLineMissing(Guid batchId, Guid lineId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import batch {BatchId} is not processing (status {Status}); message will be rejected")]
    private partial void LogBatchNotProcessing(Guid batchId, ImportBatchStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Concurrent update on import batch {BatchId} while finishing line {LineId} (attempt {Attempt}); reloading")]
    private partial void LogConflict(Guid batchId, Guid lineId, int attempt);
}

/// <summary>
/// Processa uma linha aceita: decifra os dados, cria o usuário sem senha no Keycloak (e-mail de ativação), tenta marcar o
/// Legado e grava o desfecho. Há uma mensagem por pessoa, então uma linha com problema não impede as demais.
/// <para>
/// A entrega é "pelo menos uma vez". A inbox impede contar a linha duas vezes; o Keycloak fica fora da transação, e o atributo
/// <c>importLineId</c> evita criar outro usuário. O e-mail de ativação pode se repetir se a gravação falhar após o envio (ADR-0009).
/// </para>
/// <para>
/// Falha transitória volta por uma fila de retry com espera (30 s, até 5 ciclos), sem insistir de imediato em um serviço
/// fora do ar. Falha definitiva, ou ciclos esgotados, é recusada para a DLQ em vez de ser descartada em silêncio, e a linha,
/// quando existe e o lote ainda está em processamento, fica marcada com o código do motivo.
/// </para>
/// </summary>
internal sealed partial class ProcessUserImportLineHandler(
    IUserImportWriter writer,
    IPersonalDataProtector protector,
    IIdentityUserProvisioner identity,
    ILegacyUserDirectory legacy,
    UserImportLineCompletion completion,
    ILogger<ProcessUserImportLineHandler> logger) : IProcessUserImportLineHandler
{
    /// <inheritdoc />
    public async Task<LineProcessingResult> HandleAsync(ProcessUserImportLineCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        // Atalho para não repetir chamadas externas. A garantia contra corrida é a chave única da inbox no commit.
        if (await writer.HasProcessedMessageAsync(command.MessageId, cancellationToken))
        {
            LogDuplicate(command.MessageId);
            return new LineProcessingResult(LineProcessingDisposition.Duplicate);
        }

        var line = await writer.FindLineAsync(command.BatchId, command.LineId, cancellationToken);
        if (line is null)
        {
            LogLineMissing(command.BatchId, command.LineId);
            return new LineProcessingResult(LineProcessingDisposition.Failed, ImportFailureCodes.LineNotFound);
        }

        if (line.Status != ImportLineStatus.Pending)
        {
            LogAlreadyFinished(command.LineId, line.Status);
            return new LineProcessingResult(LineProcessingDisposition.Duplicate);
        }

        if (!TryDecrypt(line, out var request))
        {
            return await completion.FailAsync(command.MessageId, command.BatchId, command.LineId, ImportFailureCodes.PersonalDataUnavailable, cancellationToken);
        }

        ProvisionedUser user;
        try
        {
            user = await identity.ProvisionAsync(request!, cancellationToken);
        }
        catch (IdentityAccessDeniedException ex)
        {
            // Credencial recusada é erro de configuração: não gasta ciclo, para não levar o lote inteiro a erro (ADR-0009).
            LogIdentityAccessDenied(ex, command.LineId);
            return new LineProcessingResult(LineProcessingDisposition.RetryLater, ImportFailureCodes.IdentityUnavailable, CountsAsCycle: false);
        }
        catch (IdentityProviderUnavailableException ex)
        {
            LogIdentityUnavailable(ex, command.LineId, command.Attempt);
            return await completion.RetryOrFailAsync(command, ImportFailureCodes.IdentityUnavailable, cancellationToken);
        }
        catch (IdentityUserRejectedException ex)
        {
            LogIdentityRejected(command.LineId, ex.Code);
            return await completion.FailAsync(command.MessageId, command.BatchId, command.LineId, ex.Code, cancellationToken);
        }

        // O Legado só é consultado enquanto o e-mail ainda está em claro, antes de a linha apagar os dados pessoais.
        var legacyMark = user.Outcome == ProvisionOutcome.Created
            ? await MarkLegacyAsync(request!.Email, user.UserId, command.LineId, cancellationToken)
            : LegacyMarkStatus.NotAttempted;

        var status = user.Outcome == ProvisionOutcome.Created ? ImportLineStatus.Created : ImportLineStatus.AlreadyExists;
        var result = await completion.CompleteAsync(command.MessageId, command.BatchId, command.LineId, status, legacyMark, cancellationToken);
        if (result.Disposition == LineProcessingDisposition.Completed)
        {
            LogLineCompleted(command.LineId, status, legacyMark);
        }

        return result.Disposition == LineProcessingDisposition.RetryLater
            ? await completion.RetryOrFailAsync(command, result.Code!, cancellationToken)
            : result;
    }

    private bool TryDecrypt(ImportLine line, out ProvisionUserRequest? request)
    {
        request = null;
        var data = line.GetProtectedData();
        if (data is null)
        {
            LogDataErased(line.Id);
            return false;
        }

        try
        {
            var name = protector.Unprotect(data.Name, data.KeyVersion, ImportLineProtection.Context(line.Id, ImportLineProtection.NameField));
            var email = protector.Unprotect(data.Email, data.KeyVersion, ImportLineProtection.Context(line.Id, ImportLineProtection.EmailField));
            var phone = data.Phone is null
                ? null
                : protector.Unprotect(data.Phone, data.KeyVersion, ImportLineProtection.Context(line.Id, ImportLineProtection.PhoneField));
            request = new ProvisionUserRequest(line.Id, name, email, phone);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or ArgumentException)
        {
            LogDecryptFailed(ex, line.Id);
            return false;
        }
    }

    private async Task<LegacyMarkStatus> MarkLegacyAsync(string email, string keycloakUserId, Guid lineId, CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await legacy.FindByEmailAsync(email, cancellationToken);
            var decision = LegacyMarkDecision.Decide(email, candidates);
            if (decision.Target is null)
            {
                if (decision.Status == LegacyMarkStatus.Ambiguous)
                {
                    LogLegacyAmbiguous(lineId, candidates.Count);
                }

                return decision.Status;
            }

            await legacy.MarkMigratedAsync(decision.Target.Id, keycloakUserId, cancellationToken);
            return LegacyMarkStatus.Marked;
        }
        catch (LegacyDirectoryUnavailableException ex)
        {
            // Não desfaz o usuário do Keycloak nem falha a linha: o estado fica em LegacyMarkStatus.
            LogLegacyFailed(ex, lineId);
            return LegacyMarkStatus.Failed;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Message {MessageId} already processed; acknowledging without repeating the effect")]
    private partial void LogDuplicate(Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import line {LineId} of batch {BatchId} not found; message will be rejected")]
    private partial void LogLineMissing(Guid batchId, Guid lineId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Import line {LineId} already finished with status {Status}; acknowledging")]
    private partial void LogAlreadyFinished(Guid lineId, ImportLineStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Personal data of import line {LineId} was already erased; line will fail")]
    private partial void LogDataErased(Guid lineId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not decrypt the personal data of import line {LineId}")]
    private partial void LogDecryptFailed(Exception exception, Guid lineId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Identity provider unavailable while processing import line {LineId} (retry cycle {Attempt})")]
    private partial void LogIdentityUnavailable(Exception exception, Guid lineId, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Identity provider refused the importer credentials or permission while processing import line {LineId}; retrying without using a cycle")]
    private partial void LogIdentityAccessDenied(Exception exception, Guid lineId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Identity provider rejected import line {LineId} ({Code})")]
    private partial void LogIdentityRejected(Guid lineId, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Legacy mark skipped for import line {LineId}: {CandidateCount} candidates with the same e-mail")]
    private partial void LogLegacyAmbiguous(Guid lineId, int candidateCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Legacy API failed while marking import line {LineId}; the Keycloak user is kept")]
    private partial void LogLegacyFailed(Exception exception, Guid lineId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Import line {LineId} finished as {Status} (legacy mark {LegacyMark})")]
    private partial void LogLineCompleted(Guid lineId, ImportLineStatus status, LegacyMarkStatus legacyMark);
}

/// <summary>Encerra uma linha como erro quando o consumidor desiste da mensagem por falha inesperada.</summary>
internal sealed class FailUserImportLineHandler(UserImportLineCompletion completion) : IFailUserImportLineHandler
{
    /// <inheritdoc />
    public Task<LineProcessingResult> HandleAsync(FailUserImportLineCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return completion.FailAsync(command.MessageId, command.BatchId, command.LineId, command.Code, cancellationToken);
    }
}
