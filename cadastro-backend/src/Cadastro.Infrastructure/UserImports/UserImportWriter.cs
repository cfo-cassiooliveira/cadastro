using Cadastro.Application.UserImports;
using Cadastro.Domain.UserImports;
using Cadastro.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cadastro.Infrastructure.UserImports;

/// <summary>
/// Escrita da importação sobre o <see cref="CadastroDbContext"/>: os agregados são carregados com rastreamento e tudo que
/// o caso de uso muda (lote, linhas, auditoria e outbox) vai numa única transação do <c>SaveChanges</c>.
/// </summary>
internal sealed partial class UserImportWriter(CadastroDbContext db, ILogger<UserImportWriter> logger) : IUserImportWriter
{
    // Códigos do SQL Server para violação de índice único / chave duplicada.
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const string FileHashIndex = "ux_import_batches_file_hash";
    private const string InboxTable = "inbox_messages";

    // Soma o resultado da linha nos contadores e, se for a última, encerra o lote (mesma regra de ImportBatch.RecordLineOutcome).
    // O lado direito de cada SET usa os valores de antes do UPDATE; só atualiza lote em processamento com linhas ainda por contar.
    // Placeholders: {0}=criadas, {1}=já existentes, {2}=erros, {3}=momento, {4}=fim da retenção do relatório, {5}=id do lote.
    private const string LineOutcomeSql = $$"""
        UPDATE [{{CadastroDbContext.Schema}}].[import_batches]
        SET CreatedCount = CreatedCount + {0},
            AlreadyExistsCount = AlreadyExistsCount + {1},
            ErrorCount = ErrorCount + {2},
            Status = CASE WHEN CreatedCount + AlreadyExistsCount + ErrorCount + 1 = ValidLines
                          THEN CASE WHEN ErrorCount + {2} = 0 THEN '{{nameof(ImportBatchStatus.Completed)}}' ELSE '{{nameof(ImportBatchStatus.CompletedWithErrors)}}' END
                          ELSE Status END,
            FinishedAtUtc = CASE WHEN CreatedCount + AlreadyExistsCount + ErrorCount + 1 = ValidLines THEN {3} ELSE FinishedAtUtc END,
            ReportExpiresAtUtc = CASE WHEN CreatedCount + AlreadyExistsCount + ErrorCount + 1 = ValidLines THEN {4} ELSE ReportExpiresAtUtc END
        WHERE Id = {5} AND Status = '{{nameof(ImportBatchStatus.Processing)}}' AND CreatedCount + AlreadyExistsCount + ErrorCount < ValidLines
        """;

    /// <inheritdoc />
    public async Task<Guid?> FindActiveBatchIdByHashAsync(string fileHash, CancellationToken cancellationToken) =>
        await db.ImportBatches.AsNoTracking()
            .Where(batch => batch.FileHash == fileHash && batch.Status != ImportBatchStatus.Cancelled)
            .Select(batch => (Guid?)batch.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ImportBatch?> FindBatchAsync(Guid batchId, CancellationToken cancellationToken) =>
        db.ImportBatches.FirstOrDefaultAsync(batch => batch.Id == batchId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImportLine>> GetLinesAsync(Guid batchId, CancellationToken cancellationToken) =>
        await db.ImportLines.Where(line => line.BatchId == batchId).OrderBy(line => line.LineNumber).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ImportLine?> FindLineAsync(Guid batchId, Guid lineId, CancellationToken cancellationToken) =>
        db.ImportLines.FirstOrDefaultAsync(line => line.BatchId == batchId && line.Id == lineId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImportLine>> GetErrorLinesAsync(Guid batchId, CancellationToken cancellationToken) =>
        await db.ImportLines.Where(line => line.BatchId == batchId && line.Status == ImportLineStatus.Error)
            .OrderBy(line => line.LineNumber).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public void AddBatch(ImportBatch batch, IEnumerable<ImportLine> lines)
    {
        db.ImportBatches.Add(batch);
        db.ImportLines.AddRange(lines);
    }

    /// <inheritdoc />
    public void AddAudit(ImportAuditEntry entry) => db.AuditEntries.Add(entry);

    /// <inheritdoc />
    public void EnqueueEvents(IEnumerable<IntegrationEvent> events) =>
        db.OutboxMessages.AddRange(events.Select(e => OutboxMessage.Create(e.MessageId, e.Type, e.Payload, e.TraceParent, e.OccurredAtUtc)));

    /// <inheritdoc />
    public void AddInboxMessage(Guid messageId, DateTimeOffset now) => db.InboxMessages.Add(InboxMessage.Create(messageId, now));

    /// <inheritdoc />
    public Task<bool> HasProcessedMessageAsync(Guid messageId, CancellationToken cancellationToken) =>
        db.InboxMessages.AsNoTracking().AnyAsync(m => m.MessageId == messageId, cancellationToken);

    /// <inheritdoc />
    public async Task<ImportSaveResult> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return ImportSaveResult.Saved;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: UniqueIndexViolation or UniqueConstraintViolation } sql
            && sql.Message.Contains(FileHashIndex, StringComparison.Ordinal))
        {
            // Não loga a mensagem do SQL (traz o valor duplicado): só o índice e o número do erro.
            LogDuplicateFileHash(FileHashIndex, sql.Number);
            db.ChangeTracker.Clear();
            return ImportSaveResult.DuplicateFileHash;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: UniqueIndexViolation or UniqueConstraintViolation } sql
            && sql.Message.Contains(InboxTable, StringComparison.Ordinal))
        {
            // Entrega duplicada: outra instância já gravou o efeito. Só o nome da tabela e o número do erro vão ao log.
            LogDuplicateMessage(InboxTable, sql.Number);
            db.ChangeTracker.Clear();
            return ImportSaveResult.DuplicateMessage;
        }
        catch (DbUpdateConcurrencyException)
        {
            LogConcurrency();
            db.ChangeTracker.Clear();
            return ImportSaveResult.ConcurrencyConflict;
        }
    }

    /// <inheritdoc />
    public async Task<ImportSaveResult> SaveLineOutcomeAsync(Guid batchId, ImportLineStatus outcome, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var finished = now.ToUniversalTime();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            var affected = await db.Database.ExecuteSqlRawAsync(
                LineOutcomeSql,
                [
                    outcome == ImportLineStatus.Created ? 1 : 0,
                    outcome == ImportLineStatus.AlreadyExists ? 1 : 0,
                    outcome == ImportLineStatus.Error ? 1 : 0,
                    finished,
                    finished + ImportRetention.Report,
                    batchId,
                ],
                cancellationToken);
            if (affected == 0)
            {
                // Lote cancelado/concluído (ou todas as linhas já contadas): desfaz a linha e a inbox desta tentativa.
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                return ImportSaveResult.BatchNotProcessing;
            }

            await transaction.CommitAsync(cancellationToken);
            return ImportSaveResult.Saved;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: UniqueIndexViolation or UniqueConstraintViolation } sql
            && sql.Message.Contains(InboxTable, StringComparison.Ordinal))
        {
            // Entrega duplicada: outra instância já gravou o efeito (a transação é desfeita ao sair do bloco).
            LogDuplicateMessage(InboxTable, sql.Number);
            db.ChangeTracker.Clear();
            return ImportSaveResult.DuplicateMessage;
        }
        catch (DbUpdateConcurrencyException)
        {
            LogConcurrency();
            db.ChangeTracker.Clear();
            return ImportSaveResult.ConcurrencyConflict;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import batch not saved: unique index {IndexName} violated (SQL error {SqlErrorNumber})")]
    private partial void LogDuplicateFileHash(string indexName, int sqlErrorNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox message already processed: unique violation on {TableName} (SQL error {SqlErrorNumber})")]
    private partial void LogDuplicateMessage(string tableName, int sqlErrorNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import batch not saved: concurrent update detected")]
    private partial void LogConcurrency();
}
