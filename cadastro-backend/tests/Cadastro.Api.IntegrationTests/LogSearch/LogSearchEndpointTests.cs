using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cadastro.Api.Auth;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Cadastro.Api.LogSearch;
using Cadastro.Application.LogSearch;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace Cadastro.Api.IntegrationTests.LogSearch;

/// <summary>
/// <c>POST /api/v1/log-search/queries</c> (ADR-0003 do graylog, item 7) com autenticação fake: 200 com os vetores do
/// plano, 400 sem eco do valor, 401, 403 auditado, 429 por <c>sub</c>, antiforgery e a trilha de auditoria sem o valor.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class LogSearchEndpointTests(CadastroApiFactory factory)
{
    // Chave dos vetores do plan.md §2 da spec 001 do graylog (bytes 00..1f): pública, só para conferir o algoritmo.
    private const string VectorKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string CpfToken = "k1-b81c27bb5f38080e6ca0ed38b828f262";
    private readonly string _investigator = Guid.NewGuid().ToString();
    private static readonly Uri Route = new("/api/v1/log-search/queries", UriKind.Relative);

    [Fact]
    public async Task Query_ForCpf_ReturnsTokenAndQueryOverEveryField()
    {
        await using var host = Host();

        using var response = await PostAsync(host, new { dataType = "cpf", value = "123.456.789-09", caseReference = "INC-1234" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("dataType").GetString().ShouldBe("cpf");
        var token = body.GetProperty("tokens").EnumerateArray().Single();
        token.GetProperty("keyId").GetString().ShouldBe("k1");
        token.GetProperty("token").GetString().ShouldBe(CpfToken);
        body.GetProperty("graylogQuery").GetString().ShouldBe(
            $"CPF_token:(\"{CpfToken}\") OR Cpf_token:(\"{CpfToken}\") OR cpf_token:(\"{CpfToken}\")");
    }

    [Theory]
    [InlineData("cnpj", "12.345.678/0001-90", "k1-f528c9e51a504dbaf6a9f245ba2cea10")]
    [InlineData("phone", "+55 (11) 98765-4321", "k1-412838d1c85d64c0ed202ae3b1546615")]
    [InlineData("login", " Maria.Silva ", "k1-c5b5c3feef6c408ecea5b17d95af9793")]
    [InlineData("email", "Ana.Souza@Gmail.com", "k1-c362504bb56a3e35cc245073244c3da6")]
    public async Task Query_ForEachPlanVector_ReturnsTheExpectedToken(string dataType, string value, string expectedToken)
    {
        await using var host = Host();

        using var response = await PostAsync(host, new { dataType, value, caseReference = "INC-1234" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("tokens").EnumerateArray().Single().GetProperty("token").GetString().ShouldBe(expectedToken);
        var query = body.GetProperty("graylogQuery").GetString()!;
        foreach (var field in LogSearchFields.For(dataType == "email" ? PersonalDataKind.Login : Enum.Parse<PersonalDataKind>(dataType, ignoreCase: true)))
        {
            query.ShouldContain($"{field}_token:(\"{expectedToken}\")");
        }
    }

    [Fact]
    public async Task Query_WithInvalidInput_Returns400WithCodesAndNeverEchoesTheValue()
    {
        const string secret = "98765432100123456";
        await using var host = Host();

        using var response = await PostAsync(host, new { dataType = "cpf", value = secret, caseReference = "x" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldNotContain(secret);
        var errors = JsonDocument.Parse(text).RootElement.GetProperty("errors");
        errors.GetProperty("value")[0].GetString().ShouldBe("log_search.invalid_value");
        errors.GetProperty("caseReference")[0].GetString().ShouldBe("log_search.invalid_case_reference");
        AuditEvents(host).ShouldBeEmpty("pesquisa recusada por validação não gera auditoria de emissão");
    }

    [Fact]
    public async Task Query_WithoutSession_Returns401()
    {
        await using var host = Host();
        using var client = Client(host, null);

        using var response = await client.PostAsJsonAsync(Route, new { dataType = "cpf", value = "12345678909", caseReference = "INC-1" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("user", "otp", 0)]
    [InlineData("log-investigator", "password", 0)]
    [InlineData("log-investigator", null, 0)]
    [InlineData("log-investigator", "otp", 360)]
    public async Task Query_WithoutRoleOrFreshOtp_Returns403AndAuditsTheDenial(string role, string? acr, int ageSeconds)
    {
        await using var host = Host();
        var identity = new TestIdentity(role, acr, ageSeconds == 0 ? 0 : ageSeconds);

        using var response = await PostAsync(host, new { dataType = "cpf", value = "123.456.789-09", caseReference = "INC-1234" }, identity);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var denied = AuditEvents(host).Single();
        Scalar(denied, "audit_event").ShouldBe("log_search_access_denied");
        Scalar(denied, "investigator_id").ShouldBe(_investigator);
        denied.Level.ShouldBe(LogEventLevel.Warning);
        denied.Properties.ShouldNotContainKey("tokens");
        denied.Properties.ShouldNotContainKey("data_type");
        denied.Properties.ShouldNotContainKey("case_reference");
        denied.Properties.ShouldContainKey("trace_id");
        denied.RenderMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldNotContain("123.456.789-09");
    }

    private static readonly Uri AccessRoute = new("/api/v1/log-search/access", UriKind.Relative);

    [Fact]
    public async Task Access_WithRoleAndFreshOtp_Returns204WithoutAudit()
    {
        await using var host = Host();
        using var client = Client(host, new TestIdentity());

        using var response = await client.GetAsync(AccessRoute, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        AuditEvents(host).ShouldBeEmpty();
    }

    [Fact]
    public async Task Access_WithoutRole_Returns403AndAuditsOnlyInvestigatorAndTrace()
    {
        await using var host = Host();
        using var client = Client(host, new TestIdentity(Role: "user"));

        using var response = await client.GetAsync(AccessRoute, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var denied = AuditEvents(host).Single();
        Scalar(denied, "audit_event").ShouldBe("log_search_access_denied");
        Scalar(denied, "investigator_id").ShouldBe(_investigator);
        denied.Properties.ShouldContainKey("trace_id");
        denied.Properties.ShouldNotContainKey("tokens");
        denied.Properties.ShouldNotContainKey("data_type");
        denied.Properties.ShouldNotContainKey("case_reference");
    }

    [Fact]
    public async Task Access_WithoutSession_Returns401()
    {
        await using var host = Host();
        using var client = Client(host, null);

        using var response = await client.GetAsync(AccessRoute, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Query_AboveTheLimit_Returns429PerInvestigator()
    {
        await using var host = Host(builder => builder.UseSetting("RateLimiting:LogSearchPermitsPerHour", "2"));
        var body = new { dataType = "cpf", value = "123.456.789-09", caseReference = "INC-1234" };

        using var first = await PostAsync(host, body);
        using var second = await PostAsync(host, body);
        using var third = await PostAsync(host, body);
        using var otherInvestigator = await PostAsync(host, body, new TestIdentity(Sub: "99999999-aaaa-bbbb-cccc-000000000000"));

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        otherInvestigator.StatusCode.ShouldBe(HttpStatusCode.OK, "o limite é por sub, não global");
    }

    [Fact]
    public async Task Query_WithoutAntiforgeryToken_Returns400()
    {
        await using var host = Host();
        using var client = Client(host, new TestIdentity());

        using var response = await client.PostAsJsonAsync(Route, new { dataType = "cpf", value = "123.456.789-09", caseReference = "INC-1234" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("antiforgery.invalid");
        AuditEvents(host).ShouldBeEmpty();
    }

    [Fact]
    public async Task Query_IssuedIsAuditedWithoutTheValueNorTheMask()
    {
        await using var host = Host(builder => builder.UseSetting("Logging:Pseudonymization:Keys:k2", Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 7)).ToArray())));

        using var response = await PostAsync(host, new { dataType = "cpf", value = "123.456.789-09", caseReference = "INC-1234" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var issued = AuditEvents(host).Single();
        issued.Level.ShouldBe(LogEventLevel.Information);
        Scalar(issued, "audit_event").ShouldBe("log_search_query_issued");
        Scalar(issued, "investigator_id").ShouldBe(_investigator);
        Scalar(issued, "data_type").ShouldBe("cpf");
        Scalar(issued, "case_reference").ShouldBe("INC-1234");
        issued.Properties.ShouldContainKey("trace_id");
        // Texto único separado por vírgula: uma lista viraria tokens_0, tokens_1... no sink GELF.
        issued.Properties["tokens"].ShouldBeOfType<ScalarValue>();
        var tokens = Scalar(issued, "tokens")!.Split(',');
        tokens.Length.ShouldBe(2);
        tokens[0].ShouldBe(CpfToken);
        tokens[1].ShouldStartWith("k2-");
        issued.Properties.Keys.ShouldNotContain(key => key.StartsWith("tokens_", StringComparison.Ordinal));

        // Nenhum evento da requisição carrega o valor (nem formatado) nem a máscara. Só os eventos desta requisição
        // (mesmo trace_id): o sink é compartilhado e testes paralelos de client-logs emitem a máscara legitimamente.
        var traceId = Scalar(issued, "trace_id");
        foreach (var logEvent in Sink(host).Events.Where(e => e.Properties.TryGetValue("trace_id", out var t) && t is ScalarValue { Value: var v } && Equals(v?.ToString(), traceId)))
        {
            var everything = logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture) + string.Concat(logEvent.Properties.Select(p => p.Value.ToString()));
            everything.ShouldNotContain("123.456.789-09");
            everything.ShouldNotContain("12345678909");
            everything.ShouldNotContain("***.456.789-**");
        }
    }

    [Fact]
    public async Task Login_WithUnsupportedAcrValues_Returns400()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

        using var response = await client.GetAsync(new Uri("/bff/login?acr_values=password", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("acr_values.invalid");
    }

    [Fact]
    public void AuditFieldNames_AreNotPseudonymizedFields()
    {
        foreach (var name in LogSearchAudit.FieldNames)
        {
            PersonalDataFields.ByName.ShouldNotContainKey(name);
        }
    }

    private static WebApplicationFactory<Program> HostOf(CadastroApiFactory factory, Action<IWebHostBuilder>? extra) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Logging:Pseudonymization:Keys:k1", VectorKey);
            extra?.Invoke(builder);
            builder.ConfigureTestServices(services => services
                .AddAuthentication(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
        });

    private WebApplicationFactory<Program> Host(Action<IWebHostBuilder>? extra = null) => HostOf(factory, extra);

    private static CollectingLogSink Sink(WebApplicationFactory<Program> host) =>
        host.Services.GetServices<ILogEventSink>().OfType<CollectingLogSink>().Single();

    private IReadOnlyList<LogEvent> AuditEvents(WebApplicationFactory<Program> host) =>
        [.. Sink(host).Events.Where(e => e.Properties.ContainsKey("audit_event") && e.Properties.ContainsKey("investigator_id") && Scalar(e, "investigator_id") == _investigator)];

    private static string? Scalar(LogEvent logEvent, string name) => CollectingLogSink.Scalar(logEvent.Properties[name]);

    private HttpClient Client(WebApplicationFactory<Program> host, TestIdentity? identity)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });
        if (identity is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.SubHeader, identity.Sub ?? _investigator);
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, identity.Role);
            if (identity.Acr is not null)
            {
                client.DefaultRequestHeaders.Add(TestAuthHandler.AcrHeader, identity.Acr);
            }

            var authTime = DateTimeOffset.UtcNow.AddSeconds(-identity.AuthAgeSeconds).ToUnixTimeSeconds();
            client.DefaultRequestHeaders.Add(TestAuthHandler.AuthTimeHeader, authTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return client;
    }

    private async Task<HttpResponseMessage> PostAsync(WebApplicationFactory<Program> host, object body, TestIdentity? identity = null)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = Client(host, identity ?? new TestIdentity());
        var session = await client.GetFromJsonAsync<BffUserResponse>(new Uri("/bff/user", UriKind.Relative), cancellation);
        using var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = JsonContent.Create(body) };
        request.Headers.Add(BffAuthenticationSetup.AntiforgeryHeaderName, session!.AntiforgeryToken);
        return await client.SendAsync(request, cancellation);
    }

    /// <summary>Investigador fake: por padrão com a role e o OTP digitado agora.</summary>
    private sealed record TestIdentity(
        string Role = "log-investigator",
        string? Acr = "otp",
        int AuthAgeSeconds = 10,
        string? Sub = null);
}
