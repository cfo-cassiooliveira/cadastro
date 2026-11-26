namespace Cadastro.Application.UserImports;

/// <summary>Limites de paginação das consultas (paginação obrigatória, nunca coleção ilimitada).</summary>
public static class ImportPaging
{
    /// <summary>Tamanho de página padrão.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>Tamanho máximo de página.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Indica se a página e o tamanho são aceitáveis.</summary>
    public static bool IsValid(int page, int pageSize) => page >= 1 && pageSize is >= 1 and <= MaxPageSize;
}

/// <summary>Lista os lotes, mais recentes primeiro.</summary>
/// <param name="Page">Página (base 1).</param>
/// <param name="PageSize">Tamanho da página.</param>
public sealed record ListUserImportsQuery(int Page, int PageSize);

/// <summary>Lista as linhas de um lote.</summary>
/// <param name="BatchId">Lote.</param>
/// <param name="Status">Filtro de estado (nome do enum), opcional.</param>
/// <param name="Page">Página (base 1).</param>
/// <param name="PageSize">Tamanho da página.</param>
public sealed record ListUserImportLinesQuery(Guid BatchId, string? Status, int Page, int PageSize);

/// <summary>Caso de uso "Listar lotes" (leitura).</summary>
public interface IListUserImportsHandler
{
    /// <summary>Devolve a página de lotes; página inválida lança <see cref="ArgumentOutOfRangeException"/> (a borda valida antes).</summary>
    Task<PagedResult<UserImportBatchDto>> HandleAsync(ListUserImportsQuery query, CancellationToken cancellationToken);
}

/// <summary>Caso de uso "Detalhar lote" (leitura).</summary>
public interface IGetUserImportHandler
{
    /// <summary>Devolve o lote com o progresso por estado; <c>null</c> se não existe.</summary>
    Task<UserImportBatchDto?> HandleAsync(Guid batchId, CancellationToken cancellationToken);
}

/// <summary>Caso de uso "Listar linhas do lote" (leitura).</summary>
public interface IListUserImportLinesHandler
{
    /// <summary>Devolve a página de linhas; <c>null</c> se o lote não existe.</summary>
    Task<PagedResult<UserImportLineDto>?> HandleAsync(ListUserImportLinesQuery query, CancellationToken cancellationToken);
}

/// <summary>Caso de uso "Obter relatório por linha" (leitura).</summary>
public interface IGetUserImportReportHandler
{
    /// <summary>Devolve todas as linhas do relatório, sem dado pessoal; <c>null</c> se o lote não existe.</summary>
    Task<IReadOnlyList<UserImportLineDto>?> HandleAsync(Guid batchId, CancellationToken cancellationToken);
}

/// <summary>Lista lotes pela porta de leitura.</summary>
internal sealed class ListUserImportsHandler(IUserImportReadService readService) : IListUserImportsHandler
{
    /// <inheritdoc />
    public Task<PagedResult<UserImportBatchDto>> HandleAsync(ListUserImportsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.PageSize, ImportPaging.MaxPageSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.PageSize, 1);
        return readService.ListBatchesAsync(query.Page, query.PageSize, cancellationToken);
    }
}

/// <summary>Detalha um lote pela porta de leitura.</summary>
internal sealed class GetUserImportHandler(IUserImportReadService readService) : IGetUserImportHandler
{
    /// <inheritdoc />
    public Task<UserImportBatchDto?> HandleAsync(Guid batchId, CancellationToken cancellationToken) =>
        readService.GetBatchAsync(batchId, cancellationToken);
}

/// <summary>Lista linhas de um lote pela porta de leitura.</summary>
internal sealed class ListUserImportLinesHandler(IUserImportReadService readService) : IListUserImportLinesHandler
{
    /// <inheritdoc />
    public Task<PagedResult<UserImportLineDto>?> HandleAsync(ListUserImportLinesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.PageSize, ImportPaging.MaxPageSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.PageSize, 1);
        return readService.ListLinesAsync(query.BatchId, query.Status, query.Page, query.PageSize, cancellationToken);
    }
}

/// <summary>Obtém o relatório por linha pela porta de leitura.</summary>
internal sealed class GetUserImportReportHandler(IUserImportReadService readService) : IGetUserImportReportHandler
{
    /// <inheritdoc />
    public Task<IReadOnlyList<UserImportLineDto>?> HandleAsync(Guid batchId, CancellationToken cancellationToken) =>
        readService.GetReportLinesAsync(batchId, cancellationToken);
}
