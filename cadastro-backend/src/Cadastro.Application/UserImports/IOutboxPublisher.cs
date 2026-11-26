namespace Cadastro.Application.UserImports;

/// <summary>Mensagem da outbox pronta para ir ao broker.</summary>
/// <param name="MessageId">Identificador (<c>message-id</c> AMQP).</param>
/// <param name="RoutingKey">Routing key e tipo do evento.</param>
/// <param name="Body">Envelope JSON completo.</param>
/// <param name="TraceParent">Cabeçalho W3C <c>traceparent</c> a propagar nos headers AMQP.</param>
/// <param name="OccurredAtUtc">Momento do fato.</param>
public sealed record OutboundMessage(Guid MessageId, string RoutingKey, string Body, string? TraceParent, DateTimeOffset OccurredAtUtc);

/// <summary>Desfecho da publicação de uma mensagem: só <see cref="Confirmed"/> autoriza marcá-la como publicada.</summary>
/// <param name="MessageId">Mensagem.</param>
/// <param name="Confirmed">O broker confirmou (publisher confirm ack) e a mensagem foi roteada.</param>
/// <param name="ErrorCode">Código curto da falha (<c>broker.unavailable</c>, <c>broker.nack</c>, <c>broker.unroutable</c>...).</param>
public sealed record PublishOutcome(Guid MessageId, bool Confirmed, string? ErrorCode)
{
    /// <summary>Indica falha de conexão/canal (e não de uma mensagem específica), que justifica pausar o relay inteiro.</summary>
    public bool IsBrokerLevelFailure => ErrorCode == PublishErrorCodes.BrokerUnavailable;
}

/// <summary>Códigos de falha de publicação (persistidos em <c>LastErrorCode</c>, nunca texto livre).</summary>
public static class PublishErrorCodes
{
    /// <summary>Conexão ou canal indisponível.</summary>
    public const string BrokerUnavailable = "broker.unavailable";

    /// <summary>O broker recusou a mensagem (nack).</summary>
    public const string Nack = "broker.nack";

    /// <summary>Nenhuma fila recebeu a mensagem (basic.return com <c>mandatory</c>).</summary>
    public const string Unroutable = "broker.unroutable";

    /// <summary>Falha inesperada ao publicar ou timeout da confirmação.</summary>
    public const string PublishFailed = "broker.publish_failed";
}

/// <summary>Porta de publicação da outbox. Nunca lança por falha de broker: devolve o desfecho de cada mensagem.</summary>
public interface IOutboxPublisher
{
    /// <summary>Publica as mensagens e devolve um desfecho por mensagem, na mesma ordem.</summary>
    Task<IReadOnlyList<PublishOutcome>> PublishAsync(IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken);
}
