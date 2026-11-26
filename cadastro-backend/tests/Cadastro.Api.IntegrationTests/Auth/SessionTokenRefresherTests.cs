using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cadastro.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Cadastro.Api.IntegrationTests.Auth;

/// <summary>
/// Renovação da sessão sem Keycloak: o principal acompanha o access token novo (roles), sem adiantar nem conceder o OTP, e um
/// token que não passa na validação encerra a sessão (falha fechada).
/// </summary>
public sealed class SessionTokenRefresherTests : IDisposable
{
    private const string Issuer = "https://id.test/realms/workspace";
    private const string ClientId = "cadastro";
    private const string Subject = "f1d2";
    private const string LoginAuthTime = "1794830000";

    private readonly RSA _key = RSA.Create(2048);
    private readonly RSA _otherKey = RSA.Create(2048);

    [Fact]
    public async Task Validate_ExpiredToken_ReplacesEveryRoleAndKeepsTheLoginStepUpClaims()
    {
        var context = await RefreshAsync(Session("otp", "user", "admin", "user-importer"), Token(acr: "otp", roles: ["user", "log-investigator"]));

        var principal = context.Principal.ShouldNotBeNull();
        principal.FindAll("role").Select(c => c.Value).ShouldBe(["user", "log-investigator"]);
        principal.IsInRole("log-investigator").ShouldBeTrue();
        principal.FindFirst("acr")!.Value.ShouldBe("otp");
        // O token novo traz outra hora: a do OTP digitado continua valendo (o refresh não renova o step-up).
        principal.FindFirst("auth_time")!.Value.ShouldBe(LoginAuthTime);
        principal.FindFirst("sub")!.Value.ShouldBe(Subject);
        principal.Identity!.Name.ShouldBe("pessoa");
        context.ShouldRenew.ShouldBeTrue();
        context.Properties.GetTokenValue("refresh_token").ShouldBe("refresh-2");
    }

    [Theory]
    [InlineData("otp", "password", null)]
    [InlineData("otp", null, null)]
    [InlineData("password", "otp", "password")]
    public async Task Validate_RefreshNeverGrantsOtpAndDropsItWhenTheNewTokenHasNone(string sessionAcr, string? tokenAcr, string? expected)
    {
        var context = await RefreshAsync(Session(sessionAcr, "user"), Token(acr: tokenAcr, roles: ["user"]));

        context.Principal.ShouldNotBeNull().FindFirst("acr")?.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("client")]
    [InlineData("subject")]
    [InlineData("expired")]
    [InlineData("unsigned")]
    [InlineData("garbage")]
    public async Task Validate_UntrustedRefreshedToken_EndsTheSession(string defect)
    {
        var token = defect switch
        {
            "signature" => Token(key: _otherKey),
            "issuer" => Token(issuer: "https://evil.test/realms/workspace"),
            "client" => Token(client: "another-client"),
            "subject" => Token(subject: "someone-else"),
            "expired" => Token(expires: DateTime.UtcNow.AddHours(-1)),
            "unsigned" => new JsonWebTokenHandler().CreateToken($$"""{"iss":"{{Issuer}}","sub":"{{Subject}}","azp":"{{ClientId}}"}"""),
            _ => "not-a-token",
        };
        var authentication = Substitute.For<IAuthenticationService>();

        var context = await RefreshAsync(Session("otp", "user-importer"), token, authentication);

        context.Principal.ShouldBeNull();
        await authentication.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(), CookieAuthenticationDefaults.AuthenticationScheme, Arg.Any<AuthenticationProperties?>());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _key.Dispose();
        _otherKey.Dispose();
    }

    private static ClaimsPrincipal Session(string acr, params string[] roles)
    {
        List<Claim> claims = [new("sub", Subject), new("preferred_username", "pessoa"), new("acr", acr), new("auth_time", LoginAuthTime)];
        claims.AddRange(roles.Select(role => new Claim("role", role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, "preferred_username", "role"));
    }

    private string Token(
        string? acr = "otp", string[]? roles = null, RSA? key = null, string issuer = Issuer, string client = ClientId,
        string subject = Subject, DateTime? expires = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["azp"] = client,
            ["auth_time"] = 1794839999,
            ["realm_access"] = new Dictionary<string, object> { ["roles"] = roles ?? ["user"] },
        };
        if (acr is not null)
        {
            claims["acr"] = acr;
        }

        var end = expires ?? DateTime.UtcNow.AddMinutes(10);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Claims = claims,
            IssuedAt = end.AddMinutes(-10),
            NotBefore = end.AddMinutes(-10),
            Expires = end,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key ?? _key) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256),
        });
    }

    private async Task<CookieValidatePrincipalContext> RefreshAsync(
        ClaimsPrincipal session, string newAccessToken, IAuthenticationService? authentication = null)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer, TokenEndpoint = Issuer + "/token" };
        configuration.SigningKeys.Add(new RsaSecurityKey(_key.ExportParameters(false)) { KeyId = "k1" });
        var options = new OpenIdConnectOptions
        {
            ClientId = ClientId,
            ClientSecret = "test-only",
            // Chave desconhecida força a busca do discovery; aqui o endereço só devolve algo que não é discovery.
            MetadataAddress = Issuer + "/.well-known/openid-configuration",
            ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration),
        };
        var monitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        monitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(options);
        using var http = new HttpClient(new TokenEndpoint(newAccessToken));
        var clients = Substitute.For<IHttpClientFactory>();
        clients.CreateClient(SessionTokenRefresher.HttpClientName).Returns(http);
        var refresher = new SessionTokenRefresher(clients, monitor, TimeProvider.System, NullLogger<SessionTokenRefresher>.Instance);

        var properties = new AuthenticationProperties();
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = "old" },
            new AuthenticationToken { Name = "refresh_token", Value = "refresh-1" },
            new AuthenticationToken { Name = "expires_at", Value = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o", CultureInfo.InvariantCulture) },
        ]);
        await using var services = new ServiceCollection()
            .AddSingleton(authentication ?? Substitute.For<IAuthenticationService>())
            .BuildServiceProvider();
        var context = new CookieValidatePrincipalContext(
            new DefaultHttpContext { RequestServices = services },
            new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(session, properties, CookieAuthenticationDefaults.AuthenticationScheme));

        await refresher.ValidateAsync(context);
        return context;
    }

    /// <summary>Token endpoint falso: devolve o access token escolhido pelo teste.</summary>
    private sealed class TokenEndpoint(string accessToken) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { access_token = accessToken, refresh_token = "refresh-2", expires_in = 600 }),
                    Encoding.UTF8,
                    "application/json"),
            });
    }
}
