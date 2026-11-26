using Cadastro.Application.UserImports;
using Cadastro.Domain.UserImports;

namespace Cadastro.Tests.Shared;

/// <summary>
/// Substitui o banco nos testes de caso de uso e de API: implementa as portas de escrita e leitura da importação em memória.
/// A fidelidade com o SQL Server (índice único, transação, rowversion) é provada nos testes do <c>Infrastructure.Tests</c>.
/// </summary>
internal sealed class InMemoryUserImportStore : IUserImportWriter, IUserImportReadService
{
    private readonly Lock _gate = new();
    private readonly List<ImportBatch> _batches = [];
    private readonly List<ImportLine> _lines = [];
    private readonly List<ImportBatch> _stagedBatches = [];
    private readonly List<ImportLine> _stagedLines = [];
    private readonly List<ImportAuditEntry> _stagedAudits = [];
    private readonly List<IntegrationEvent> _stagedEvents = [];
    private readonly HashSet<Guid> _inbox = [];
    private readonly List<Guid> _stagedInbox = [];

    /// <summary>Lotes gravados.</summary>
    public IReadOnlyList<ImportBatch> Batches => _batches;

    /// <summary>Linhas gravadas.</summary>
    public IReadOnlyList<ImportLine> Lines => _lines;

    /// <summary>Auditoria gravada.</summary>
    public List<ImportAuditEntry> Audits { get; } = [];

    /// <summary>Mensagens de outbox gravadas.</summary>
    public List<IntegrationEvent> Events { get; } = [];

    /// <summary>Quando definido, o próximo <see cref="SaveChangesAsync"/> devolve este resultado sem gravar.</summary>
    public ImportSaveResult? ForcedSaveResult { get; set; }

    /// <inheritdoc />
    public Task<Guid?> FindActiveBatchIdByHashAsync(string fileHash, CancellationToken cancellationToken) =>
        Task.FromResult(_batches.Where(b => b.FileHash == fileHash && b.Status != ImportBatchStatus.Cancelled).Select(b => (Guid?)b.Id).FirstOrDefault());

    /// <inheritdoc />
    public Task<ImportBatch?> FindBatchAsync(Guid batchId, CancellationToken cancellationToken) =>
        Task.FromResult(_batches.FirstOrDefault(b => b.Id == batchId));

    /// <inheritdoc />
    public Task<IReadOnlyList<ImportLine>> GetLinesAsync(Guid batchId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ImportLine>>([.. _lines.Where(l => l.BatchId == batchId).OrderBy(l => l.LineNumber)]);

    /// <inheritdoc />
    public Task<ImportLine?> FindLineAsync(Guid batchId, Guid lineId, CancellationToken cancellationToken) =>
        Task.FromResult(_lines.FirstOrDefault(l => l.BatchId == batchId && l.Id == lineId));

    /// <inheritdoc />
    public Task<IReadOnlyList<ImportLine>> GetErrorLinesAsync(Guid batchId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ImportLine>>([.. _lines.Where(l => l.BatchId == batchId && l.Status == ImportLineStatus.Error)]);

    /// <inheritdoc />
    public void AddBatch(ImportBatch batch, IEnumerable<ImportLine> lines)
    {
        _stagedBatches.Add(batch);
        _stagedLines.AddRange(lines);
    }

    /// <inheritdoc />
    public void AddAudit(ImportAuditEntry entry) => _stagedAudits.Add(entry);

    /// <inheritdoc />
    public void EnqueueEvents(IEnumerable<IntegrationEvent> events) => _stagedEvents.AddRange(events);

    /// <inheritdoc />
    public void AddInboxMessage(Guid messageId, DateTimeOffset now) => _stagedInbox.Add(messageId);

    /// <inheritdoc />
    public Task<bool> HasProcessedMessageAsync(Guid messageId, CancellationToken cancellationToken) => Task.FromResult(_inbox.Contains(messageId));

    /// <summary>Quantas mensagens a inbox registrou.</summary>
    public int InboxCount => _inbox.Count;

    /// <inheritdoc />
    public Task<ImportSaveResult> SaveLineOutcomeAsync(Guid batchId, ImportLineStatus outcome, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var batch = _batches.FirstOrDefault(b => b.Id == batchId);
            if (ForcedSaveResult is null && (batch is null || batch.Status != ImportBatchStatus.Processing || batch.FinishedLines >= batch.ValidLines))
            {
                ClearStaged();
                return Task.FromResult(ImportSaveResult.BatchNotProcessing);
            }

            if (ForcedSaveResult is null && !_stagedInbox.Any(_inbox.Contains))
            {
                batch!.RecordLineOutcome(outcome, now);
            }
        }

        return SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<ImportSaveResult> SaveChangesAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (ForcedSaveResult is { } forced)
            {
                ForcedSaveResult = null;
                ClearStaged();
                return Task.FromResult(forced);
            }

            if (_stagedBatches.Any(staged => _batches.Any(b => b.FileHash == staged.FileHash && b.Status != ImportBatchStatus.Cancelled)))
            {
                ClearStaged();
                return Task.FromResult(ImportSaveResult.DuplicateFileHash);
            }

            if (_stagedInbox.Any(_inbox.Contains))
            {
                ClearStaged();
                return Task.FromResult(ImportSaveResult.DuplicateMessage);
            }

            _inbox.UnionWith(_stagedInbox);
            _batches.AddRange(_stagedBatches);
            _lines.AddRange(_stagedLines);
            Audits.AddRange(_stagedAudits);
            Events.AddRange(_stagedEvents);
            ClearStaged();
            return Task.FromResult(ImportSaveResult.Saved);
        }
    }

    /// <inheritdoc />
    public Task<PagedResult<UserImportBatchDto>> ListBatchesAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var ordered = _batches.OrderByDescending(b => b.CreatedAtUtc).ThenByDescending(b => b.Id).ToList();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(ToDto).ToList();
        return Task.FromResult(new PagedResult<UserImportBatchDto>(items, page, pageSize, ordered.Count));
    }

    /// <inheritdoc />
    public Task<UserImportBatchDto?> GetBatchAsync(Guid batchId, CancellationToken cancellationToken) =>
        Task.FromResult(_batches.FirstOrDefault(b => b.Id == batchId) is { } batch ? ToDto(batch) : null);

    /// <inheritdoc />
    public Task<PagedResult<UserImportLineDto>?> ListLinesAsync(Guid batchId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (_batches.All(b => b.Id != batchId))
        {
            return Task.FromResult<PagedResult<UserImportLineDto>?>(null);
        }

        var query = _lines.Where(l => l.BatchId == batchId && (status is null || l.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(l => l.LineNumber).ToList();
        var items = query.Skip((page - 1) * pageSize).Take(pageSize).Select(ToLineDto).ToList();
        return Task.FromResult<PagedResult<UserImportLineDto>?>(new PagedResult<UserImportLineDto>(items, page, pageSize, query.Count));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UserImportLineDto>?> GetReportLinesAsync(Guid batchId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UserImportLineDto>?>(
            _batches.Any(b => b.Id == batchId)
                ? [.. _lines.Where(l => l.BatchId == batchId).OrderBy(l => l.LineNumber).Select(ToLineDto)]
                : null);

    private static UserImportLineDto ToLineDto(ImportLine l) =>
        new(l.Id, l.LineNumber, l.Status.ToString(), l.FailureReason, l.LegacyMarkStatus.ToString(), l.ReprocessCount);

    private UserImportBatchDto ToDto(ImportBatch b)
    {
        int Count(ImportLineStatus status) => _lines.Count(l => l.BatchId == b.Id && l.Status == status);
        return new UserImportBatchDto(
            b.Id, b.Status.ToString(), b.ValidLines, b.InvalidLines, b.DuplicateLines, b.CreatedAtUtc, b.ConfirmedAtUtc, b.FinishedAtUtc,
            new ImportLineCounts(
                Count(ImportLineStatus.Pending), Count(ImportLineStatus.Created), Count(ImportLineStatus.AlreadyExists),
                Count(ImportLineStatus.Error), Count(ImportLineStatus.Cancelled)));
    }

    private void ClearStaged()
    {
        _stagedBatches.Clear();
        _stagedLines.Clear();
        _stagedAudits.Clear();
        _stagedEvents.Clear();
        _stagedInbox.Clear();
    }
}
