using System.Net;
using System.Net.Http.Json;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Cadastro.Application.Users;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Cadastro.Api.IntegrationTests.Users;

/// <summary><c>GET /api/v1/users/me</c> contra o Keycloak real (US-06).</summary>
[Collection(KeycloakTestGroup.Name)]
public sealed class MeEndpointTests(KeycloakApiFactory factory)
{
    private const string Password = "Test-Only-Password-1!";
    private static readonly Uri MeUri = new("/api/v1/users/me", UriKind.Relative);

    [Fact]
    public async Task Me_ReturnsTheProfileFromKeycloakUserProfile()
    {
        using var browser = new BrowserSession(factory);
        using var _ = await browser.SignInAsync("usuario.teste", Password);

        using var response = await browser.Bff.GetAsync(MeUri, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var profile = await response.Content.ReadFromJsonAsync<MyProfile>(TestContext.Current.CancellationToken);
        profile!.UserName.ShouldBe("usuario.teste");
        profile.FirstName.ShouldBe("Usuario");
        profile.MobilePhone.ShouldBe("+5511900000000");
        profile.Address.ShouldBe(new ProfileAddress("01001000", "Praça da Sé", "1", null, "Sé", "São Paulo", "SP"));
    }

    [Fact]
    public async Task Me_WhenUserHasNoAddress_ReturnsProfileWithoutAddress()
    {
        using var browser = new BrowserSession(factory);
        using var _ = await browser.SignInAsync("admin.teste", Password);

        var profile = await browser.Bff.GetFromJsonAsync<MyProfile>(MeUri, TestContext.Current.CancellationToken);

        profile!.UserName.ShouldBe("admin.teste");
        profile.Address.ShouldBeNull();
        profile.MobilePhone.ShouldBeNull();
    }

    [Fact]
    public async Task Me_WhenAnonymous_Returns401()
    {
        using var browser = new BrowserSession(factory);

        using var response = await browser.Bff.GetAsync(MeUri, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_WhenKeycloakAdminApiIsDown_Returns503ProblemDetails()
    {
        var users = Substitute.For<IIdentityUserReadService>();
        users.FindProfileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<MyProfile?>(_ => throw new IdentityProviderUnavailableException("simulated"));
        await using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton(users))));
        using var browser = new BrowserSession(host);
        using var _ = await browser.SignInAsync("usuario.teste", Password);

        using var response = await browser.Bff.GetAsync(MeUri, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("identity.unavailable");
    }

    [Fact]
    public async Task Ready_ChecksTheRealKeycloak()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("\"keycloak\"");
    }
}
