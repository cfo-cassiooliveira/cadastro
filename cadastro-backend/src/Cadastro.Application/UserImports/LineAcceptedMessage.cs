using System.Text.Json;

namespace Cadastro.Application.UserImports;

/// <summary>Conteúdo útil do envelope da linha aceita (só ids; nunca dado pessoal).</summary>
/// <param name="MessageId">Id do envelope (chave da inbox).</param>
/// <param name="BatchId">Lote da linha.</param>
/// <param name="LineId">Linha a processar.</param>
/// <param name="CorrelationId">Correlação de origem, quando houver.</param>
public sealed record LineAcceptedMessage(Guid MessageId, Guid BatchId, Guid LineId, string? CorrelationId)
{
    /// <summary>Lê o envelope JSON; devolve <c>false</c> para corpo inválido, tipo diferente ou ids ausentes.</summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out LineAcceptedMessage? message)
    {
        message = null;
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.GetString() != ImportLimits.LineAcceptedEventType
                || !root.TryGetProperty("messageId", out var messageId) || !messageId.TryGetGuid(out var messageGuid)
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("batchId", out var batchId) || !batchId.TryGetGuid(out var batchGuid)
                || !data.TryGetProperty("lineId", out var lineId) || !lineId.TryGetGuid(out var lineGuid))
            {
                return false;
            }

            var correlation = root.TryGetProperty("correlationId", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            message = new LineAcceptedMessage(messageGuid, batchGuid, lineGuid, correlation);
            return true;
        }
        catch (JsonException)
        {
            // Corpo que não é JSON é mensagem inválida (vai à DLQ), não falha do processamento.
            return false;
        }
    }
}
