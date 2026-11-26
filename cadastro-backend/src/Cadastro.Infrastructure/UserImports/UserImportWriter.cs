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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import batch not saved: unique index {IndexName} violated (SQL error {SqlErrorNumber})")]
    private partial void LogDuplicateFileHash(string indexName, int sqlErrorNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox message already processed: unique violation on {TableName} (SQL error {SqlErrorNumber})")]
    private partial void LogDuplicateMessage(string tableName, int sqlErrorNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import batch not saved: concurrent update detected")]
    private partial void LogConcurrency();
}
