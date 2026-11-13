using System.Net;
using System.Net.Http.Json;
using System.Web;
using Cadastro.Api.Auth;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Cadastro.Api.IntegrationTests.Auth;

/// <summary>Login OIDC completo contra um Keycloak real (US-01, US-08 e o BFF do ADR-0001).</summary>
[Collection(KeycloakTestGroup.Name)]
public sealed class BffLoginTests(KeycloakApiFactory factory)
{
    private const string Password = "Test-Only-Password-1!";

    // PAR (RFC 9126): redirect URI, PKCE e state vão pelo canal de servidor; o navegador só leva o request_uri.
    // O Keycloak só aceita o push com o redirect URI exato registrado, então o login completo abaixo prova o resto.
    [Fact]
    public async Task Login_RedirectsToKeycloakWithPushedAuthorizationRequest()
    {
        using var browser = new BrowserSession(factory);

        using var response = await browser.Bff.GetAsync(new Uri("/bff/login?returnUrl=/profile", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = response.Headers.Location!;
        location.ToString().ShouldStartWith($"{factory.KeycloakUrl}/realms/workspace/protocol/openid-connect/auth");
        var query = HttpUtility.ParseQueryString(location.Query);
        query["client_id"].ShouldBe("cadastro");
        query["request_uri"].ShouldStartWith("urn:ietf:params:oauth:request_uri:");
        query["code_challenge"].ShouldBeNull("PKCE não pode vazar na URL do navegador quando há PAR");
    }

    [Fact]
    public async Task SignIn_ReturnsToTheRequestedPageWithHardenedSessionCookie()
    {
        using var browser = new BrowserSession(factory);

        using var callback = await browser.SignInAsync("usuario.teste", Password, "/profile");

        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        callback.Headers.Location!.ToString().ShouldBe("/profile");
        var cookie = BrowserSession.SetCookie(callback, BffAuthenticationSetup.SessionCookieName)!.ToLowerInvariant();
        cookie.ShouldContain("httponly");
        cookie.ShouldContain("secure");
        cookie.ShouldContain("samesite=strict");
        cookie.ShouldContain("path=/");
        cookie.ShouldNotContain("domain=");
    }

    [Fact]
    public async Task User_AfterSignIn_ReturnsLoginAndRealmRolesWithoutPersonalData()
    {
        using var browser = new BrowserSession(factory);
        using var _ = await browser.SignInAsync("admin.teste", Password);

        using var response = await browser.Bff.GetAsync(new Uri("/bff/user", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var user = await response.Content.ReadFromJsonAsync<BffUserResponse>(TestContext.Current.CancellationToken);
        user!.IsAuthenticated.ShouldBeTrue();
        user.UserName.ShouldBe("admin.teste");
        user.Roles.ShouldContain("admin");
        user.Roles.ShouldContain("user");
        body.ShouldNotContain("@example.com");
        // O navegador nunca recebe token OAuth (BFF): só o token anti-CSRF.
        body.ShouldNotContain("access_token", Case.Insensitive);
        body.ShouldNotContain("refresh_token", Case.Insensitive);
        body.ShouldNotContain("id_token", Case.Insensitive);
        body.ShouldNotContain("eyJ");
    }

    [Fact]
    public async Task Logout_WithoutAntiforgeryToken_IsRejectedAndKeepsTheSession()
    {
        using var browser = new BrowserSession(factory);
        using var _ = await browser.SignInAsync("usuario.teste", Password);

        using var response = await browser.Bff.PostAsync(new Uri("/bff/logout", UriKind.Relative), null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await IsAuthenticatedAsync(browser)).ShouldBeTrue();
    }

    [Fact]
    public async Task Logout_WithAntiforgeryToken_EndsSessionAndPointsToKeycloakLogout()
    {
        using var browser = new BrowserSession(factory);
        using var _ = await browser.SignInAsync("usuario.teste", Password);
        var xsrf = await browser.GetAntiforgeryTokenAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/bff/logout", UriKind.Relative));
        request.Headers.Add(BffAuthenticationSetup.AntiforgeryHeaderName, xsrf);
        using var response = await browser.Bff.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var logout = await response.Content.ReadFromJsonAsync<LogoutResponse>(TestContext.Current.CancellationToken);
        var url = new Uri(logout!.LogoutUrl);
        url.ToString().ShouldStartWith($"{factory.KeycloakUrl}/realms/workspace/protocol/openid-connect/logout");
        var query = HttpUtility.ParseQueryString(url.Query);
        query["post_logout_redirect_uri"].ShouldBe("http://localhost:4300/");
        query["state"].ShouldBe("signed-out");
        query["id_token_hint"].ShouldNotBeNullOrEmpty();
        // Minimização (LGPD): o id token guardado na sessão traz o login, mas não e-mail nem nome.
        var idTokenPayload = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(query["id_token_hint"]);
        idTokenPayload.TryGetPayloadValue<string>("preferred_username", out var login).ShouldBeTrue();
        login.ShouldBe("usuario.teste");
        idTokenPayload.TryGetPayloadValue<string>("email", out var email).ShouldBeFalse(email);
        idTokenPayload.TryGetPayloadValue<string>("name", out var name).ShouldBeFalse(name);
        (await IsAuthenticatedAsync(browser)).ShouldBeFalse();
    }

    [Fact]
    public async Task ExpiredAccessToken_IsRefreshedAndTheSessionContinues()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = factory.WithClock(clock);
        using var browser = new BrowserSession(host);
        using var _ = await browser.SignInAsync("usuario.teste", Password);

        clock.Advance(TimeSpan.FromMinutes(11));

        (await IsAuthenticatedAsync(browser)).ShouldBeTrue();
    }

    // Regressão: requisições paralelas renovavam com o mesmo refresh token; o realm recusa o reuso e a sessão caía.
    [Fact]
    public async Task ExpiredAccessToken_RequestedConcurrently_IsRefreshedOnceAndTheSessionContinues()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = factory.WithClock(clock);
        using var browser = new BrowserSession(host);
        using var _ = await browser.SignInAsync("usuario.teste", Password);

        clock.Advance(TimeSpan.FromMinutes(11));
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => IsAuthenticatedAsync(browser)));

        concurrent.ShouldAllBe(isAuthenticated => isAuthenticated);
        (await IsAuthenticatedAsync(browser)).ShouldBeTrue();
        host.Services.GetRequiredService<FakeLogCollector>().GetSnapshot()
            .ShouldNotContain(record => record.Message.Contains("token refresh rejected", StringComparison.Ordinal));
    }

    // Regressão: com o Keycloak fora, o logout respondia 500 e a sessão local continuava aberta (OWASP A10).
    [Fact]
    public async Task Logout_WhenKeycloakIsUnavailable_StillEndsTheLocalSession()
    {
        var discovery = new DiscoverySwitch();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme,
                options => options.ConfigurationManager = discovery.Wrap(options.ConfigurationManager!))));
        using var browser = new BrowserSession(host);
        using var _ = await browser.SignInAsync("usuario.teste", Password);
        var xsrf = await browser.GetAntiforgeryTokenAsync();
        discovery.IsDown = true;

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/bff/logout", UriKind.Relative));
        request.Headers.Add(BffAuthenticationSetup.AntiforgeryHeaderName, xsrf);
        using var response = await browser.Bff.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<LogoutResponse>(TestContext.Current.CancellationToken))!
            .LogoutUrl.ShouldBe("http://localhost:4300/?state=signed-out");
        (await IsAuthenticatedAsync(browser)).ShouldBeFalse();
        host.Services.GetRequiredService<FakeLogCollector>().GetSnapshot().ShouldContain(record =>
            record.Level == LogLevel.Error && record.Message.Contains("signed out locally", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpiredAccessToken_WhenKeycloakSessionWasEnded_EndsTheLocalSession()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = factory.WithClock(clock);
        using var browser = new BrowserSession(host);
        using var _ = await browser.SignInAsync("admin.teste", Password);
        await KeycloakAdmin.EndAllSessionsAsync(factory.KeycloakUrl, "admin.teste");

        clock.Advance(TimeSpan.FromMinutes(11));

        (await IsAuthenticatedAsync(browser)).ShouldBeFalse();
        host.Services.GetRequiredService<FakeLogCollector>().GetSnapshot().ShouldContain(record =>
            record.Level == LogLevel.Warning && record.Message.Contains("token refresh rejected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignIn_LogsOnlyTheTechnicalUserId()
    {
        using var browser = new BrowserSession(factory);

        using var _ = await browser.SignInAsync("usuario.teste", Password);

        var records = factory.Logs.GetSnapshot();
        records.ShouldContain(record => record.Level == LogLevel.Information && record.Message.StartsWith("User ", StringComparison.Ordinal)
            && record.Message.EndsWith(" signed in", StringComparison.Ordinal));
        records.ShouldNotContain(record => record.Message.Contains("usuario.teste", StringComparison.OrdinalIgnoreCase)
            || record.Message.Contains("@example.com", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> IsAuthenticatedAsync(BrowserSession browser)
    {
        var user = await browser.Bff.GetFromJsonAsync<BffUserResponse>(new Uri("/bff/user", UriKind.Relative), TestContext.Current.CancellationToken);
        return user!.IsAuthenticated;
    }
}
