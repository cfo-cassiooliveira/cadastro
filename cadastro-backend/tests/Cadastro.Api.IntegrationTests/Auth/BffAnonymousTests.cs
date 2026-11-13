using System.Net;
using System.Net.Http.Json;
using Cadastro.Api.Auth;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Cadastro.Api.IntegrationTests.Auth;

/// <summary>BFF sem sessão: visitante, open redirect e negação por padrão.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class BffAnonymousTests(CadastroApiFactory factory)
{
    [Fact]
    public async Task User_WhenAnonymous_ReturnsVisitorWithAntiforgeryTokenAndOnlyHttpOnlyCookies()
    {
        using var client = HttpsClient();

        using var response = await client.GetAsync(new Uri("/bff/user", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var user = await response.Content.ReadFromJsonAsync<BffUserResponse>(TestContext.Current.CancellationToken);
        user!.IsAuthenticated.ShouldBeFalse();
        user.UserName.ShouldBeNull();
        user.AntiforgeryToken.ShouldNotBeNullOrWhiteSpace();
        var validationCookie = BrowserSession.SetCookie(response, BffAuthenticationSetup.AntiforgeryCookieName)!.ToLowerInvariant();
        validationCookie.ShouldContain("httponly");
        validationCookie.ShouldContain("secure");
        validationCookie.ShouldContain("samesite=strict");
        // Nenhum cookie legível por JavaScript: o token vai no corpo e fica em memória na SPA.
        response.Headers.GetValues("Set-Cookie").ShouldAllBe(cookie => cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    // Regressão: no compose local (http://localhost) o antiforgery lançava 500 ao gravar o cookie Secure.
    [Fact]
    public async Task User_OverLocalHttpWithLocalSecureContext_IssuesSecureCookies()
    {
        using var local = factory.WithWebHostBuilder(builder => builder.UseSetting("Bff:LocalSecureContext", "true"));
        using var client = local.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });

        using var response = await client.GetAsync(new Uri("/bff/user", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        BrowserSession.SetCookie(response, BffAuthenticationSetup.AntiforgeryCookieName)!.ToLowerInvariant().ShouldContain("secure");
    }

    [Theory]
    [InlineData("https://evil.example.com/")]
    [InlineData("//evil.example.com")]
    [InlineData("/\\evil.example.com")]
    public async Task Login_WithExternalReturnUrl_IsRejected(string returnUrl)
    {
        using var client = HttpsClient();

        using var response = await client.GetAsync(
            new Uri($"/bff/login?returnUrl={Uri.EscapeDataString(returnUrl)}", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("return_url.invalid");
        factory.Logs.GetSnapshot().ShouldContain(record =>
            record.Level == LogLevel.Warning && record.Message == "Sign-in rejected: return URL is not local");
    }

    [Fact]
    public async Task Logout_WhenAnonymous_Returns401WithoutRedirect()
    {
        using var client = HttpsClient();

        using var response = await client.PostAsync(new Uri("/bff/logout", UriKind.Relative), null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Location.ShouldBeNull();
    }

    [Fact]
    public async Task UnknownEndpoint_IsDeniedByDefault()
    {
        using var client = HttpsClient();

        using var response = await client.GetAsync(new Uri("/api/v1/anything", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.NotFound);
    }

    private HttpClient HttpsClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("https://localhost"),
    });
}
