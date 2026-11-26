using Cadastro.Domain.UserImports;
using Microsoft.Extensions.Logging;

namespace Cadastro.Application.UserImports;

/// <summary>Caso de uso do job de retenção: cancela lotes esquecidos em aguardando confirmação.</summary>
public interface ICancelAbandonedImportsHandler
{
    /// <summary>
    /// Cancela até <paramref name="maxBatches"/> lotes parados além de <see cref="ImportRetention.AbandonedBatch"/>, apagando os dados
    /// pessoais das linhas pendentes. Devolve quantos cancelou; chamar de novo até devolver 0.
    /// </summary>
    Task<int> HandleAsync(int maxBatches, CancellationToken cancellationToken);
}

/// <summary>
/// Cancela os lotes que ninguém confirmou nem cancelou dentro do prazo, para que dado pessoal cifrado de um lote que talvez nunca
/// seja processado não fique guardado sem limite (Q9 da spec 002, LGPD). O cancelamento é o mesmo do administrador
/// (<see cref="ImportBatch.Cancel"/>), com auditoria em nome do sistema.
/// </summary>
public sealed partial class CancelAbandonedImportsHandler(
    IImportRetentionStore store, IUserImportWriter writer, TimeProvider clock, ILogger<CancelAbandonedImportsHandler> logger)
    : ICancelAbandonedImportsHandler
{
    /// <summary>Autor da auditoria do cancelamento automático.</summary>
    public const string SystemActor = "system:retention";

    /// <inheritdoc />
    public async Task<int> HandleAsync(int maxBatches, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatches, 1);
        var now = clock.GetUtcNow();
        var ids = await store.FindAbandonedBatchIdsAsync(now - ImportRetention.AbandonedBatch, maxBatches, cancellationToken);

        var cancelled = 0;
        foreach (var id in ids)
        {
            var batch = await writer.FindBatchAsync(id, cancellationToken);
            if (batch is null)
            {
                continue;
            }

            var lines = await writer.GetLinesAsync(id, cancellationToken);
            try
            {
                batch.Cancel(lines, now);
            }
            catch (ImportDomainException)
            {
                // O administrador confirmou ou cancelou entre a seleção e aqui: nada a fazer.
                continue;
            }

            writer.AddAudit(ImportAuditEntry.Create(Guid.CreateVersion7(), SystemActor, "batch.abandoned-cancelled", id, null, now));
            if (await writer.SaveChangesAsync(cancellationToken) == ImportSaveResult.Saved)
            {
                cancelled++;
                LogCancelled(id);
            }
        }

        return cancelled;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Import batch {BatchId} cancelled automatically: not confirmed within the retention period")]
    private partial void LogCancelled(Guid batchId);
}
