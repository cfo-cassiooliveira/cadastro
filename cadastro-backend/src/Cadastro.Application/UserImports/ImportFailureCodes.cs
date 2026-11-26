namespace Cadastro.Application.UserImports;

/// <summary>Códigos de falha gravados na linha (<c>FailureReason</c>); nunca texto livre nem dado pessoal.</summary>
public static class ImportFailureCodes
{
    /// <summary>O provedor de identidade está indisponível (falha transitória).</summary>
    public const string IdentityUnavailable = "identity.unavailable";

    /// <summary>O provedor de identidade recusou o usuário (validação ou conflito).</summary>
    public const string IdentityRejected = "identity.rejected";

    /// <summary>Os ciclos de retentativa se esgotaram.</summary>
    public const string RetriesExhausted = "processing.retries_exhausted";

    /// <summary>Falha inesperada no processamento.</summary>
    public const string ProcessingFailed = "processing.failed";

    /// <summary>A mensagem não é um envelope válido de linha aceita.</summary>
    public const string MessageInvalid = "message.invalid";

    /// <summary>A linha da mensagem não existe.</summary>
    public const string LineNotFound = "line.not_found";

    /// <summary>Os dados pessoais da linha já foram apagados ou não podem ser decifrados.</summary>
    public const string PersonalDataUnavailable = "personal_data.unavailable";

    /// <summary>O lote não está em processamento.</summary>
    public const string BatchInvalidState = "batch.invalid_state";

    /// <summary>Conflitos de concorrência persistentes ao gravar o resultado.</summary>
    public const string ConcurrencyConflict = "db.concurrency_conflict";
}
