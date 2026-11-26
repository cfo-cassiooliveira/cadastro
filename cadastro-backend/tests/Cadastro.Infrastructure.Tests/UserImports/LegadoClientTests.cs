using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Cadastro.Application.UserImports;
using Cadastro.Infrastructure.Legado;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cadastro.Infrastructure.Tests.UserImports;

/// <summary>
/// Cliente da API interna do Legado contra um Kestrel HTTPS em processo: TLS validado só pela CA informada, assinatura HMAC no
/// formato do ADR-0005 do Legado (nonce novo a cada tentativa) e tradução de falhas.
/// </summary>
public sealed partial class LegadoClientTests : IAsyncLifetime
{
    private static readonly byte[] SigningKey = RandomNumberGenerator.GetBytes(32);
    private readonly List<string> _nonces = [];
    private readonly List<(string Method, string Path, string Body)> _requests = [];
    private readonly string _caFile = Path.Combine(Path.GetTempPath(), $"legado-ca-{Guid.NewGuid():N}.pem");
    private WebApplication _server = null!;
    private ServiceProvider _client = null!;
    private int _lookupFailuresLeft;
    private Func<string, (HttpStatusCode Status, string? Json)> _lookup = _ => (HttpStatusCode.OK, "{\"outcome\":\"not_found\"}");
    private HttpStatusCode _markStatus = HttpStatusCode.NoContent;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=Legado Test CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        await File.WriteAllTextAsync(_caFile, ca.ExportCertificatePem(), TestContext.Current.CancellationToken);

        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        serverRequest.CertificateExtensions.Add(san.Build());
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var issued = serverRequest.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), RandomNumberGenerator.GetBytes(8));
        // O PFX evita o problema de chave efêmera do SChannel no Windows.
        using var withKey = issued.CopyWithPrivateKey(serverKey);
        var serverCertificate = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = HttpProtocols.Http1;
            listen.UseHttps(serverCertificate);
        }));
        _server = builder.Build();
        _server.Map("/{**path}", HandleAsync);
        await _server.StartAsync(TestContext.Current.CancellationToken);
        var port = new Uri(_server.Urls.First()).Port;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ImportConsumer:Enabled"] = "true",
            ["Legado:BaseUrl"] = $"https://localhost:{port}/",
            ["Legado:KeyId"] = "cadastro-1",
            ["Legado:SigningKey"] = Convert.ToBase64String(SigningKey),
            ["Legado:CaCertificatePath"] = _caFile,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddImportConsumer(configuration);
        _client = services.BuildServiceProvider();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        File.Delete(_caFile);
    }

    private ILegacyUserDirectory Directory => _client.GetRequiredService<ILegacyUserDirectory>();

    private async Task HandleAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var bytes = Encoding.UTF8.GetBytes(body);
        var pathAndQuery = context.Request.Path + context.Request.QueryString.Value;
        var header = context.Request.Headers[LegadoSignature.HeaderName].ToString();
        var match = HeaderPattern().Match(header);
        if (!match.Success)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var expected = LegadoSignature.Header(
            match.Groups["k"].Value, SigningKey, long.Parse(match.Groups["t"].Value, System.Globalization.CultureInfo.InvariantCulture), match.Groups["n"].Value, context.Request.Method, pathAndQuery, bytes);
        var fresh = false;
        lock (_nonces)
        {
            fresh = !_nonces.Contains(match.Groups["n"].Value);
            _nonces.Add(match.Groups["n"].Value);
            _requests.Add((context.Request.Method, pathAndQuery, body));
        }

        var inWindow = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(match.Groups["t"].Value, System.Globalization.CultureInfo.InvariantCulture)) <= 60;
        if (expected != header || !fresh || !inWindow)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (context.Request.Method == "POST" && pathAndQuery == "/api/v1/internal/user-lookups")
        {
            if (Interlocked.Decrement(ref _lookupFailuresLeft) >= 0)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            var (status, json) = _lookup(body);
            context.Response.StatusCode = (int)status;
            if (json is not null)
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(json);
            }

            return;
        }

        context.Response.StatusCode = context.Request.Method == "PUT" && pathAndQuery.EndsWith("/migration", StringComparison.Ordinal)
            ? (int)_markStatus
            : StatusCodes.Status404NotFound;
    }

    [Fact]
    public async Task Lookup_Found_ReturnsTheCandidateAndSendsTheEmailInTheBodyNotTheUrl()
    {
        var legacyId = Guid.NewGuid();
        _lookup = _ => (HttpStatusCode.OK, $"{{\"outcome\":\"found\",\"user\":{{\"id\":\"{legacyId}\",\"userName\":\"ana\",\"email\":\"ana@example.com\"}}}}");

        var candidates = await Directory.FindByEmailAsync(" ana@example.com ", TestContext.Current.CancellationToken);

        candidates.ShouldBe([new LegacyUserCandidate(legacyId, "ana@example.com")]);
        var request = _requests.Single();
        request.Path.ShouldBe("/api/v1/internal/user-lookups");
        request.Path.ShouldNotContain("ana");
        request.Body.ShouldBe("{\"login\":\"ana@example.com\"}");
    }

    [Theory]
    [InlineData("not_found")]
    [InlineData("migrated")]
    public async Task Lookup_NotFoundOrMigrated_ReturnsNoCandidates(string outcome)
    {
        _lookup = _ => (HttpStatusCode.OK, $"{{\"outcome\":\"{outcome}\"}}");

        (await Directory.FindByEmailAsync("nobody@example.com", TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Lookup_AfterATransientFailure_RetriesWithANewNonceAndSucceeds()
    {
        _lookupFailuresLeft = 1;
        _lookup = _ => (HttpStatusCode.OK, "{\"outcome\":\"not_found\"}");

        (await Directory.FindByEmailAsync("a@example.com", TestContext.Current.CancellationToken)).ShouldBeEmpty();

        // O servidor recusa nonce repetido: se o retry reusasse a assinatura, a segunda tentativa daria 401.
        _nonces.Count.ShouldBe(2);
        _nonces.Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Mark_SendsTheKeycloakIdInTheBodyAndAcceptsNoContent()
    {
        var legacyId = Guid.NewGuid();
        var keycloakId = Guid.NewGuid();

        await Directory.MarkMigratedAsync(legacyId, keycloakId.ToString("D"), TestContext.Current.CancellationToken);

        var request = _requests.Single();
        (request.Method, request.Path).ShouldBe(("PUT", $"/api/v1/internal/users/{legacyId:D}/migration"));
        request.Body.ShouldBe($"{{\"keycloakUserId\":\"{keycloakId:D}\"}}");
    }

    [Fact]
    public async Task Mark_WhenLegadoAnswersConflict_ThrowsUnavailable()
    {
        _markStatus = HttpStatusCode.Conflict;

        await Should.ThrowAsync<LegacyDirectoryUnavailableException>(
            () => Directory.MarkMigratedAsync(Guid.NewGuid(), Guid.NewGuid().ToString("D"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Mark_WithANonGuidKeycloakId_ThrowsUnavailable() =>
        await Should.ThrowAsync<LegacyDirectoryUnavailableException>(
            () => Directory.MarkMigratedAsync(Guid.NewGuid(), "not-a-guid", TestContext.Current.CancellationToken));

    [Fact]
    public async Task Lookup_WhenLegadoKeepsFailing_ThrowsUnavailableAfterTheResilienceBudget()
    {
        _lookupFailuresLeft = int.MaxValue;

        await Should.ThrowAsync<LegacyDirectoryUnavailableException>(() => Directory.FindByEmailAsync("a@example.com", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Signature_MatchesTheLegadoReferenceVectors()
    {
        // Vetores de Internal/hmac-test-vectors.json do Legado (oráculo independente: openssl).
        var key = Convert.FromBase64String("MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=");

        LegadoSignature.Header(
            "k", key, 1763035200, "AAECAwQFBgcICQoLDA0ODw", "POST", "/api/v1/internal/credential-verifications",
            Encoding.UTF8.GetBytes("{\"login\":\"ana.souza\",\"password\":\"Senha@123\"}"))
            .ShouldEndWith("v1=WKg1BEo1aG4nOL86xzCTZwhl1g0TS2EcD6+SfaKydGU=");
        LegadoSignature.Header(
            "k", key, 1763035200, "EBESExQVFhcYGRobHB0eHw", "PUT", "/api/v1/internal/users/0195f1d2-7c3a-7b11-9a00-000000000001/migration",
            Encoding.UTF8.GetBytes("{\"keycloakUserId\":\"7d3b5f0e-1a2b-4c3d-8e9f-000000000002\"}"))
            .ShouldEndWith("v1=9MljcDCTair8GUaxERbDN8vP5XtKqexQCmo5gQY9n6Y=");
        LegadoSignature.Header("k", key, 1763035200, "ICEiIyQlJicoKSorLC0uLw", "GET", "/api/v1/internal/ping?x=1", [])
            .ShouldEndWith("v1=Iao9mDTHqgUsq1RhM325GvKZoEGjjYwJxmHixge8thM=");
    }

    [Fact]
    public void Tls_AcceptsOnlyCertificatesChainedToTheLegadoCa()
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(5));
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherRequest = new CertificateRequest("CN=Other CA", otherKey, HashAlgorithmName.SHA256);
        otherRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var otherCa = otherRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(5));
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leaf = new CertificateRequest("CN=legado-api", leafKey, HashAlgorithmName.SHA256)
            .Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2), RandomNumberGenerator.GetBytes(8));

        LegadoTlsTrust.IsTrusted(leaf, SslPolicyErrors.RemoteCertificateChainErrors, ca).ShouldBeTrue();
        LegadoTlsTrust.IsTrusted(leaf, SslPolicyErrors.RemoteCertificateChainErrors, otherCa).ShouldBeFalse();
        LegadoTlsTrust.IsTrusted(leaf, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, ca).ShouldBeFalse();
        LegadoTlsTrust.IsTrusted(null, SslPolicyErrors.RemoteCertificateNotAvailable, ca).ShouldBeFalse();
    }

    [GeneratedRegex(@"^keyId=(?<k>[^,]+), t=(?<t>\d+), n=(?<n>[A-Za-z0-9_-]+), v1=.+$")]
    private static partial Regex HeaderPattern();
}
