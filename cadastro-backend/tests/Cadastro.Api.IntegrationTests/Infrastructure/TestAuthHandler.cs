using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadastro.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Autenticação fake dos testes (sem Keycloak): monta o principal da sessão a partir dos headers
/// <c>X-Test-*</c>, com os mesmos tipos de claim que o BFF grava (<c>sub</c>, <c>role</c>, <c>acr</c>, <c>auth_time</c>).
/// Sem <c>X-Test-Sub</c> a requisição é anônima (401).
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Nome do esquema registrado nos testes.</summary>
    public const string SchemeName = "Test";

    /// <summary>Header com o <c>sub</c>.</summary>
    public const string SubHeader = "X-Test-Sub";

    /// <summary>Header com as roles separadas por vírgula.</summary>
    public const string RolesHeader = "X-Test-Roles";

    /// <summary>Header com o <c>acr</c>.</summary>
    public const string AcrHeader = "X-Test-Acr";

    /// <summary>Header com o <c>auth_time</c> em segundos Unix.</summary>
    public const string AuthTimeHeader = "X-Test-AuthTime";

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(SubHeader, out var sub))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(SchemeName, "preferred_username", "role");
        identity.AddClaim(new Claim("sub", sub.ToString()));
        foreach (var role in Request.Headers[RolesHeader].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            identity.AddClaim(new Claim("role", role));
        }

        if (Request.Headers.TryGetValue(AcrHeader, out var acr))
        {
            identity.AddClaim(new Claim("acr", acr.ToString()));
        }

        if (Request.Headers.TryGetValue(AuthTimeHeader, out var authTime))
        {
            identity.AddClaim(new Claim("auth_time", authTime.ToString()));
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
