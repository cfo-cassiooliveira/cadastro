using System.Text.Json;

namespace Cadastro.Application.UserImports;

/// <summary>
/// Monta o envelope CloudEvents-like do steering de mensageria (<c>messageId</c>, <c>type</c>, <c>source</c>,
/// <c>occurredAt</c>, <c>correlationId</c>, <c>causationId</c>, <c>data</c>) em JSON camelCase. O <c>data</c> leva só ids.
/// </summary>
public static class EventEnvelope
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Serializa o envelope de uma linha aceita, com <c>data = { batchId, lineId }</c>.</summary>
    public static string CreateLineAccepted(
        Guid messageId, Guid batchId, Guid lineId, string? correlationId, string? causationId, DateTimeOffset occurredAt) =>
        JsonSerializer.Serialize(
            new
            {
                messageId,
                type = ImportLimits.LineAcceptedEventType,
                source = ImportLimits.EventSource,
                occurredAt = occurredAt.ToUniversalTime(),
                correlationId,
                causationId,
                data = new { batchId, lineId },
            },
            Options);
}
