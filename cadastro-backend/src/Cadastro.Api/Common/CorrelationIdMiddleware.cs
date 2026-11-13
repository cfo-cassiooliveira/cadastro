using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Cadastro.Api.Common;

/// <summary>
/// Lê ou gera o <c>X-Correlation-Id</c>, devolve no response e o inclui no escopo de log (Art. VI.2).
/// </summary>
internal sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    /// <summary>Header HTTP que transporta o identificador de correlação.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Resolve o correlation id, devolve no header da resposta e abre o escopo de log da requisição.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        context.Response.Headers[HeaderName] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object> { ["correlation_id"] = correlationId }))
        {
            await next(context);
        }
    }

    /// <summary>Indica se um id vindo do cliente tem formato seguro para ir aos logs (evita log injection).</summary>
    public static bool IsSafeId(string? value) => value is not null && SafeIdPattern().IsMatch(value);

    private static string ResolveCorrelationId(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        // Aceita só formato seguro para evitar log injection com valor vindo do cliente.
        if (IsSafeId(incoming))
        {
            return incoming;
        }

        return Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
    }

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex SafeIdPattern();
}
