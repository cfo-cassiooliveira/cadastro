using Cadastro.Domain.UserImports;

namespace Cadastro.Application.UserImports;

/// <summary>Resultado de gravar as mudanças de uma operação de escrita (uma transação).</summary>
public enum ImportSaveResult
{
    /// <summary>Tudo gravado.</summary>
    Saved = 0,

    /// <summary>Já existe lote ativo com o mesmo hash de arquivo (corrida entre dois envios iguais).</summary>
    DuplicateFileHash = 1,

    /// <summary>Outro processo alterou o lote ao mesmo tempo (concorrência otimista); nada foi gravado.</summary>
    ConcurrencyConflict = 2,

    /// <summary>A mensagem já foi processada (inbox): nada foi gravado.</summary>
    DuplicateMessage = 3,
}

/// <summary>Mensagem de integração a gravar na outbox, na mesma transação da mudança de estado (Art. VII.3).</summary>
/// <param name="MessageId">UUIDv7 da mensagem (<c>messageId</c> do envelope).</param>
/// <param name="Type">Routing key e tipo do evento.</param>
/// <param name="Payload">Envelope JSON completo, sem dados pessoais.</param>
/// <param name="TraceParent">Cabeçalho W3C <c>traceparent</c> da requisição de origem.</param>
/// <param name="OccurredAtUtc">Momento do fato.</param>
public sealed record IntegrationEvent(Guid MessageId, string Type, string Payload, string? TraceParent, DateTimeOffset OccurredAtUtc);

/// <summary>
/// Porta de escrita da importação (agregados <see cref="ImportBatch"/> e <see cref="ImportLine"/>, auditoria e outbox).
/// Os métodos Add/Enqueue só registram; <see cref="SaveChangesAsync"/> grava tudo numa única transação.
/// </summary>
public interface IUserImportWriter
{
    /// <summary>Id do lote não cancelado com o mesmo hash de arquivo; <c>null</c> se não existe.</summary>
    Task<Guid?> FindActiveBatchIdByHashAsync(string fileHash, CancellationToken cancellationToken);

    /// <summary>Carrega o lote para alteração (rastreado); <c>null</c> se não existe.</summary>
    Task<ImportBatch?> FindBatchAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>Carrega as linhas do lote para alteração (rastreadas).</summary>
    Task<IReadOnlyList<ImportLine>> GetLinesAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>Carrega uma linha do lote para alteração; <c>null</c> se não existe nele.</summary>
    Task<ImportLine?> FindLineAsync(Guid batchId, Guid lineId, CancellationToken cancellationToken);

    /// <summary>Carrega as linhas com erro definitivo do lote para alteração.</summary>
    Task<IReadOnlyList<ImportLine>> GetErrorLinesAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>Registra um lote novo com suas linhas.</summary>
    void AddBatch(ImportBatch batch, IEnumerable<ImportLine> lines);

    /// <summary>Registra um registro de auditoria.</summary>
    void AddAudit(ImportAuditEntry entry);

    /// <summary>Registra mensagens na outbox.</summary>
    void EnqueueEvents(IEnumerable<IntegrationEvent> events);

    /// <summary>Registra a mensagem como processada na inbox, na mesma transação do efeito (Art. VII.2).</summary>
    void AddInboxMessage(Guid messageId, DateTimeOffset now);

    /// <summary>Indica se a mensagem já foi processada (descarte rápido de duplicata antes do efeito).</summary>
    Task<bool> HasProcessedMessageAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>Grava tudo numa transação; conflitos previstos viram resultado, não exceção.</summary>
    Task<ImportSaveResult> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Contagem de linhas por estado, para o progresso do lote.</summary>
/// <param name="Pending">Aguardando processamento.</param>
/// <param name="Created">Criadas no Keycloak.</param>
/// <param name="AlreadyExists">Já existentes.</param>
/// <param name="Error">Com erro definitivo.</param>
/// <param name="Cancelled">Canceladas.</param>
public sealed record ImportLineCounts(int Pending, int Created, int AlreadyExists, int Error, int Cancelled);

/// <summary>Lote para listagem e detalhe, sem dado pessoal.</summary>
/// <param name="Id">Identificador.</param>
/// <param name="Status">Estado do lote (nome do enum).</param>
/// <param name="ValidLines">Linhas válidas.</param>
/// <param name="InvalidLines">Linhas inválidas.</param>
/// <param name="DuplicateLines">Linhas duplicadas.</param>
/// <param name="CreatedAtUtc">Criação.</param>
/// <param name="ConfirmedAtUtc">Confirmação.</param>
/// <param name="FinishedAtUtc">Fim.</param>
/// <param name="Progress">Linhas por estado.</param>
public sealed record UserImportBatchDto(
    Guid Id,
    string Status,
    int ValidLines,
    int InvalidLines,
    int DuplicateLines,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ConfirmedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    ImportLineCounts Progress);

/// <summary>Linha do relatório: estado e motivo por código, nunca dado pessoal.</summary>
/// <param name="Id">Identificador da linha.</param>
/// <param name="LineNumber">Número da linha no arquivo.</param>
/// <param name="Status">Estado da linha.</param>
/// <param name="FailureReason">Código do motivo da falha.</param>
/// <param name="LegacyMarkStatus">Resultado da marcação no Legado.</param>
/// <param name="ReprocessCount">Vezes em que foi reprocessada.</param>
public sealed record UserImportLineDto(Guid Id, int LineNumber, string Status, string? FailureReason, string LegacyMarkStatus, int ReprocessCount);

/// <summary>Página de resultados (paginação obrigatória nas listagens).</summary>
/// <typeparam name="T">Tipo do item.</typeparam>
/// <param name="Items">Itens da página.</param>
/// <param name="Page">Página (base 1).</param>
/// <param name="PageSize">Tamanho da página.</param>
/// <param name="TotalCount">Total de itens.</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>Porta de leitura da importação: projeta direto para DTO (sem rastreamento, sem dado pessoal).</summary>
public interface IUserImportReadService
{
    /// <summary>Lista os lotes, mais recentes primeiro.</summary>
    Task<PagedResult<UserImportBatchDto>> ListBatchesAsync(int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Detalhe de um lote com o progresso por estado; <c>null</c> se não existe.</summary>
    Task<UserImportBatchDto?> GetBatchAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>Linhas de um lote, paginadas e opcionalmente filtradas por estado; <c>null</c> se o lote não existe.</summary>
    Task<PagedResult<UserImportLineDto>?> ListLinesAsync(Guid batchId, string? status, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Todas as linhas do lote em ordem de número, para o relatório; <c>null</c> se o lote não existe.</summary>
    Task<IReadOnlyList<UserImportLineDto>?> GetReportLinesAsync(Guid batchId, CancellationToken cancellationToken);
}
