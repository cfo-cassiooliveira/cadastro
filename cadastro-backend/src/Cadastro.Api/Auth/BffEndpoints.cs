using Cadastro.Api.Common;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Cadastro.Api.Auth;

/// <summary>Endpoints do BFF: iniciar o login, sessão atual (com token anti-CSRF) e logout no Cadastro e no Keycloak.</summary>
internal static partial class BffEndpoints
{
    // Devolvido pelo Keycloak no retorno do logout: o Angular mostra "Você saiu da sua conta.".
    private const string SignedOutState = "signed-out";

    /// <summary>Mapeia <c>/bff/login</c>, <c>/bff/user</c> e <c>/bff/logout</c>.</summary>
    public static IEndpointRouteBuilder MapBffEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/bff").WithTags("BFF");
        // Anônimo de propósito: é a porta de entrada do login.
        group.MapGet("/login", Login).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Login);
        // Anônimo de propósito: a SPA consulta a sessão antes de saber se há login.
        group.MapGet("/user", GetUser).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Session);
        group.MapPost("/logout", LogoutAsync).RequireAuthorization().AddEndpointFilter<AntiforgeryValidationFilter>();
        return app;
    }

    private static IResult Login(string? returnUrl, ILoggerFactory loggerFactory)
    {
        var target = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl;
        if (!LocalReturnUrl.IsLocal(target))
        {
            LogExternalReturnUrl(loggerFactory.CreateLogger(typeof(BffEndpoints).FullName!));
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The return URL must be local.",
                extensions: new Dictionary<string, object?> { ["code"] = "return_url.invalid" });
        }

        return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [OpenIdConnectDefaults.AuthenticationScheme]);
    }

    // O token anti-CSRF vai no corpo (a SPA guarda em memória e manda no header); o cookie que o valida é HttpOnly.
    private static Ok<BffUserResponse> GetUser(HttpContext context, IAntiforgery antiforgery)
    {
        var antiforgeryToken = antiforgery.GetAndStoreTokens(context).RequestToken!;
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return TypedResults.Ok(BffUserResponse.Anonymous(antiforgeryToken));
        }

        return TypedResults.Ok(new BffUserResponse(
            true,
            user.FindFirst(SessionPrincipal.UserNameClaim)?.Value,
            [.. user.FindAll(SessionPrincipal.RoleClaim).Select(claim => claim.Value)],
            antiforgeryToken));
    }

    private static async Task<Ok<LogoutResponse>> LogoutAsync(
        HttpContext context,
        IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
        IOptions<BffOidcOptions> bffOptions,
        ILoggerFactory loggerFactory)
    {
        var authentication = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var idToken = authentication.Properties?.GetTokenValue("id_token");
        var userId = context.User.FindFirst(SessionPrincipal.SubjectClaim)?.Value ?? "unknown";
        var logger = loggerFactory.CreateLogger(typeof(BffEndpoints).FullName!);
        var publicHome = bffOptions.Value.PublicOrigin.TrimEnd('/') + "/";

        // Encerra primeiro a sessão local (cookie e sessão no servidor): "Sair" sempre funciona no Cadastro,
        // mesmo com o Keycloak fora do ar (falha fechada, OWASP A10).
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        LogSignedOut(logger, userId);

        var options = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        string endSessionEndpoint;
        try
        {
            endSessionEndpoint = (await options.ConfigurationManager!.GetConfigurationAsync(context.RequestAborted)).EndSessionEndpoint;
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException
                                              && !context.RequestAborted.IsCancellationRequested)
        {
            // Sem o Keycloak não dá para encerrar o SSO agora; o usuário volta ao Início já sem sessão no Cadastro.
            LogSingleSignOutUnavailable(logger, exception, userId);
            return TypedResults.Ok(new LogoutResponse(QueryHelpers.AddQueryString(publicHome, "state", SignedOutState)));
        }

        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = options.ClientId,
            ["post_logout_redirect_uri"] = publicHome,
            ["state"] = SignedOutState,
        };
        if (!string.IsNullOrEmpty(idToken))
        {
            parameters["id_token_hint"] = idToken;
        }

        return TypedResults.Ok(new LogoutResponse(QueryHelpers.AddQueryString(endSessionEndpoint, parameters)));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in rejected: return URL is not local")]
    private static partial void LogExternalReturnUrl(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} signed out")]
    private static partial void LogSignedOut(ILogger logger, string userId);

    [LoggerMessage(Level = LogLevel.Error, Message = "User {UserId} signed out locally; identity provider unavailable to end the single sign-on session")]
    private static partial void LogSingleSignOutUnavailable(ILogger logger, Exception exception, string userId);
}
