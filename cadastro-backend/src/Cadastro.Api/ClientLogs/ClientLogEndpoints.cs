using System.Text.RegularExpressions;
using Cadastro.Api.Common;
using Cadastro.Application.LogSearch;

namespace Cadastro.Api.ClientLogs;

/// <summary>
/// Recebe eventos de log do frontend e os registra no log da API (e, por ele, no Graylog), para que falhas no
/// navegador também sejam rastreáveis. A entrada é tratada como não confiável: validada, limitada e saneada.
/// </summary>
internal static partial class ClientLogEndpoints
{
    /// <summary>Nome do logger (categoria) dos eventos vindos do navegador.</summary>
    public const string LoggerCategory = "Cadastro.Web.Browser";

    private const int MaxMessageLength = 500;
    private const int MaxSourceLength = 100;
    private const int MaxStackLength = 4000;

    // Folga além do limite ao cortar antes de mascarar: limita o trabalho das expressões sobre entrada de qualquer
    // tamanho sem deixar sem reconhecimento um e-mail (até 254 caracteres) que cruze o limite.
    private const int MaskingMargin = 256;

    /// <summary>Mapeia <c>POST /api/v1/client-logs</c>.</summary>
    public static IEndpointRouteBuilder MapClientLogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/client-logs", Receive)
            .WithTags("ClientLogs")
            // Anônimo por requisito: falhas acontecem também antes do login (Início, cadastro).
            // Protegido por rate limit próprio e validação estrita do conteúdo.
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.ClientLogs)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem();
        return app;
    }

    /// <summary>Valida o evento e o registra com o nível correspondente.</summary>
    private static IResult Receive(ClientLogRequest request, ILoggerFactory loggerFactory, ILogPseudonymizer pseudonymizer)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var logger = loggerFactory.CreateLogger(LoggerCategory);
        var clientEvent = request.Event!; // Validado acima.
        var message = Sanitize(logger, pseudonymizer, request.Message, MaxMessageLength);
        var source = Sanitize(logger, pseudonymizer, WithoutQueryAndFragment(request.Source), MaxSourceLength);

        // Campos extras do evento do navegador; log_source permite filtrar no Graylog.
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["log_source"] = "browser",
            ["ClientStatus"] = request.Status,
            ["ClientStack"] = Sanitize(logger, pseudonymizer, request.Stack, MaxStackLength, keepNewLines: true),
        }))
        {
            switch (request.Level)
            {
                case "error":
                    LogBrowserError(logger, clientEvent, source, message);
                    break;
                case "warning":
                    LogBrowserWarning(logger, clientEvent, source, message);
                    break;
                default:
                    LogBrowserInformation(logger, clientEvent, source, message);
                    break;
            }
        }

        return TypedResults.NoContent();
    }

    private static Dictionary<string, string[]> Validate(ClientLogRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.Level is not ("error" or "warning" or "information"))
        {
            errors["level"] = ["client_log.invalid_level"];
        }

        if (request.Event is null || !EventPattern().IsMatch(request.Event))
        {
            errors["event"] = ["client_log.invalid_event"];
        }

        if (request.Status is < 0 or > 599)
        {
            errors["status"] = ["client_log.invalid_status"];
        }

        return errors;
    }

    /// <summary>
    /// Remove caracteres de controle (evita forjar linhas de log), troca e-mail, CNPJ, CPF e celular reconhecidos pelo
    /// formato pela máscara do ADR-0002 (defesa em profundidade: o frontend não deve enviar dados pessoais) e limita o
    /// tamanho.
    /// </summary>
    private static string Sanitize(ILogger logger, ILogPseudonymizer pseudonymizer, string? value, int maxLength, bool keepNewLines = false)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var bounded = Truncate(value, maxLength + MaskingMargin);
        var cleaned = (keepNewLines ? ControlExceptNewLine() : ControlCharacters()).Replace(bounded, " ");
        try
        {
            return Truncate(PersonalDataTextMasker.Mask(pseudonymizer, cleaned), maxLength);
        }
        catch (RegexMatchTimeoutException)
        {
            // Entrada hostil estourou o limite de tempo: o campo inteiro é descartado (nunca segue em claro) e o evento é registrado.
            LogMaskingTimedOut(logger);
            return PersonalDataTextMasker.Redacted;
        }
    }

    /// <summary>
    /// Origem sem query string nem fragmento: se o navegador mandar uma URL, os parâmetros (filtros, valores
    /// digitados) nunca chegam ao log.
    /// </summary>
    private static string? WithoutQueryAndFragment(string? source)
    {
        var end = source?.IndexOfAny(['?', '#']) ?? -1;
        return source is null || end < 0 ? source : source[..end];
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z0-9_]+){1,3}$")]
    private static partial Regex EventPattern();

    [GeneratedRegex("[\\p{Cc}]")]
    private static partial Regex ControlCharacters();

    [GeneratedRegex("[\\p{Cc}-[\\n]]")]
    private static partial Regex ControlExceptNewLine();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Browser log field replaced by redaction: personal data masking timed out")]
    private static partial void LogMaskingTimedOut(ILogger logger);

    // Mesmo template nos três níveis: o nível vem do próprio evento (regra de log do workspace).
    [LoggerMessage(Level = LogLevel.Error, Message = "Browser {ClientEvent} at {ClientSource}: {ClientMessage}")]
    private static partial void LogBrowserError(ILogger logger, string clientEvent, string clientSource, string clientMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Browser {ClientEvent} at {ClientSource}: {ClientMessage}")]
    private static partial void LogBrowserWarning(ILogger logger, string clientEvent, string clientSource, string clientMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Browser {ClientEvent} at {ClientSource}: {ClientMessage}")]
    private static partial void LogBrowserInformation(ILogger logger, string clientEvent, string clientSource, string clientMessage);
}
