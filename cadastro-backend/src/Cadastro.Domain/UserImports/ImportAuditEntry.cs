namespace Cadastro.Domain.UserImports;

/// <summary>Registro imutável da trilha de auditoria da importação (quem fez o quê e quando), sem dados pessoais.</summary>
public sealed class ImportAuditEntry
{
    private const int MaxActionLength = 100;
    private const int MaxActorLength = 200;
    private const int MaxCorrelationLength = 100;

    private ImportAuditEntry()
    {
        Actor = string.Empty;
        Action = string.Empty;
    }

    /// <summary>Identificador do registro.</summary>
    public Guid Id { get; private set; }

    /// <summary>Momento do evento (UTC).</summary>
    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>Identificador do ator (<c>sub</c> do Keycloak), nunca nome ou e-mail.</summary>
    public string Actor { get; private set; }

    /// <summary>Código da ação (ex.: <c>batch.confirmed</c>, <c>access.denied</c>).</summary>
    public string Action { get; private set; }

    /// <summary>Lote relacionado, quando houver.</summary>
    public Guid? BatchId { get; private set; }

    /// <summary>Identificador de correlação da requisição (W3C Trace Context).</summary>
    public string? CorrelationId { get; private set; }

    /// <summary>Quando o registro pode ser descartado (12 meses após o evento).</summary>
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    /// <summary>Cria um registro de auditoria; <paramref name="action"/> deve ser um código, não texto livre com dados pessoais.</summary>
    public static ImportAuditEntry Create(Guid id, string actor, string action, Guid? batchId, string? correlationId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        if (actor.Length > MaxActorLength || action.Length > MaxActionLength || correlationId?.Length > MaxCorrelationLength)
        {
            throw new ImportDomainException("Audit field exceeds the allowed length.");
        }

        var occurred = now.ToUniversalTime();
        return new ImportAuditEntry
        {
            Id = id,
            OccurredAtUtc = occurred,
            Actor = actor,
            Action = action,
            BatchId = batchId,
            CorrelationId = correlationId,
            ExpiresAtUtc = occurred.AddMonths(ImportRetention.AuditMonths),
        };
    }
}
