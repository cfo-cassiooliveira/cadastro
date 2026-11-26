namespace Cadastro.Infrastructure.Persistence;

/// <summary>
/// Mensagem já processada pelo consumidor. A chave primária é o <c>messageId</c>: inserir de novo viola a unicidade, e é
/// assim que a entrega duplicada (at least once) é descartada na mesma transação do efeito (Art. VII.2). Consumidor na tarefa C5b.
/// </summary>
public sealed class InboxMessage
{
    private InboxMessage()
    {
    }

    /// <summary>Identificador da mensagem recebida (único).</summary>
    public Guid MessageId { get; private set; }

    /// <summary>Processamento concluído (UTC).</summary>
    public DateTimeOffset ProcessedAtUtc { get; private set; }

    /// <summary>Registra uma mensagem como processada.</summary>
    public static InboxMessage Create(Guid messageId, DateTimeOffset now) =>
        new() { MessageId = messageId, ProcessedAtUtc = now.ToUniversalTime() };
}
