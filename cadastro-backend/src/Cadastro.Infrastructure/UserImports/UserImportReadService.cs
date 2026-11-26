using Cadastro.Application.UserImports;
using Cadastro.Domain.UserImports;
using Cadastro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cadastro.Infrastructure.UserImports;

/// <summary>Leitura da importação direto para DTO (<c>AsNoTracking</c>): nenhuma coluna de dado pessoal é projetada.</summary>
internal sealed class UserImportReadService(CadastroDbContext db) : IUserImportReadService
{
    /// <inheritdoc />
    public async Task<PagedResult<UserImportBatchDto>> ListBatchesAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.ImportBatches.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        // Id (UUIDv7) desempata datas iguais: a paginação fica estável.
        var batches = await query.OrderByDescending(batch => batch.CreatedAtUtc).ThenByDescending(batch => batch.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(Projection).ToListAsync(cancellationToken);
        var progress = await ProgressAsync(batches.Select(batch => batch.Id).ToList(), cancellationToken);
        return new PagedResult<UserImportBatchDto>(
            batches.Select(batch => ToDto(batch, progress.GetValueOrDefault(batch.Id))).ToList(), page, pageSize, total);
    }

    /// <inheritdoc />
    public async Task<UserImportBatchDto?> GetBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await db.ImportBatches.AsNoTracking().Where(b => b.Id == batchId).Select(Projection).FirstOrDefaultAsync(cancellationToken);
        if (batch is null)
        {
            return null;
        }

        var progress = await ProgressAsync([batchId], cancellationToken);
        return ToDto(batch, progress.GetValueOrDefault(batchId));
    }

    /// <inheritdoc />
    public async Task<PagedResult<UserImportLineDto>?> ListLinesAsync(
        Guid batchId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (!await db.ImportBatches.AsNoTracking().AnyAsync(batch => batch.Id == batchId, cancellationToken))
        {
            return null;
        }

        var query = db.ImportLines.AsNoTracking().Where(line => line.BatchId == batchId);
        if (status is not null)
        {
            if (!Enum.TryParse<ImportLineStatus>(status, ignoreCase: true, out var parsed))
            {
                return new PagedResult<UserImportLineDto>([], page, pageSize, 0);
            }

            query = query.Where(line => line.Status == parsed);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await ToLinesAsync(query.OrderBy(line => line.LineNumber).Skip((page - 1) * pageSize).Take(pageSize), cancellationToken);
        return new PagedResult<UserImportLineDto>(items, page, pageSize, total);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserImportLineDto>?> GetReportLinesAsync(Guid batchId, CancellationToken cancellationToken)
    {
        if (!await db.ImportBatches.AsNoTracking().AnyAsync(batch => batch.Id == batchId, cancellationToken))
        {
            return null;
        }

        // O lote tem no máximo 5.000 linhas (limite do upload), então o relatório inteiro cabe numa consulta.
        return await ToLinesAsync(db.ImportLines.AsNoTracking().Where(line => line.BatchId == batchId).OrderBy(line => line.LineNumber), cancellationToken);
    }

    private static readonly System.Linq.Expressions.Expression<Func<ImportBatch, BatchRow>> Projection = batch => new BatchRow(
        batch.Id, batch.Status, batch.FileHash, batch.ValidLines, batch.InvalidLines, batch.DuplicateLines,
        batch.CreatedAtUtc, batch.ConfirmedAtUtc, batch.FinishedAtUtc);

    private static async Task<List<UserImportLineDto>> ToLinesAsync(IQueryable<ImportLine> query, CancellationToken cancellationToken)
    {
        // Projeta só colunas sem dado pessoal; o texto dos enums é montado em memória.
        var rows = await query.Select(line => new LineRow(line.Id, line.LineNumber, line.Status, line.FailureReason, line.LegacyMarkStatus, line.ReprocessCount))
            .ToListAsync(cancellationToken);
        return rows.Select(row => new UserImportLineDto(
            row.Id, row.LineNumber, row.Status.ToString(), row.FailureReason, row.LegacyMarkStatus.ToString(), row.ReprocessCount)).ToList();
    }

    private async Task<Dictionary<Guid, ImportLineCounts>> ProgressAsync(IReadOnlyCollection<Guid> batchIds, CancellationToken cancellationToken)
    {
        var rows = await db.ImportLines.AsNoTracking().Where(line => batchIds.Contains(line.BatchId))
            .GroupBy(line => new { line.BatchId, line.Status })
            .Select(group => new StatusCount(group.Key.BatchId, group.Key.Status, group.Count()))
            .ToListAsync(cancellationToken);
        return rows.GroupBy(row => row.BatchId).ToDictionary(
            group => group.Key,
            group => new ImportLineCounts(
                Count(group, ImportLineStatus.Pending), Count(group, ImportLineStatus.Created), Count(group, ImportLineStatus.AlreadyExists),
                Count(group, ImportLineStatus.Error), Count(group, ImportLineStatus.Cancelled)));

        static int Count(IEnumerable<StatusCount> group, ImportLineStatus status) => group.Where(row => row.Status == status).Sum(row => row.Count);
    }

    private static UserImportBatchDto ToDto(BatchRow batch, ImportLineCounts? progress) => new(
        batch.Id,
        batch.Status.ToString(),
        batch.ValidLines,
        batch.InvalidLines,
        batch.DuplicateLines,
        batch.CreatedAtUtc,
        batch.ConfirmedAtUtc,
        batch.FinishedAtUtc,
        progress ?? new ImportLineCounts(0, 0, 0, 0, 0));

    private sealed record LineRow(Guid Id, int LineNumber, ImportLineStatus Status, string? FailureReason, LegacyMarkStatus LegacyMarkStatus, int ReprocessCount);

    private sealed record StatusCount(Guid BatchId, ImportLineStatus Status, int Count);

    private sealed record BatchRow(
        Guid Id,
        ImportBatchStatus Status,
        string FileHash,
        int ValidLines,
        int InvalidLines,
        int DuplicateLines,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? ConfirmedAtUtc,
        DateTimeOffset? FinishedAtUtc);
}
