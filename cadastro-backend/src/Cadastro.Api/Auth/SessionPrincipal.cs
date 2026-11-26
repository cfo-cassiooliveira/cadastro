using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Cadastro.Api.Auth;

/// <summary>
/// Monta a identidade mínima da sessão (minimização, LGPD): só id técnico, login, id da sessão no Keycloak, acr/auth_time e as
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

    /// <summary>Nível de autenticação (<c>otp</c> só quando o OTP foi digitado nesta autenticação).</summary>
    public const string AcrClaim = "acr";

    /// <summary>Hora (segundos Unix) da autenticação; no step-up é a hora do OTP.</summary>
    public const string AuthTimeClaim = "auth_time";

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

        // acr e auth_time sustentam a policy do step-up OTP: vêm do access token, ou do id token se ele os trouxer.
        foreach (var (type, value) in ReadStepUpClaims(accessToken, validatedIdToken))
        {
            identity.AddClaim(new Claim(type, value));
        }

        foreach (var role in ReadRealmRoles(accessToken))
        {
            identity.AddClaim(new Claim(RoleClaim, role));
        }

        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Identidade da sessão depois de renovar os tokens: as roles passam a ser as do access token novo (já validado).
    /// <c>acr</c> e <c>auth_time</c> continuam os do login: o refresh nunca adianta a hora do OTP nem concede <c>acr=otp</c>;
    /// se o token novo já não traz <c>acr=otp</c>, a sessão também o perde.
    /// </summary>
    public static ClaimsPrincipal Refresh(ClaimsPrincipal session, string accessToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var keepsOtp = new JsonWebToken(accessToken).TryGetPayloadValue<string>(AcrClaim, out var acr)
            && acr == OtpStepUpRequirement.OtpAcr;
        var identity = new ClaimsIdentity(session.Identity?.AuthenticationType, UserNameClaim, RoleClaim);
        identity.AddClaims(session.Claims
            .Where(claim => claim.Type != RoleClaim
                && (keepsOtp || claim.Type != AcrClaim || claim.Value != OtpStepUpRequirement.OtpAcr))
            .Select(claim => new Claim(claim.Type, claim.Value)));
        identity.AddClaims(ReadRealmRoles(accessToken).Select(role => new Claim(RoleClaim, role)));
        return new ClaimsPrincipal(identity);
    }

    // acr e auth_time sempre da mesma fonte (a que traz o acr): misturar tokens combinaria um acr com a hora de outro login.
    private static IEnumerable<(string Type, string Value)> ReadStepUpClaims(string? accessToken, ClaimsPrincipal idToken)
    {
        if (!string.IsNullOrEmpty(accessToken))
        {
            var token = new JsonWebToken(accessToken);
            if (token.TryGetPayloadValue<string>(AcrClaim, out var tokenAcr))
            {
                yield return (AcrClaim, tokenAcr);
                if (token.TryGetPayloadValue<long>(AuthTimeClaim, out var tokenAuthTime))
                {
                    yield return (AuthTimeClaim, tokenAuthTime.ToString(CultureInfo.InvariantCulture));
                }

                yield break;
            }
        }

        foreach (var type in new[] { AcrClaim, AuthTimeClaim })
        {
            if (idToken.FindFirst(type) is { } claim)
            {
                yield return (type, claim.Value);
            }
        }
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
