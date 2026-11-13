using System.Net;
using Cadastro.Infrastructure.Keycloak;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Cadastro.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Host da API em memória para os testes que não falam com o Keycloak (o discovery OIDC só é lido no primeiro login;
/// a sonda de readiness do Keycloak responde 200 por um handler fake). Logs vão para o <see cref="FakeLogCollector"/>.
/// </summary>
public class CadastroApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Segredo usado só nos testes (o realm de teste tem o mesmo valor).</summary>
    public const string TestClientSecret = "test-only-client-secret";

    /// <summary>Endereço do Keycloak que a API usa; sem container, um endereço que nunca é chamado.</summary>
    protected virtual string KeycloakBaseAddress => "http://127.0.0.1:9";

    /// <summary>Logs emitidos pela API durante os testes.</summary>
    public FakeLogCollector Logs => Services.GetRequiredService<FakeLogCollector>();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var keycloak = KeycloakBaseAddress.TrimEnd('/');
        builder.UseSetting("Oidc:Authority", $"{keycloak}/realms/workspace");
        builder.UseSetting("Oidc:MetadataAddress", string.Empty);
        builder.UseSetting("Oidc:ClientId", "cadastro");
        builder.UseSetting("Oidc:ClientSecret", TestClientSecret);
        builder.UseSetting("Oidc:PublicOrigin", "http://localhost:4300");
        builder.UseSetting("Oidc:RequireHttpsMetadata", "false");
        builder.UseSetting("KeycloakAdmin:BaseUrl", $"{keycloak}/");
        builder.UseSetting("KeycloakAdmin:ClientId", "cadastro");
        builder.UseSetting("KeycloakAdmin:ClientSecret", TestClientSecret);
        builder.ConfigureLogging(logging => logging.AddFakeLogging());
        builder.ConfigureTestServices(ConfigureKeycloakProbe);
    }

    /// <summary>Sem Keycloak de verdade, a sonda de readiness responde 200.</summary>
    protected virtual void ConfigureKeycloakProbe(IServiceCollection services) =>
        services.AddHttpClient(KeycloakHealthCheck.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StaticResponseHandler(HttpStatusCode.OK));
}

/// <summary>Compartilha um único host da API entre as classes de teste sem Keycloak.</summary>
[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<CadastroApiFactory>
{
    public const string Name = "api";
}
