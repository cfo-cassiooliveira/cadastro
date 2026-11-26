using System.Text.RegularExpressions;

namespace Cadastro.Domain.UserImports;

/// <summary>Linha do lote: uma pessoa a criar, com os dados pessoais cifrados até o fim do processamento.</summary>
public sealed partial class ImportLine
{
    private ImportLine()
    {
    }

    /// <summary>Identificador da linha (vai na mensagem e no atributo <c>importLineId</c> do Keycloak).</summary>
    public Guid Id { get; private set; }

    /// <summary>Lote ao qual a linha pertence.</summary>
    public Guid BatchId { get; private set; }

    /// <summary>Número da linha no arquivo (base 1).</summary>
    public int LineNumber { get; private set; }

    /// <summary>Estado de processamento.</summary>
    public ImportLineStatus Status { get; private set; }

    /// <summary>Código do motivo da falha (ex.: <c>keycloak.unavailable</c>); nunca texto livre nem dado pessoal.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Resultado da marcação no Legado.</summary>
    public LegacyMarkStatus LegacyMarkStatus { get; private set; }

    /// <summary>Nome cifrado; <c>null</c> depois de apagado.</summary>
    public byte[]? NameCipher { get; private set; }

    /// <summary>E-mail cifrado; <c>null</c> depois de apagado.</summary>
    public byte[]? EmailCipher { get; private set; }

    /// <summary>CPF cifrado; <c>null</c> depois de apagado.</summary>
    public byte[]? CpfCipher { get; private set; }

    /// <summary>Telefone cifrado; <c>null</c> quando não informado ou depois de apagado.</summary>
    public byte[]? PhoneCipher { get; private set; }

    /// <summary>Índice cego (HMAC) do e-mail normalizado; <c>null</c> depois de apagado.</summary>
    public byte[]? EmailIndex { get; private set; }

    /// <summary>Versão da chave que gerou o índice cego do e-mail; <c>null</c> depois de apagado.</summary>
    public string? EmailIndexKeyVersion { get; private set; }

    /// <summary>Versão da chave que cifrou os dados; <c>null</c> depois de apagados.</summary>
    public string? KeyVersion { get; private set; }

    /// <summary>Quantas vezes a linha foi reaberta para reprocessamento depois de um erro definitivo.</summary>
    public int ReprocessCount { get; private set; }

    /// <summary>Criação da linha (UTC).</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Fim do processamento (UTC).</summary>
    public DateTimeOffset? FinishedAtUtc { get; private set; }

    /// <summary>Momento em que os dados pessoais foram apagados (UTC).</summary>
    public DateTimeOffset? PersonalDataErasedAtUtc { get; private set; }

    /// <summary>Indica se ainda há dados pessoais cifrados na linha.</summary>
    public bool HasPersonalData => NameCipher is not null;

    /// <summary>Cria uma linha pendente com os dados pessoais já cifrados.</summary>
    public static ImportLine Create(Guid id, Guid batchId, int lineNumber, ProtectedPersonalData data, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(data.KeyVersion);
        if (lineNumber < 1)
        {
            throw new ImportDomainException("Line number must be 1 or greater.");
        }

        return new ImportLine
        {
            Id = id,
            BatchId = batchId,
            LineNumber = lineNumber,
            Status = ImportLineStatus.Pending,
            NameCipher = data.Name,
            EmailCipher = data.Email,
            CpfCipher = data.Cpf,
            PhoneCipher = data.Phone,
            EmailIndex = data.EmailIndex,
            KeyVersion = data.KeyVersion,
            EmailIndexKeyVersion = data.EmailIndexKeyVersion,
            CreatedAtUtc = now.ToUniversalTime(),
        };
    }

    /// <summary>Devolve os dados cifrados para o consumidor decifrar; <c>null</c> se já foram apagados.</summary>
    public ProtectedPersonalData? GetProtectedData() =>
        HasPersonalData
            // Os campos são sempre criados e apagados em conjunto, então nenhum é nulo aqui.
            ? new ProtectedPersonalData(NameCipher!, EmailCipher!, CpfCipher!, PhoneCipher, EmailIndex!, KeyVersion!, EmailIndexKeyVersion)
            : null;

    /// <summary>Marca a linha como criada no Keycloak e apaga os dados pessoais (fim da linha).</summary>
    public void MarkCreated(DateTimeOffset now) => Finish(ImportLineStatus.Created, null, now, erase: true);

    /// <summary>Marca a linha como já existente e apaga os dados pessoais (fim da linha).</summary>
    public void MarkAlreadyExists(DateTimeOffset now) => Finish(ImportLineStatus.AlreadyExists, null, now, erase: true);

    /// <summary>
    /// Marca a falha definitiva. Os dados pessoais são mantidos para o reprocessamento manual (decisão Q8) e
    /// só saem por <see cref="ErasePersonalData"/> (expiração do relatório ou cancelamento).
    /// </summary>
    public void MarkFailed(string reasonCode, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        if (!ReasonCode().IsMatch(reasonCode))
        {
            throw new ImportDomainException("Failure reason must be a code (letters, digits, dot, dash or underscore), never free text.");
        }

        Finish(ImportLineStatus.Error, reasonCode, now, erase: false);
    }

    /// <summary>
    /// Reabre uma linha com erro definitivo para nova tentativa (decisão Q8). Só vale enquanto os dados pessoais ainda
    /// estão guardados; a linha volta a <see cref="ImportLineStatus.Pending"/> e a tentativa é contada.
    /// </summary>
    public void Reprocess()
    {
        if (Status != ImportLineStatus.Error)
        {
            throw new ImportDomainException($"Only lines with a definitive error can be reprocessed (status {Status}).");
        }

        if (!HasPersonalData)
        {
            throw new ImportDomainException("Line personal data was already erased; it cannot be reprocessed.");
        }

        Status = ImportLineStatus.Pending;
        FailureReason = null;
        FinishedAtUtc = null;
        ReprocessCount++;
    }

    /// <summary>Cancela a linha pendente (cancelamento do lote) e apaga os dados pessoais.</summary>
    public void Cancel(DateTimeOffset now) => Finish(ImportLineStatus.Cancelled, null, now, erase: true);

    /// <summary>Registra o resultado da marcação no Legado; só depois que a linha foi criada ou já existia.</summary>
    public void RecordLegacyMark(LegacyMarkStatus status)
    {
        if (Status is ImportLineStatus.Pending or ImportLineStatus.Error or ImportLineStatus.Cancelled)
        {
            throw new ImportDomainException("Legacy mark only applies to lines that were created or already existed.");
        }

        LegacyMarkStatus = status;
    }

    /// <summary>Apaga todos os dados pessoais da linha (cifrados e índice); idempotente.</summary>
    public void ErasePersonalData(DateTimeOffset now)
    {
        if (!HasPersonalData)
        {
            return;
        }

        NameCipher = null;
        EmailCipher = null;
        CpfCipher = null;
        PhoneCipher = null;
        EmailIndex = null;
        KeyVersion = null;
        EmailIndexKeyVersion = null;
        PersonalDataErasedAtUtc = now.ToUniversalTime();
    }

    private void Finish(ImportLineStatus status, string? reason, DateTimeOffset now, bool erase)
    {
        if (Status != ImportLineStatus.Pending)
        {
            throw new ImportDomainException($"Line already finished with status {Status}.");
        }

        Status = status;
        FailureReason = reason;
        FinishedAtUtc = now.ToUniversalTime();
        if (erase)
        {
            ErasePersonalData(now);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex ReasonCode();
}
