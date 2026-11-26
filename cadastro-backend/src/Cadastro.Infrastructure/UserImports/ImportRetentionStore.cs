using Cadastro.Application.UserImports;
using Cadastro.Domain.UserImports;
using Cadastro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cadastro.Infrastructure.UserImports;

/// <summary>
/// Remove do esquema <c>import</c> o que já passou da retenção. Seleciona os ids em lote e apaga por <c>ExecuteDelete</c>
/// (sem carregar entidades, logo sem materializar dado pessoal); as linhas saem pela cascata da chave estrangeira.
/// </summary>
internal sealed class ImportRetentionStore(CadastroDbContext db) : IImportRetentionStore
{
    /// <inheritdoc />
    public async Task<int> DeleteExpiredAuditEntriesAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var ids = await db.AuditEntries
            .Where(a => a.ExpiresAtUtc <= now)
            .OrderBy(a => a.ExpiresAtUtc)
            .Select(a => a.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return 0;
        }

        return await db.AuditEntries.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ExpiredBatchPurge> DeleteExpiredBatchesAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var ids = await db.ImportBatches
            .Where(b => b.ReportExpiresAtUtc != null && b.ReportExpiresAtUtc <= now)
            .OrderBy(b => b.ReportExpiresAtUtc)
            .Select(b => b.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return new ExpiredBatchPurge(0, 0);
        }

        // Conta antes de apagar: depois da cascata não há mais como saber quantas linhas saíram.
        var lines = await db.ImportLines.CountAsync(l => ids.Contains(l.BatchId), cancellationToken);
        var batches = await db.ImportBatches.Where(b => ids.Contains(b.Id)).ExecuteDeleteAsync(cancellationToken);
        return new ExpiredBatchPurge(batches, lines);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> FindAbandonedBatchIdsAsync(DateTimeOffset createdBefore, int limit, CancellationToken cancellationToken) =>
        await db.ImportBatches
            .Where(b => b.Status == ImportBatchStatus.AwaitingConfirmation && b.CreatedAtUtc <= createdBefore)
            .OrderBy(b => b.CreatedAtUtc)
            .Select(b => b.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
}
