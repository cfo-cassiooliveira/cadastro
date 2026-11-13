using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Cadastro.Api.Auth;

/// <summary>
/// BFF (security.md, ADR-0001): login OIDC no Keycloak com client confidencial, Authorization Code + PKCE S256, tokens
/// guardados no servidor e cookie <c>__Host-</c> para o navegador, que nunca vê token. Negar por padrão.
/// </summary>
internal static partial class BffAuthenticationSetup
{
    /// <summary>Cookie de sessão do Cadastro (só a chave da sessão guardada no servidor).</summary>
    public const string SessionCookieName = "__Host-cadastro";

    /// <summary>Header com o token anti-CSRF que a SPA recebe em <c>/bff/user</c> e guarda em memória.</summary>
    public const string AntiforgeryHeaderName = "X-XSRF-TOKEN";

    /// <summary>Cookie HttpOnly que o antiforgery usa para validar o token do header.</summary>
    public const string AntiforgeryCookieName = "__Host-cadastro-xsrf";

    /// <summary>Caminho do callback OIDC (redirect URI registrado no Keycloak).</summary>
    public const string CallbackPath = "/bff/signin-oidc";

    /// <summary>Policy: usuário autenticado acessando os próprios dados.</summary>
    public const string UsersSelfPolicy = "users:self";

    /// <summary>Policy: listagem de usuários (role <c>admin</c>).</summary>
    public const string UsersReadPolicy = "users:read";

    /// <summary>Encerramento da sessão por inatividade (spec 001, ADR-0001).</summary>
    public static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Registra cookie + OIDC, sessão no servidor, renovação de tokens, antiforgery e policies.</summary>
    public static IServiceCollection AddBffAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BffOidcOptions>()
            .Bind(configuration.GetSection(BffOidcOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddSingleton<InMemoryTicketStore>();
        services.AddSingleton<SessionTokenRefresher>();
        services.AddHttpClient(SessionTokenRefresher.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(5));

        if (configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
        {
            // Chaves que cifram cookie e antiforgery sobrevivem ao restart do container (volume).
            services.AddDataProtection().SetApplicationName("cadastro").PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        // Challenge padrão é o cookie (401 para a SPA); só o /bff/login desafia o OIDC e leva ao Keycloak.
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie()
            .AddOpenIdConnect();
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<InMemoryTicketStore, SessionTokenRefresher, TimeProvider, ILoggerFactory>(ConfigureCookie);
        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IOptions<BffOidcOptions>, TimeProvider, ILoggerFactory>(ConfigureOpenIdConnect);

        services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryHeaderName;
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(UsersSelfPolicy, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(UsersReadPolicy, policy => policy.RequireRole("admin"));
        return services;
    }

    private static void ConfigureCookie(
        CookieAuthenticationOptions options,
        InMemoryTicketStore ticketStore,
        SessionTokenRefresher refresher,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(BffAuthenticationSetup).FullName!);
        options.Cookie.Name = SessionCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = SessionIdleTimeout;
        options.SlidingExpiration = true;
        options.SessionStore = ticketStore;
        options.TimeProvider = timeProvider;
        options.Events.OnValidatePrincipal = refresher.ValidateAsync;
        // A SPA trata 401/403 (o BFF não tem páginas): sem redirect para login ou página de acesso negado.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            LogAccessDenied(logger, context.HttpContext.User.FindFirst(SessionPrincipal.SubjectClaim)?.Value ?? "unknown",
                context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }

    private static void ConfigureOpenIdConnect(
        OpenIdConnectOptions options,
        IOptions<BffOidcOptions> bffOptions,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        var settings = bffOptions.Value;
        var logger = loggerFactory.CreateLogger(typeof(BffAuthenticationSetup).FullName!);
        options.Authority = settings.Authority;
        if (!string.IsNullOrWhiteSpace(settings.MetadataAddress))
        {
            options.MetadataAddress = settings.MetadataAddress;
        }

        options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
        options.ClientId = settings.ClientId;
        options.ClientSecret = settings.ClientSecret;
        options.ResponseType = "code";
        // Query (e não form_post): o retorno é uma navegação GET same-site e os cookies de correlação usam SameSite=Lax.
        options.ResponseMode = "query";
        options.UsePkce = true;
        options.CallbackPath = CallbackPath;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;
        // Só "openid": o client traz o login por mapper próprio; nome e e-mail não entram nos tokens (minimização, LGPD).
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.TimeProvider = timeProvider;
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.TokenValidationParameters.NameClaimType = SessionPrincipal.UserNameClaim;
        options.TokenValidationParameters.RoleClaimType = SessionPrincipal.RoleClaim;

        var redirectUri = settings.PublicOrigin.TrimEnd('/') + CallbackPath;
        options.Events.OnRedirectToIdentityProvider = context =>
        {
            // Redirect URI exato do Keycloak, independente do Host repassado pelo nginx.
            context.ProtocolMessage.RedirectUri = redirectUri;
            return Task.CompletedTask;
        };
        options.Events.OnTokenValidated = context =>
        {
            context.Principal = SessionPrincipal.Create(
                context.Principal!, context.TokenEndpointResponse?.AccessToken, CookieAuthenticationDefaults.AuthenticationScheme);
            LogSignedIn(logger, context.Principal.FindFirst(SessionPrincipal.SubjectClaim)?.Value ?? "unknown");
            return Task.CompletedTask;
        };
        options.Events.OnRemoteFailure = context =>
        {
            // Login cancelado, state inválido ou erro do Keycloak: volta ao início sem expor detalhe técnico.
            LogSignInFailed(logger, context.Failure?.GetType().Name ?? "unknown");
            context.Response.Redirect("/?login=failed");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} signed in")]
    private static partial void LogSignedIn(ILogger logger, string userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in failed at the identity provider callback ({FailureType})")]
    private static partial void LogSignInFailed(ILogger logger, string failureType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Access denied for user {UserId} on {Path}")]
    private static partial void LogAccessDenied(ILogger logger, string userId, PathString path);
}
