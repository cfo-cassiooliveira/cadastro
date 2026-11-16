using System.Security.Claims;
using System.Text.Json;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cadastro.Api.IntegrationTests.Auth;

/// <summary>
/// Regressão do step-up de OTP: o handler OIDC roda as <c>ClaimActions</c> padrão depois do <c>OnTokenValidated</c> e
/// elas removem claims como <c>acr</c> da identidade; sem <c>acr</c> na sessão a policy <c>logs:investigate</c> nunca é
/// satisfeita em um login real (achado do E2E com Keycloak; os demais testes usam o <c>TestAuthHandler</c>).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class OidcClaimActionsTests(CadastroApiFactory factory)
{
    [Theory]
    [InlineData("acr", "otp")]
    [InlineData("auth_time", "1794830400")]
    public void OidcClaimActions_KeepTheStepUpClaimsOfTheSessionIdentity(string type, string value)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity([new Claim("sub", "f1d2"), new Claim(type, value)], "Cookies");
        using var emptyUserData = JsonDocument.Parse("{}");

        options.ClaimActions.ShouldNotBeEmpty();
        foreach (var action in options.ClaimActions)
        {
            action.Run(emptyUserData.RootElement, identity, "issuer");
        }

        identity.FindFirst(type).ShouldNotBeNull().Value.ShouldBe(value);
    }
}
