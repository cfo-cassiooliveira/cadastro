namespace Cadastro.Infrastructure.Persistence;

/// <summary>
/// Mensagem a publicar no RabbitMQ, gravada na mesma transação da mudança de estado (Transactional Outbox, Art. VII.3).
/// O payload leva só identificadores (<c>batchId</c>, <c>lineId</c>), nunca dados pessoais.
/// </summary>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
        Type = string.Empty;
        Payload = string.Empty;
    }

    /// <summary>Identificador da mensagem; vira o <c>messageId</c> que o consumidor usa na inbox.</summary>
    public Guid Id { get; private set; }

    /// <summary>Routing key do evento (ex.: <c>cadastro.user-import-line.accepted.v1</c>).</summary>
    public string Type { get; private set; }

    /// <summary>Corpo JSON da mensagem, sem dados pessoais.</summary>
    public string Payload { get; private set; }

    /// <summary>Cabeçalho W3C <c>traceparent</c> da requisição que originou a mensagem.</summary>
    public string? TraceParent { get; private set; }

    /// <summary>Gravação na outbox (UTC).</summary>
    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>Confirmação do broker (UTC); <c>null</c> enquanto pendente.</summary>
    public DateTimeOffset? PublishedAtUtc { get; private set; }

    /// <summary>Próxima tentativa permitida (UTC); o relay só pega a mensagem quando esta data chega (backoff com jitter).</summary>
    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    /// <summary>Tentativas de publicação já feitas.</summary>
    public int Attempts { get; private set; }

    /// <summary>Código curto do último erro de publicação (sem mensagem livre).</summary>
    public string? LastErrorCode { get; private set; }

    /// <summary>Cria uma mensagem pendente.</summary>
    public static OutboxMessage Create(Guid id, string type, string payload, string? traceParent, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        return new OutboxMessage
        {
            Id = id,
            Type = type,
            Payload = payload,
            TraceParent = traceParent,
            OccurredAtUtc = now.ToUniversalTime(),
            NextAttemptAtUtc = now.ToUniversalTime(),
        };
    }

    /// <summary>Registra a confirmação do broker; uma mensagem já publicada não pode ser publicada de novo.</summary>
    public void MarkPublished(DateTimeOffset now)
    {
        if (PublishedAtUtc is not null)
        {
            throw new InvalidOperationException("Outbox message was already published.");
        }

        PublishedAtUtc = now.ToUniversalTime();
        Attempts++;
        LastErrorCode = null;
    }

    /// <summary>Registra uma tentativa sem confirmação; a mensagem continua pendente e só volta ao relay em <paramref name="nextAttemptAtUtc"/>.</summary>
    public void MarkAttemptFailed(string errorCode, DateTimeOffset nextAttemptAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        Attempts++;
        LastErrorCode = errorCode;
        NextAttemptAtUtc = nextAttemptAtUtc.ToUniversalTime();
    }
}
