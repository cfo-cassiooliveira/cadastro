using System.Net;
using System.Security.Claims;
using System.Text;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Cadastro.Api.IntegrationTests.Auth;

/// <summary>Login com step-up de OTP: o pedido leva <c>acr_values=otp</c> ao Keycloak e o retorno é conferido.</summary>
[Collection(KeycloakTestGroup.Name)]
public sealed class OtpStepUpLoginTests(KeycloakApiFactory factory)
{
    [Fact]
    public async Task Login_WithAcrValuesOtp_SendsAcrValuesToTheIdentityProvider()
    {
        string? sent = null;
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                var previous = options.Events.OnPushAuthorization;
                options.Events.OnPushAuthorization = context =>
                {
                    sent = context.ProtocolMessage.AcrValues;
                    return previous(context);
                };
            })));
        using var browser = new BrowserSession(host);

        using var response = await browser.Bff.GetAsync(
            new Uri("/bff/login?returnUrl=/log-search&acr_values=otp", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        sent.ShouldBe("otp");
    }

    [Fact]
    public async Task Login_WithoutAcrValues_DoesNotAskForStepUp()
    {
        string? sent = "unset";
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                var previous = options.Events.OnPushAuthorization;
                options.Events.OnPushAuthorization = context =>
                {
                    sent = context.ProtocolMessage.AcrValues;
                    return previous(context);
                };
            })));
        using var browser = new BrowserSession(host);

        using var response = await browser.Bff.GetAsync(new Uri("/bff/login", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        sent.ShouldBeNull();
    }

    [Theory]
    [InlineData("password", 10, false)]
    [InlineData("otp", 600, false)]
    [InlineData("otp", 10, true)]
    public async Task Callback_WithStepUpRequested_FailsUnlessOtpIsFresh(string acr, int ageSeconds, bool accepted)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        var authTime = DateTimeOffset.UtcNow.AddSeconds(-ageSeconds).ToUnixTimeSeconds();
        var accessToken = Jwt("{\"acr\":\"" + acr + "\",\"auth_time\":" + authTime + ",\"realm_access\":{\"roles\":[\"log-investigator\"]}}");
        var context = new TokenValidatedContext(
            new DefaultHttpContext { RequestServices = factory.Services },
            new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler)),
            options,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "f1d2")], "oidc")),
            new AuthenticationProperties { Items = { [Cadastro.Api.Auth.BffAuthenticationSetup.StepUpItem] = "otp" } })
        {
            TokenEndpointResponse = new OpenIdConnectMessage { AccessToken = accessToken },
        };

        await options.Events.OnTokenValidated(context);

        (context.Result?.Failure is null).ShouldBe(accepted);
        if (accepted)
        {
            context.Principal!.FindFirst("acr")!.Value.ShouldBe("otp");
        }
    }

    private static string Jwt(string payload) =>
        $"{Base64Url("""{"alg":"none","typ":"JWT"}""")}.{Base64Url(payload)}.";

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
