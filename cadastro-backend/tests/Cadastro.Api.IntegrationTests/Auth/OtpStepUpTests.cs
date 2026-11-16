using System.Security.Claims;
using System.Text;
using Cadastro.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Cadastro.Api.IntegrationTests.Auth;

/// <summary>Contrato do ../keycloak: policy <c>logs:investigate</c> = role + <c>acr=otp</c> + <c>auth_time</c> ≤ 5 min.</summary>
public sealed class OtpStepUpTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("otp", -10, true)]
    [InlineData("otp", -300, true)]
    [InlineData("otp", -301, false)]
    [InlineData("otp", 30, true)]
    [InlineData("otp", 120, false)]
    [InlineData("password", -10, false)]
    [InlineData("0", -10, false)]
    public void IsSatisfied_RequiresOtpAcrAndRecentAuthTime(string acr, int offsetSeconds, bool expected)
    {
        var user = Principal(("acr", acr), ("auth_time", Now.AddSeconds(offsetSeconds).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));

        OtpStepUpRequirement.IsSatisfied(user, Now).ShouldBe(expected);
    }

    [Theory]
    [InlineData("otp", null)]
    [InlineData("otp", "not-a-number")]
    [InlineData(null, "1794830400")]
    public void IsSatisfied_WithMissingOrMalformedClaims_IsFalse(string? acr, string? authTime)
    {
        var claims = new List<(string, string)>();
        if (acr is not null)
        {
            claims.Add(("acr", acr));
        }

        if (authTime is not null)
        {
            claims.Add(("auth_time", authTime));
        }

        OtpStepUpRequirement.IsSatisfied(Principal([.. claims]), Now).ShouldBeFalse();
    }

    [Fact]
    public void Create_KeepsAcrAndAuthTimeFromTheAccessToken()
    {
        var idToken = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "f1d2")]));

        var principal = SessionPrincipal.Create(idToken, Jwt("""{"acr":"otp","auth_time":1794830400,"realm_access":{"roles":["log-investigator"]}}"""), "Cookies");

        principal.FindFirst("acr")!.Value.ShouldBe("otp");
        principal.FindFirst("auth_time")!.Value.ShouldBe("1794830400");
        principal.IsInRole("log-investigator").ShouldBeTrue();
    }

    [Fact]
    public void Create_FallsBackToTheIdTokenClaims()
    {
        var idToken = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "f1d2"), new Claim("acr", "password"), new Claim("auth_time", "1794830000")]));

        var principal = SessionPrincipal.Create(idToken, Jwt("""{"sub":"x"}"""), "Cookies");

        principal.FindFirst("acr")!.Value.ShouldBe("password");
        principal.FindFirst("auth_time")!.Value.ShouldBe("1794830000");
    }

    [Fact]
    public void ApplyStepUp_AsksForOtpOnlyWhenTheLoginRequestedIt()
    {
        var withStepUp = new OpenIdConnectMessage();
        var without = new OpenIdConnectMessage();

        BffAuthenticationSetup.ApplyStepUp(withStepUp, new AuthenticationProperties { Items = { [BffAuthenticationSetup.StepUpItem] = "otp" } });
        BffAuthenticationSetup.ApplyStepUp(without, new AuthenticationProperties());

        withStepUp.AcrValues.ShouldBe("otp");
        without.AcrValues.ShouldBeNull();
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Test"));

    private static string Jwt(string payload) =>
        $"{Base64Url("""{"alg":"none","typ":"JWT"}""")}.{Base64Url(payload)}.";

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
