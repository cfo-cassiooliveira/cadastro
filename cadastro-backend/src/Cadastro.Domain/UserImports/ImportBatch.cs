using System.Text.RegularExpressions;

namespace Cadastro.Domain.UserImports;

/// <summary>
/// Lote de importação: um arquivo CSV validado, confirmado em dois passos e processado linha a linha. A confirmação separada
/// permite ver quantas linhas passaram antes de criar qualquer conta. Os contadores ficam no lote para saber quando a última
/// linha terminou; o custo é que todas as linhas do lote atualizam o mesmo registro.
/// </summary>
public sealed partial class ImportBatch
{
    private ImportBatch()
    {
        FileHash = string.Empty;
        CreatedBy = string.Empty;
    }

    /// <summary>Identificador do lote.</summary>
    public Guid Id { get; private set; }

    /// <summary>SHA-256 do arquivo em hexadecimal minúsculo; base da idempotência do reenvio.</summary>
    public string FileHash { get; private set; }

    /// <summary>Estado atual do lote.</summary>
    public ImportBatchStatus Status { get; private set; }

    /// <summary>Linhas válidas, que serão processadas após a confirmação.</summary>
    public int ValidLines { get; private set; }

    /// <summary>Linhas rejeitadas na validação (resumo exibido antes de confirmar).</summary>
    public int InvalidLines { get; private set; }

    /// <summary>Linhas duplicadas dentro do arquivo (resumo exibido antes de confirmar).</summary>
    public int DuplicateLines { get; private set; }

    /// <summary>Linhas que viraram usuário no Keycloak.</summary>
    public int CreatedCount { get; private set; }

    /// <summary>Linhas cuja pessoa já existia.</summary>
    public int AlreadyExistsCount { get; private set; }

    /// <summary>Linhas com falha definitiva.</summary>
    public int ErrorCount { get; private set; }

    /// <summary>Identificador (<c>sub</c>) do administrador que enviou o arquivo.</summary>
    public string CreatedBy { get; private set; }

    /// <summary>Criação do lote (UTC).</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Confirmação pelo administrador (UTC).</summary>
    public DateTimeOffset? ConfirmedAtUtc { get; private set; }

    /// <summary>Fim do lote, concluído ou cancelado (UTC).</summary>
    public DateTimeOffset? FinishedAtUtc { get; private set; }

    /// <summary>Quando o relatório por linha deixa de ser retido (90 dias após o fim).</summary>
    public DateTimeOffset? ReportExpiresAtUtc { get; private set; }

    /// <summary>
    /// Token de concorrência otimista (rowversion): um comando do administrador baseado numa leitura antiga falha em vez de
    /// sobrescrever o lote. O término das linhas não depende dele: soma os contadores com um <c>UPDATE</c> atômico.
    /// </summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Linhas que já terminaram (criadas, já existentes ou com erro).</summary>
    public int FinishedLines => CreatedCount + AlreadyExistsCount + ErrorCount;

    /// <summary>Cria um lote aguardando confirmação.</summary>
    public static ImportBatch Create(
        Guid id, string fileHash, string createdBy, int validLines, int invalidLines, int duplicateLines, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        ArgumentNullException.ThrowIfNull(fileHash);
        if (!Sha256Hex().IsMatch(fileHash))
        {
            throw new ImportDomainException("File hash must be a lowercase SHA-256 hexadecimal string.");
        }

        if (validLines < 0 || invalidLines < 0 || duplicateLines < 0)
        {
            throw new ImportDomainException("Line counters cannot be negative.");
        }

        return new ImportBatch
        {
            Id = id,
            FileHash = fileHash,
            CreatedBy = createdBy,
            Status = ImportBatchStatus.AwaitingConfirmation,
            ValidLines = validLines,
            InvalidLines = invalidLines,
            DuplicateLines = duplicateLines,
            CreatedAtUtc = now.ToUniversalTime(),
        };
    }

    /// <summary>Confirma o lote (segundo passo); só é possível com ao menos uma linha válida.</summary>
    public void Confirm(DateTimeOffset now)
    {
        if (Status != ImportBatchStatus.AwaitingConfirmation)
        {
            throw new ImportDomainException($"Batch cannot be confirmed from status {Status}.");
        }

        if (ValidLines == 0)
        {
            throw new ImportDomainException("Batch has no valid lines to process.");
        }

        Status = ImportBatchStatus.Processing;
        ConfirmedAtUtc = now.ToUniversalTime();
    }

    /// <summary>
    /// Descarta o lote antes da confirmação. As linhas pendentes são canceladas e seus dados pessoais apagados, para não
    /// sobrar dado cifrado de um lote que nunca será processado.
    /// </summary>
    /// <param name="lines">Linhas do lote carregadas pelo chamador.</param>
    /// <param name="now">Momento do cancelamento.</param>
    public void Cancel(IEnumerable<ImportLine> lines, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (Status != ImportBatchStatus.AwaitingConfirmation)
        {
            throw new ImportDomainException($"Batch cannot be cancelled from status {Status}.");
        }

        foreach (var line in lines)
        {
            EnsureBelongs(line);
            if (line.Status == ImportLineStatus.Pending)
            {
                line.Cancel(now);
            }
        }

        Status = ImportBatchStatus.Cancelled;
        Finish(now);
    }

    /// <summary>
    /// Reprocessamento manual (decisão Q8): reabre uma linha com erro definitivo, ajusta o contador de erros e, se o lote
    /// já tinha terminado, volta-o para <see cref="ImportBatchStatus.Processing"/>. Devolve o registro de auditoria
    /// da tentativa, que o chamador grava na mesma transação.
    /// </summary>
    /// <param name="line">Linha com erro, deste lote.</param>
    /// <param name="actor">Identificador (<c>sub</c>) do administrador que pediu o reprocessamento.</param>
    /// <param name="correlationId">Correlação da requisição.</param>
    /// <param name="now">Momento do pedido.</param>
    public ImportAuditEntry Reprocess(ImportLine line, string actor, string? correlationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (Status is not (ImportBatchStatus.Processing or ImportBatchStatus.CompletedWithErrors))
        {
            throw new ImportDomainException($"Batch cannot reprocess lines from status {Status}.");
        }

        EnsureBelongs(line);
        line.Reprocess();
        ErrorCount--;
        if (Status == ImportBatchStatus.CompletedWithErrors)
        {
            Status = ImportBatchStatus.Processing;
            FinishedAtUtc = null;
            ReportExpiresAtUtc = null;
        }

        return ImportAuditEntry.Create(Guid.CreateVersion7(), actor, "line.reprocess-requested", Id, correlationId, now);
    }

    private void EnsureBelongs(ImportLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.BatchId != Id)
        {
            throw new ImportDomainException("Line does not belong to this batch.");
        }
    }

    /// <summary>Contabiliza o término de uma linha e encerra o lote quando a última termina.</summary>
    public void RecordLineOutcome(ImportLineStatus outcome, DateTimeOffset now)
    {
        if (Status != ImportBatchStatus.Processing)
        {
            throw new ImportDomainException($"Batch is not processing (status {Status}).");
        }

        if (FinishedLines >= ValidLines)
        {
            throw new ImportDomainException("All lines of the batch have already finished.");
        }

        switch (outcome)
        {
            case ImportLineStatus.Created:
                CreatedCount++;
                break;
            case ImportLineStatus.AlreadyExists:
                AlreadyExistsCount++;
                break;
            case ImportLineStatus.Error:
                ErrorCount++;
                break;
            default:
                throw new ImportDomainException($"Outcome {outcome} is not a final line status.");
        }

        if (FinishedLines == ValidLines)
        {
            Status = ErrorCount == 0 ? ImportBatchStatus.Completed : ImportBatchStatus.CompletedWithErrors;
            Finish(now);
        }
    }

    private void Finish(DateTimeOffset now)
    {
        var finished = now.ToUniversalTime();
        FinishedAtUtc = finished;
        ReportExpiresAtUtc = finished + ImportRetention.Report;
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();
}
