using System.Net;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Cadastro.Application.Users;
using Cadastro.Infrastructure.Keycloak;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Cadastro.Api.IntegrationTests.Users;

/// <summary>Adaptador da Admin API com o Keycloak simulado: conversão do User Profile e tratamento de falhas.</summary>
public sealed class KeycloakAdminClientTests
{
    private const string TokenJson = """{"access_token":"service-token","expires_in":300}""";

    private static readonly IOptions<KeycloakAdminOptions> Options = Microsoft.Extensions.Options.Options.Create(new KeycloakAdminOptions
    {
        BaseUrl = new Uri("http://keycloak.test/"),
        Realm = "workspace",
        ClientId = "cadastro",
        ClientSecret = "secret",
    });

    [Fact]
    public async Task FindProfile_MapsUserProfileAttributesAndSendsServiceAccountToken()
    {
        var admin = new StaticResponseHandler(HttpStatusCode.OK, """
            {"username":"ana.souza","firstName":"Ana","lastName":"Souza","email":"ana@example.com",
             "attributes":{"mobilePhone":["+5511912345678"],"postalCode":["01001000"],"street":["Rua A"],"addressNumber":["10"],
                           "complement":["Apto 1"],"district":["Centro"],"city":["São Paulo"],"state":["SP"]}}
            """);
        using var tokens = TokenProvider(new StaticResponseHandler(HttpStatusCode.OK, TokenJson));
        var client = AdminClient(admin, tokens);

        var profile = await client.FindProfileAsync("user-1", TestContext.Current.CancellationToken);

        profile.ShouldBe(new MyProfile("ana.souza", "Ana", "Souza", "ana@example.com", "+5511912345678",
            new ProfileAddress("01001000", "Rua A", "10", "Apto 1", "Centro", "São Paulo", "SP")));
        admin.Requests.Single().RequestUri!.AbsolutePath.ShouldBe("/admin/realms/workspace/users/user-1");
        admin.Requests.Single().Headers.Authorization!.Parameter.ShouldBe("service-token");
    }

    [Fact]
    public async Task FindProfile_WhenUserDoesNotExist_ReturnsNull()
    {
        using var tokens = TokenProvider(new StaticResponseHandler(HttpStatusCode.OK, TokenJson));
        var client = AdminClient(new StaticResponseHandler(HttpStatusCode.NotFound), tokens);

        (await client.FindProfileAsync("gone", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FindProfile_WhenAdminApiFails_ThrowsIdentityProviderUnavailable(HttpStatusCode status)
    {
        using var tokens = TokenProvider(new StaticResponseHandler(HttpStatusCode.OK, TokenJson));
        var client = AdminClient(new StaticResponseHandler(status), tokens);

        await Should.ThrowAsync<IdentityProviderUnavailableException>(() =>
            client.FindProfileAsync("user-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindProfile_WhenKeycloakIsUnreachable_ThrowsIdentityProviderUnavailable()
    {
        using var tokens = TokenProvider(new UnreachableHandler());
        var client = AdminClient(new StaticResponseHandler(HttpStatusCode.OK), tokens);

        await Should.ThrowAsync<IdentityProviderUnavailableException>(() =>
            client.FindProfileAsync("user-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetToken_ReusesTheTokenUntilItIsAboutToExpire()
    {
        var tokenEndpoint = new StaticResponseHandler(HttpStatusCode.OK, TokenJson);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var tokens = TokenProvider(tokenEndpoint, clock);

        await tokens.GetTokenAsync(TestContext.Current.CancellationToken);
        await tokens.GetTokenAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(271));
        await tokens.GetTokenAsync(TestContext.Current.CancellationToken);

        tokenEndpoint.Requests.Count.ShouldBe(2);
    }

    private static KeycloakAdminClient AdminClient(HttpMessageHandler handler, KeycloakServiceTokenProvider tokens) =>
        new(new HttpClient(handler) { BaseAddress = Options.Value.BaseUrl }, tokens, Options);

    private static KeycloakServiceTokenProvider TokenProvider(HttpMessageHandler handler, TimeProvider? clock = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(KeycloakServiceTokenProvider.HttpClientName)
            .Returns(_ => new HttpClient(handler, disposeHandler: false) { BaseAddress = Options.Value.BaseUrl });
        return new KeycloakServiceTokenProvider(factory, Options, clock ?? TimeProvider.System);
    }
}
