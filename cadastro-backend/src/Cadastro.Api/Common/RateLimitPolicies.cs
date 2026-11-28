using System.Threading.RateLimiting;
using Cadastro.Api.Auth;
using Microsoft.AspNetCore.RateLimiting;

namespace Cadastro.Api.Common;

/// <summary>
/// Limites por IP (endpoints anônimos) e por usuário (pesquisa em logs e envio da importação), configuráveis em
/// <c>RateLimiting:*</c>. Janela fixa e em memória: simples, mas vale por instância e admite rajada na virada da janela.
/// </summary>
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

    /// <summary>Pesquisa em logs por token: limita por usuário (<c>sub</c>), não por IP, para conter o uso como oráculo.</summary>
    public const string LogSearch = "log-search";

    /// <summary>Upload da importação em lote: limita por usuário (<c>sub</c>), pois cada envio aciona o antivírus.</summary>
    public const string UserImports = "user-imports";

    private const string LoggerCategory = "Cadastro.Api.RateLimiting";
    private const int DefaultLoginPermitsPerMinute = 10;
    private const int DefaultSessionPermitsPerMinute = 60;
    private const int DefaultClientLogsPermitsPerMinute = 30;
    private const int DefaultLogSearchPermitsPerHour = 30;
    private const int DefaultUserImportsPermitsPerHour = 30;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LogSearchWindow = TimeSpan.FromHours(1);

    /// <summary>Registra as políticas por IP e loga cada rejeição (evento de segurança, OWASP A09).</summary>
    public static IServiceCollection AddCadastroRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var loginPermits = configuration.GetValue("RateLimiting:LoginPermitsPerMinute", DefaultLoginPermitsPerMinute);
        var sessionPermits = configuration.GetValue("RateLimiting:SessionPermitsPerMinute", DefaultSessionPermitsPerMinute);
        var clientLogPermits = configuration.GetValue("RateLimiting:ClientLogsPermitsPerMinute", DefaultClientLogsPermitsPerMinute);

        var logSearchPermits = configuration.GetValue("RateLimiting:LogSearchPermitsPerHour", DefaultLogSearchPermitsPerHour);
        var userImportsPermits = configuration.GetValue("RateLimiting:UserImportsPermitsPerHour", DefaultUserImportsPermitsPerHour);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Login, context => FixedWindowByIp(context, loginPermits));
            options.AddPolicy(Session, context => FixedWindowByIp(context, sessionPermits));
            options.AddPolicy(ClientLogs, context => FixedWindowByIp(context, clientLogPermits));
            options.AddPolicy(LogSearch, context => FixedWindowBySubject(context, logSearchPermits));
            options.AddPolicy(UserImports, context => FixedWindowBySubject(context, userImportsPermits));
        });

        // O logger vem do DI (injeção explícita), não de service locator dentro do callback.
        services.AddOptions<RateLimiterOptions>().Configure<ILoggerFactory>((options, loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger(LoggerCategory);
            options.OnRejected = (context, _) =>
            {
                var path = context.HttpContext.Request.Path.Value ?? string.Empty;
                if (context.HttpContext.User.FindFirst(SessionPrincipal.SubjectClaim)?.Value is { } userId)
                {
                    // Limite por usuário (pesquisa em logs): quem excedeu fica na trilha, sem audit_event (contrato do graylog).
                    LogRejectedForUser(logger, path, userId);
                }
                else
                {
                    LogRejected(logger, path);
                }

                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    // O endpoint exige autenticação: o sub sempre existe quando o limiter roda (o fallback nunca é usado na prática).
    private static RateLimitPartition<string> FixedWindowBySubject(HttpContext context, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst(SessionPrincipal.SubjectClaim)?.Value ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = LogSearchWindow, QueueLimit = 0 });

    private static RateLimitPartition<string> FixedWindowByIp(HttpContext context, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = Window, QueueLimit = 0 });

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit exceeded on {RequestPath} for user {UserId}")]
    private static partial void LogRejectedForUser(ILogger logger, string requestPath, string userId);

    // Sem IP no log (dado pessoal, LGPD); a rota e o correlation_id bastam para investigar.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit exceeded on {RequestPath}")]
    private static partial void LogRejected(ILogger logger, string requestPath);
}
