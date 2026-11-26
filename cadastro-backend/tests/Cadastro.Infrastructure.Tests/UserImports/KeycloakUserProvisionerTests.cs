using System.Net;
using System.Text;
using Cadastro.Application.UserImports;
using Cadastro.Application.Users;
using Cadastro.Infrastructure.Keycloak;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Cadastro.Infrastructure.Tests.UserImports;

/// <summary>Provisionador do Keycloak sem rede: contrato das chamadas, idempotência e classificação das falhas.</summary>
public sealed class KeycloakUserProvisionerTests
{
    private static readonly Guid LineId = Guid.Parse("0195f1d2-7c3a-7b11-9a00-000000000001");
    private static readonly ProvisionUserRequest Request = new(LineId, "Ana Souza", "Ana@Example.com", "(11) 99999-0000");

    private sealed record Call(HttpMethod Method, string PathAndQuery, string Body);

    private sealed class ScriptedHandler(Func<Call, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = new Call(request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            Calls.Add(call);
            return call.PathAndQuery.Contains("openid-connect/token", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"t\",\"expires_in\":300}", Encoding.UTF8, "application/json") }
                : respond(call);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (KeycloakUserProvisioner Provisioner, ScriptedHandler Handler) Build(Func<Call, HttpResponseMessage> respond, CreationRateLimiter? limiter = null)
    {
        var handler = new ScriptedHandler(respond);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://keycloak.test/") });
        var admin = Options.Create(new KeycloakAdminOptions { BaseUrl = new Uri("http://keycloak.test/"), Realm = "workspace", ClientId = "cadastro", ClientSecret = "x" });
        var tokens = new KeycloakServiceTokenProvider(factory, () => new KeycloakServiceTokenProvider.ServiceCredentials("workspace", "cadastro-importer", "secret"), TimeProvider.System);
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://keycloak.test/") };
        return (new KeycloakUserProvisioner(http, tokens, admin, limiter ?? new CreationRateLimiter(1000, TimeProvider.System)), handler);
    }

    private static HttpResponseMessage Created(string id)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created);
        response.Headers.Location = new Uri($"http://keycloak.test/admin/realms/workspace/users/{id}");
        return response;
    }

    [Fact]
    public async Task Provision_NewPerson_CreatesWithoutPasswordAndSendsActivationEmailWith72HourLifespan()
    {
        var (provisioner, handler) = Build(call => call switch
        {
            { Method.Method: "GET" } => Json(HttpStatusCode.OK, "[]"),
            { Method.Method: "POST" } => Created("kc-1"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        });

        var user = await provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken);

        user.ShouldBe(new ProvisionedUser("kc-1", ProvisionOutcome.Created));
        var create = handler.Calls.Single(c => c.Method == HttpMethod.Post && c.PathAndQuery.EndsWith("/users", StringComparison.Ordinal));
        create.Body.ShouldContain("\"requiredActions\":[\"UPDATE_PASSWORD\"]");
        create.Body.ShouldContain($"\"importLineId\":[\"{LineId:D}\"]");
        using var created = System.Text.Json.JsonDocument.Parse(create.Body);
        created.RootElement.GetProperty("attributes").GetProperty("mobilePhone")[0].GetString().ShouldBe("+5511999990000");
        create.Body.ShouldNotContain("credentials");
        create.Body.ShouldNotContain("52998224725");
        var email = handler.Calls.Single(c => c.Method == HttpMethod.Put);
        email.PathAndQuery.ShouldBe("/admin/realms/workspace/users/kc-1/execute-actions-email?lifespan=259200");
        email.Body.ShouldBe("[\"UPDATE_PASSWORD\"]");
    }

    [Fact]
    public async Task Provision_WhenUserOfThisLineExists_ResumesWithoutCreatingAndResendsTheEmail()
    {
        var (provisioner, handler) = Build(call => call switch
        {
            { Method.Method: "GET" } when call.PathAndQuery.Contains("importLineId", StringComparison.Ordinal) => Json(HttpStatusCode.OK, "[{\"id\":\"kc-9\",\"requiredActions\":[\"UPDATE_PASSWORD\"]}]"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        });

        var user = await provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken);

        user.ShouldBe(new ProvisionedUser("kc-9", ProvisionOutcome.Created));
        handler.Calls.ShouldNotContain(c => c.Method == HttpMethod.Post && c.PathAndQuery.EndsWith("/users", StringComparison.Ordinal));
        handler.Calls.ShouldContain(c => c.Method == HttpMethod.Put && c.PathAndQuery.Contains("lifespan=259200", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Provision_WhenAnotherUserHasTheEmail_IsAlreadyExistsWithoutCreatingOrEmailing()
    {
        var (provisioner, handler) = Build(call =>
            call.PathAndQuery.Contains("importLineId", StringComparison.Ordinal) ? Json(HttpStatusCode.OK, "[]") : Json(HttpStatusCode.OK, "[{\"id\":\"kc-5\"}]"));

        var user = await provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken);

        user.ShouldBe(new ProvisionedUser("kc-5", ProvisionOutcome.AlreadyExists));
        handler.Calls.ShouldNotContain(c => c.Method != HttpMethod.Get && !c.PathAndQuery.Contains("openid-connect", StringComparison.Ordinal));
        handler.Calls.Single(c => c.PathAndQuery.Contains("email=ana%40example.com&exact=true", StringComparison.Ordinal)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Provision_Conflict_ForSomeoneElse_IsPermanentRejection()
    {
        var (provisioner, _) = Build(call => call.Method == HttpMethod.Post ? new HttpResponseMessage(HttpStatusCode.Conflict) : Json(HttpStatusCode.OK, "[]"));

        var exception = await Should.ThrowAsync<IdentityUserRejectedException>(() => provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken));

        exception.Code.ShouldBe(ImportFailureCodes.IdentityRejected);
    }

    [Fact]
    public async Task Provision_Conflict_ForThisLine_ResumesAfterTheRetriedPost()
    {
        var searches = 0;
        var (provisioner, _) = Build(call => call switch
        {
            { Method.Method: "POST" } => new HttpResponseMessage(HttpStatusCode.Conflict),
            { Method.Method: "GET" } when call.PathAndQuery.Contains("importLineId", StringComparison.Ordinal) => Json(HttpStatusCode.OK, ++searches == 1 ? "[]" : "[{\"id\":\"kc-7\"}]"),
            { Method.Method: "GET" } => Json(HttpStatusCode.OK, "[]"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        });

        var user = await provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken);

        user.UserId.ShouldBe("kc-7");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Provision_ValidationErrorOnCreate_IsPermanentRejection(HttpStatusCode status)
    {
        var (provisioner, _) = Build(call => call.Method == HttpMethod.Post ? new HttpResponseMessage(status) : Json(HttpStatusCode.OK, "[]"));

        await Should.ThrowAsync<IdentityUserRejectedException>(() => provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Provision_OperationalErrors_AreTransient(HttpStatusCode status)
    {
        var (provisioner, _) = Build(call => call.Method == HttpMethod.Post ? new HttpResponseMessage(status) : Json(HttpStatusCode.OK, "[]"));

        await Should.ThrowAsync<IdentityProviderUnavailableException>(() => provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Provision_WhenActivationEmailFails_IsTransientSoTheRetryResendsIt()
    {
        var (provisioner, _) = Build(call => call.Method.Method switch
        {
            "GET" => Json(HttpStatusCode.OK, "[]"),
            "POST" => Created("kc-3"),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        });

        await Should.ThrowAsync<IdentityProviderUnavailableException>(() => provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Provision_WhenNetworkFails_IsTransient()
    {
        var (provisioner, _) = Build(_ => throw new HttpRequestException("connection refused"));

        await Should.ThrowAsync<IdentityProviderUnavailableException>(() => provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken));
    }

    private static bool IsTokenCall(Call call) => call.PathAndQuery.Contains("openid-connect/token", StringComparison.Ordinal);

    [Fact]
    public async Task Provision_Unauthorized_DiscardsTheCachedTokenAndRetriesOnce()
    {
        var adminCalls = 0;
        var (provisioner, handler) = Build(call => call switch
        {
            { Method.Method: "GET" } when ++adminCalls == 1 => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            { Method.Method: "GET" } => Json(HttpStatusCode.OK, "[]"),
            { Method.Method: "POST" } => Created("kc-2"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        });

        var user = await provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken);

        user.ShouldBe(new ProvisionedUser("kc-2", ProvisionOutcome.Created));
        handler.Calls.Count(IsTokenCall).ShouldBe(2);
    }

    [Fact]
    public async Task Provision_PersistentUnauthorized_IsAccessDeniedAfterASingleRetry()
    {
        var (provisioner, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Should.ThrowAsync<IdentityAccessDeniedException>(() => provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken));

        handler.Calls.Count(IsTokenCall).ShouldBe(2);
    }

    [Fact]
    public async Task Provision_Forbidden_IsAccessDeniedNotARejectionOfTheLine()
    {
        var (provisioner, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        await Should.ThrowAsync<IdentityAccessDeniedException>(() => provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Provision_EmailRaceOnPost_IsAlreadyExistsNotRejection()
    {
        var emailSearches = 0;
        var (provisioner, _) = Build(call => call switch
        {
            { Method.Method: "POST" } => new HttpResponseMessage(HttpStatusCode.Conflict),
            { Method.Method: "GET" } when call.PathAndQuery.Contains("importLineId", StringComparison.Ordinal) => Json(HttpStatusCode.OK, "[]"),
            { Method.Method: "GET" } => Json(HttpStatusCode.OK, ++emailSearches == 1 ? "[]" : "[{\"id\":\"kc-8\"}]"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        });

        var user = await provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken);

        user.ShouldBe(new ProvisionedUser("kc-8", ProvisionOutcome.AlreadyExists));
    }

    [Theory]
    [InlineData("[\"UPDATE_PASSWORD\"]", true)]
    [InlineData("[]", false)]
    [InlineData("[\"VERIFY_EMAIL\"]", false)]
    public async Task Provision_Resume_ResendsTheEmailOnlyWhileUpdatePasswordIsPending(string requiredActions, bool resends)
    {
        var (provisioner, handler) = Build(call => call switch
        {
            { Method.Method: "GET" } => Json(HttpStatusCode.OK, $"[{{\"id\":\"kc-9\",\"requiredActions\":{requiredActions}}}]"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        });

        var user = await provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken);

        user.ShouldBe(new ProvisionedUser("kc-9", ProvisionOutcome.Created));
        handler.Calls.Any(c => c.Method == HttpMethod.Put).ShouldBe(resends);
    }

    [Fact]
    public async Task Provision_ResendOnResume_GoesThroughTheCreationRateLimiter()
    {
        var clock = new FakeTimeProvider();
        var limiter = new CreationRateLimiter(1, clock);
        await limiter.WaitAsync(TestContext.Current.CancellationToken);
        var (provisioner, handler) = Build(call => call.Method.Method == "GET"
            ? Json(HttpStatusCode.OK, "[{\"id\":\"kc-9\",\"requiredActions\":[\"UPDATE_PASSWORD\"]}]")
            : new HttpResponseMessage(HttpStatusCode.NoContent), limiter);

        var task = provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        task.IsCompleted.ShouldBeFalse();
        handler.Calls.ShouldNotContain(c => c.Method == HttpMethod.Put);
        clock.Advance(TimeSpan.FromSeconds(1));
        await task;
        handler.Calls.ShouldContain(c => c.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Provision_CreatePost_GoesThroughTheCreationRateLimiter()
    {
        var clock = new FakeTimeProvider();
        var limiter = new CreationRateLimiter(1, clock);
        await limiter.WaitAsync(TestContext.Current.CancellationToken);
        var (provisioner, handler) = Build(call => call.Method.Method switch
        {
            "GET" => Json(HttpStatusCode.OK, "[]"),
            "POST" => Created("kc-4"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        }, limiter);

        var task = provisioner.ProvisionAsync(Request, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        task.IsCompleted.ShouldBeFalse();
        handler.Calls.ShouldNotContain(c => c.Method == HttpMethod.Post && c.PathAndQuery.EndsWith("/users", StringComparison.Ordinal));
        clock.Advance(TimeSpan.FromSeconds(1));
        await task;
        handler.Calls.ShouldContain(c => c.Method == HttpMethod.Post && c.PathAndQuery.EndsWith("/users", StringComparison.Ordinal));
    }

    [Fact]
    public void UserNameFor_IsStableNonPersonalAndWithinTheRealmPattern()
    {
        var name = KeycloakUserProvisioner.UserNameFor(LineId);

        name.ShouldBe(KeycloakUserProvisioner.UserNameFor(LineId));
        name.ShouldMatch("^[a-z0-9._-]{4,30}$");
        name.ShouldNotBe(KeycloakUserProvisioner.UserNameFor(Guid.NewGuid()));
    }

    [Theory]
    [InlineData("Ana", "Ana", "")]
    [InlineData("Ana Souza", "Ana", "Souza")]
    [InlineData("  Ana  Maria da Silva ", "Ana", "Maria da Silva")]
    public void SplitName_UsesTheFirstSpace(string full, string first, string last) =>
        KeycloakUserProvisioner.SplitName(full).ShouldBe((first, last));

    [Theory]
    [InlineData("(11) 99999-0000", "+5511999990000")]
    [InlineData("11999990000", "+5511999990000")]
    [InlineData("+55 11 99999-0000", "+5511999990000")]
    [InlineData("1133334444", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeMobilePhone_AcceptsOnlyTheProfileFormat(string? input, string? expected) =>
        KeycloakUserProvisioner.NormalizeMobilePhone(input).ShouldBe(expected);
}
