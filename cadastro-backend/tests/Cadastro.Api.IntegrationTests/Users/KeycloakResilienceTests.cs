using System.Diagnostics;
using Cadastro.Application.Users;
using Cadastro.Infrastructure;
using Cadastro.Infrastructure.Keycloak;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cadastro.Api.IntegrationTests.Users;

/// <summary>
/// Adaptador do Keycloak com o pipeline de resiliência real (o mesmo do <c>AddInfrastructure</c>): timeout por tentativa
/// e total viram "provedor indisponível" (503), nunca erro inesperado (500).
/// </summary>
public sealed class KeycloakResilienceTests
{
    // Regressão: o timeout do Polly (TimeoutRejectedException) escapava do mapeamento e virava 500.
    [Fact]
    public async Task FindProfile_WhenKeycloakHangs_FailsAsUnavailableWithinTheTotalTimeout()
    {
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IIdentityUserReadService>();
        var stopwatch = Stopwatch.StartNew();

        var failure = await Should.ThrowAsync<IdentityProviderUnavailableException>(() =>
            users.FindProfileAsync("user-1", TestContext.Current.CancellationToken));

        failure.InnerException.ShouldBeAssignableTo<Polly.ExecutionRejectedException>();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }

    private static ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeycloakAdmin:BaseUrl"] = "http://keycloak.test/",
            ["KeycloakAdmin:ClientId"] = "cadastro",
            ["KeycloakAdmin:ClientSecret"] = "secret",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddInfrastructure(configuration);
        services.AddHttpClient(KeycloakServiceTokenProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new HangingHandler());
        return services.BuildServiceProvider();
    }

    /// <summary>Keycloak que aceita a conexão e nunca responde (só desiste quando a tentativa é cancelada).</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
