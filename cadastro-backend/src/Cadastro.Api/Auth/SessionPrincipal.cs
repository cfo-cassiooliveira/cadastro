using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Cadastro.Api.Auth;

/// <summary>
/// Monta a identidade mínima da sessão (minimização, LGPD): só id técnico, login, id da sessão no Keycloak e as
/// roles do realm, lidas do access token (o id token do Keycloak não traz <c>realm_access</c>).
/// </summary>
internal static class SessionPrincipal
{
    /// <summary>Claim do id técnico do usuário no Keycloak.</summary>
    public const string SubjectClaim = "sub";

    /// <summary>Claim do login exibido no cabeçalho.</summary>
    public const string UserNameClaim = "preferred_username";

    /// <summary>Claim de cada role do realm.</summary>
    public const string RoleClaim = "role";

    private const string SessionIdClaim = "sid";

    /// <summary>Cria a identidade da sessão a partir do id token validado e do access token recebido do Keycloak.</summary>
    public static ClaimsPrincipal Create(ClaimsPrincipal validatedIdToken, string? accessToken, string authenticationType)
    {
        ArgumentNullException.ThrowIfNull(validatedIdToken);
        var identity = new ClaimsIdentity(authenticationType, UserNameClaim, RoleClaim);
        foreach (var type in new[] { SubjectClaim, UserNameClaim, SessionIdClaim })
        {
            if (validatedIdToken.FindFirst(type) is { } claim)
            {
                identity.AddClaim(new Claim(type, claim.Value));
            }
        }

        foreach (var role in ReadRealmRoles(accessToken))
        {
            identity.AddClaim(new Claim(RoleClaim, role));
        }

        return new ClaimsPrincipal(identity);
    }

    /// <summary>Lê <c>realm_access.roles</c> do access token (recebido direto do token endpoint pela rede interna).</summary>
    public static IReadOnlyList<string> ReadRealmRoles(string? accessToken)
    {
        if (string.IsNullOrEmpty(accessToken))
        {
            return [];
        }

        var token = new JsonWebToken(accessToken);
        if (!token.TryGetPayloadValue<JsonElement>("realm_access", out var realmAccess)
            || realmAccess.ValueKind != JsonValueKind.Object
            || !realmAccess.TryGetProperty("roles", out var roles)
            || roles.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. roles.EnumerateArray().Where(role => role.ValueKind == JsonValueKind.String).Select(role => role.GetString()!)];
    }
}
