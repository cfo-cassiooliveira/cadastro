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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Cadastro.Api.IntegrationTests.Auth;

/// <summary>
/// Rotação da chave de assinatura do realm com o <see cref="ConfigurationManager{T}"/> real (o mesmo do OIDC), que depois de
/// <c>RequestRefresh</c> ainda entrega o JWKS antigo enquanto busca o novo em segundo plano.
/// </summary>
public sealed class SessionTokenRefresherKeyRotationTests : IDisposable
{
    private const string Subject = "f1d2";

    private readonly FakeIdentityProvider _provider = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FakeLogCollector _logs = new();
    private readonly HttpClient _http;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _manager;
    private readonly SessionTokenRefresher _refresher;
    private int _refreshes;

    public SessionTokenRefresherKeyRotationTests()
    {
        _http = new HttpClient(_provider);
        _manager = new ConfigurationManager<OpenIdConnectConfiguration>(
            FakeIdentityProvider.MetadataAddress, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(_http));
        var options = new OpenIdConnectOptions
        {
            ClientId = "cadastro",
            ClientSecret = "test-only",
            MetadataAddress = FakeIdentityProvider.MetadataAddress,
            ConfigurationManager = _manager,
        };
        var monitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        monitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(options);
        var clients = Substitute.For<IHttpClientFactory>();
        clients.CreateClient(SessionTokenRefresher.HttpClientName).Returns(_http);
        _refresher = new SessionTokenRefresher(clients, monitor, _clock, new FakeLogger<SessionTokenRefresher>(_logs));
    }

    [Fact]
    public async Task Validate_RightAfterTheRealmRotatesItsKey_KeepsTheSession()
    {
        // O gerenciador já guardou o JWKS antigo (como depois de um login).
        await _manager.GetConfigurationAsync(TestContext.Current.CancellationToken);
        _provider.Rotate();

        var principal = await RefreshAsync(_provider.IssueToken());

        principal.ShouldNotBeNull().IsInRole("user").ShouldBeTrue();
        // A segunda sessão, logo em seguida, também é atendida sem nova busca.
        var fetches = _provider.JwksRequests;
        (await RefreshAsync(_provider.IssueToken())).ShouldNotBeNull();
        _provider.JwksRequests.ShouldBeLessThanOrEqualTo(fetches + 1);
    }

    [Fact]
    public async Task Validate_TokensSignedWithUnknownKeys_FetchTheKeysAtMostOncePerInterval()
    {
        await _manager.GetConfigurationAsync(TestContext.Current.CancellationToken);
        using var forged = RSA.Create(2048);
        var before = _provider.JwksRequests;

        for (var i = 0; i < 5; i++)
        {
            (await RefreshAsync(_provider.IssueToken(forged, "forged-" + i))).ShouldBeNull();
        }

        // Uma busca aguardada do refresher e, no máximo, a atualização em segundo plano do gerenciador.
        _provider.JwksRequests.ShouldBeInRange(before + 1, before + 2);
        var afterFirstInterval = _provider.JwksRequests;
        _clock.Advance(TimeSpan.FromSeconds(31));
        (await RefreshAsync(_provider.IssueToken(forged, "forged-late"))).ShouldBeNull();
        _provider.JwksRequests.ShouldBeGreaterThan(afterFirstInterval);
    }

    [Fact]
    public async Task Validate_RefusedToken_LogsTheReasonOnceWithoutTheToken()
    {
        using var forged = RSA.Create(2048);
        var token = _provider.IssueToken(forged, "forged");

        (await RefreshAsync(token)).ShouldBeNull();

        var record = _logs.GetSnapshot().Where(r => r.Level >= LogLevel.Warning).ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Warning);
        record.Message.ShouldContain(nameof(SecurityTokenSignatureKeyNotFoundException));
        record.Message.ShouldNotContain(token);
        record.Message.ShouldNotContain("eyJ");
    }

    [Fact]
    public async Task Validate_WhenTheKeysCannotBeFetched_EndsTheSessionAndLogsOneError()
    {
        await _manager.GetConfigurationAsync(TestContext.Current.CancellationToken);
        _provider.Rotate();
        _provider.KeysAreDown = true;

        (await RefreshAsync(_provider.IssueToken())).ShouldBeNull();

        var record = _logs.GetSnapshot().Where(r => r.Level >= LogLevel.Warning).ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Error);
        record.Exception.ShouldNotBeNull();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _provider.Dispose();
    }

    private async Task<ClaimsPrincipal?> RefreshAsync(string newAccessToken)
    {
        _provider.NextAccessToken = newAccessToken;
        var session = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Subject)], "Cookies", "preferred_username", "role"));
        var properties = new AuthenticationProperties();
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = "old" },
            new AuthenticationToken { Name = "refresh_token", Value = "refresh-" + ++_refreshes },
            new AuthenticationToken { Name = "expires_at", Value = _clock.GetUtcNow().AddMinutes(-1).ToString("o", CultureInfo.InvariantCulture) },
        ]);
        await using var services = new ServiceCollection().AddSingleton(Substitute.For<IAuthenticationService>()).BuildServiceProvider();
        var context = new CookieValidatePrincipalContext(
            new DefaultHttpContext { RequestServices = services },
            new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(session, properties, "Cookies"));

        await _refresher.ValidateAsync(context);
        return context.Principal;
    }

    /// <summary>Discovery, JWKS e token endpoint falsos, com uma chave de assinatura que pode ser trocada.</summary>
    private sealed class FakeIdentityProvider : HttpMessageHandler
    {
        private const string Issuer = "https://id.test/realms/workspace";
        private static readonly string[] Roles = ["user"];
        public const string MetadataAddress = Issuer + "/.well-known/openid-configuration";

        private RSA _key = RSA.Create(2048);
        private int _version = 1;
        private int _jwksRequests;

        public int JwksRequests => Volatile.Read(ref _jwksRequests);

        public bool KeysAreDown { get; set; }

        public string NextAccessToken { get; set; } = string.Empty;

        public void Rotate()
        {
            _key.Dispose();
            _key = RSA.Create(2048);
            _version++;
        }

        public string IssueToken(RSA? key = null, string? keyId = null) =>
            new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = Subject,
                    ["azp"] = "cadastro",
                    ["realm_access"] = new Dictionary<string, object> { ["roles"] = Roles },
                },
                Expires = DateTime.UtcNow.AddMinutes(10),
                SigningCredentials = new SigningCredentials(
                    new RsaSecurityKey(key ?? _key) { KeyId = keyId ?? "k" + _version }, SecurityAlgorithms.RsaSha256),
            });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/token", StringComparison.Ordinal))
            {
                return Json(new { access_token = NextAccessToken, refresh_token = "rotated", expires_in = 600 });
            }

            if (path.EndsWith("/certs", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _jwksRequests);
                if (KeysAreDown)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }

                var parameters = _key.ExportParameters(false);
                return Json(new
                {
                    keys = new[]
                    {
                        new
                        {
                            kty = "RSA", use = "sig", alg = "RS256", kid = "k" + _version,
                            n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent),
                        },
                    },
                });
            }

            return Json(new { issuer = Issuer, token_endpoint = Issuer + "/token", jwks_uri = Issuer + "/certs" });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _key.Dispose();
            }

            base.Dispose(disposing);
        }

        private static Task<HttpResponseMessage> Json(object body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
    }
}
