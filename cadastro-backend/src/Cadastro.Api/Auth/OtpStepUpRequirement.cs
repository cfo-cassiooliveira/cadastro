using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Cadastro.Api.Auth;

/// <summary>
/// Exige que o OTP tenha sido digitado há pouco: <c>acr=otp</c> e <c>auth_time</c> de no máximo
/// <see cref="MaxAge"/> atrás. A role sozinha não basta: SSO por cookie e refresh de token antigo trazem a role sem
/// OTP, e o refresh de um token de step-up mantém <c>acr=otp</c> por até 10 h (contrato do ../keycloak).
/// </summary>
internal sealed class OtpStepUpRequirement : IAuthorizationRequirement
{
    /// <summary>Valor de <c>acr</c> emitido pelo Keycloak quando o OTP foi digitado nesta autenticação.</summary>
    public const string OtpAcr = "otp";

    /// <summary>Idade máxima do <c>auth_time</c> (a hora do step-up).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    // Tolerância de relógio entre o Keycloak e o BFF; auth_time muito no futuro é recusado.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>Se o principal tem <c>acr=otp</c> e <c>auth_time</c> recente em <paramref name="now"/>.</summary>
    public static bool IsSatisfied(ClaimsPrincipal user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.FindFirst(SessionPrincipal.AcrClaim)?.Value != OtpAcr
            || !long.TryParse(user.FindFirst(SessionPrincipal.AuthTimeClaim)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(seconds);
        return age >= -ClockSkew && age <= MaxAge;
    }
}

/// <summary>Avalia <see cref="OtpStepUpRequirement"/> com o relógio injetado.</summary>
internal sealed class OtpStepUpHandler(TimeProvider timeProvider) : AuthorizationHandler<OtpStepUpRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OtpStepUpRequirement requirement)
    {
        if (OtpStepUpRequirement.IsSatisfied(context.User, timeProvider.GetUtcNow()))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
