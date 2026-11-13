using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cadastro.Api.Health;

/// <summary>Escreve o relatório de saúde em JSON (status, duração e dados de cada check), sem exceções.</summary>
internal static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Serializa o relatório de saúde em JSON indentado.</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 2),
                    description = entry.Value.Description,
                    tags = entry.Value.Tags,
                    data = entry.Value.Data,
                }),
        };

        context.Response.ContentType = "application/json";
        // Cliente que desistiu da requisição (timeout do orquestrador, conexão fechada): não há para quem escrever.
        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions), context.RequestAborted);
    }
}
