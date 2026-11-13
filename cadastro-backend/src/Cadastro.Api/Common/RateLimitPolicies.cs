using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Cadastro.Api.Common;

/// <summary>Limites por IP para os endpoints anônimos (configuráveis em <c>RateLimiting:*</c>).</summary>
internal static partial class RateLimitPolicies
{
    /// <summary>
    /// Início do login: cada chamada faz o BFF enviar um pedido (PAR) ao Keycloak; sem limite, um script anônimo
    /// usaria o BFF para inundar o Keycloak.
    /// </summary>
    public const string Login = "login";

    /// <summary>Consulta da sessão (<c>/bff/user</c>), chamada a cada carregamento da SPA.</summary>
    public const string Session = "session";

    /// <summary>Recebimento de logs do navegador (evita inundar o Graylog).</summary>
    public const string ClientLogs = "client-logs";

    private const string LoggerCategory = "Cadastro.Api.RateLimiting";
    private const int DefaultLoginPermitsPerMinute = 10;
    private const int DefaultSessionPermitsPerMinute = 60;
    private const int DefaultClientLogsPermitsPerMinute = 30;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Registra as políticas por IP e loga cada rejeição (evento de segurança, OWASP A09).</summary>
    public static IServiceCollection AddCadastroRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var loginPermits = configuration.GetValue("RateLimiting:LoginPermitsPerMinute", DefaultLoginPermitsPerMinute);
        var sessionPermits = configuration.GetValue("RateLimiting:SessionPermitsPerMinute", DefaultSessionPermitsPerMinute);
        var clientLogPermits = configuration.GetValue("RateLimiting:ClientLogsPermitsPerMinute", DefaultClientLogsPermitsPerMinute);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Login, context => FixedWindowByIp(context, loginPermits));
            options.AddPolicy(Session, context => FixedWindowByIp(context, sessionPermits));
            options.AddPolicy(ClientLogs, context => FixedWindowByIp(context, clientLogPermits));
        });

        // O logger vem do DI (injeção explícita), não de service locator dentro do callback.
        services.AddOptions<RateLimiterOptions>().Configure<ILoggerFactory>((options, loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger(LoggerCategory);
            options.OnRejected = (context, _) =>
            {
                LogRejected(logger, context.HttpContext.Request.Path.Value ?? string.Empty);
                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    private static RateLimitPartition<string> FixedWindowByIp(HttpContext context, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = Window, QueueLimit = 0 });

    // Sem IP no log (dado pessoal, LGPD); a rota e o correlation_id bastam para investigar.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit exceeded on {RequestPath}")]
    private static partial void LogRejected(ILogger logger, string requestPath);
}
