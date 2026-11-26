namespace Cadastro.Application.UserImports;

/// <summary>Quanto o job de retenção removeu num ciclo.</summary>
/// <param name="AuditEntries">Registros de auditoria vencidos (12 meses).</param>
/// <param name="Batches">Lotes com relatório vencido (90 dias).</param>
/// <param name="Lines">Linhas removidas junto com os lotes (inclui os dados cifrados das linhas com erro).</param>
public sealed record ImportRetentionResult(int AuditEntries, int Batches, int Lines)
{
    /// <summary>Indica se o ciclo removeu alguma coisa.</summary>
    public bool RemovedAnything => AuditEntries > 0 || Batches > 0 || Lines > 0;
}

/// <summary>Resultado de uma remoção de lotes vencidos.</summary>
/// <param name="Batches">Lotes removidos.</param>
/// <param name="Lines">Linhas removidas em cascata.</param>
public sealed record ExpiredBatchPurge(int Batches, int Lines);

/// <summary>Porta de remoção por retenção (Q9 da spec 002 do rabbitmq). Cada chamada remove no máximo <c>batchSize</c> itens.</summary>
public interface IImportRetentionStore
{
    /// <summary>Remove registros de auditoria cujo prazo (<c>ExpiresAtUtc</c>) já passou; devolve quantos removeu.</summary>
    Task<int> DeleteExpiredAuditEntriesAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Remove lotes cujo relatório (<c>ReportExpiresAtUtc</c>) já venceu; as linhas, com os dados cifrados das que terminaram
    /// em erro, saem em cascata. Lote sem prazo (ainda em processamento) nunca é removido.
    /// </summary>
    Task<ExpiredBatchPurge> DeleteExpiredBatchesAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);
}

/// <summary>Caso de uso do job de limpeza por retenção da importação.</summary>
public interface IPurgeExpiredImportDataHandler
{
    /// <summary>Remove tudo que já venceu, em lotes pequenos, até esgotar; devolve o total removido.</summary>
    Task<ImportRetentionResult> HandleAsync(int batchSize, CancellationToken cancellationToken);
}

/// <summary>
/// Aplica a retenção da importação: auditoria de 12 meses e relatório de 90 dias. Remove em lotes de tamanho limitado para não
/// segurar locks longos nem estourar o log de transações; o job pode ser interrompido e retomado sem perda (idempotente).
/// </summary>
public sealed class PurgeExpiredImportDataHandler(IImportRetentionStore store, TimeProvider clock) : IPurgeExpiredImportDataHandler
{
    /// <inheritdoc />
    public async Task<ImportRetentionResult> HandleAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var now = clock.GetUtcNow();

        var audit = 0;
        int removed;
        do
        {
            removed = await store.DeleteExpiredAuditEntriesAsync(now, batchSize, cancellationToken);
            audit += removed;
        }
        while (removed == batchSize);

        var batches = 0;
        var lines = 0;
        ExpiredBatchPurge purge;
        do
        {
            purge = await store.DeleteExpiredBatchesAsync(now, batchSize, cancellationToken);
            batches += purge.Batches;
            lines += purge.Lines;
        }
        while (purge.Batches == batchSize);

        return new ImportRetentionResult(audit, batches, lines);
    }
}
