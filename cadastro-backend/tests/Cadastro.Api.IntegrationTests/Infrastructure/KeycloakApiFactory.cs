using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.Keycloak;

namespace Cadastro.Api.IntegrationTests.Infrastructure;

/// <summary>
/// API em memória falando com um Keycloak real (Testcontainers) e o realm mínimo de teste: login OIDC de verdade.
/// </summary>
public sealed class KeycloakApiFactory : CadastroApiFactory, IAsyncLifetime
{
    // Mesma versão do Keycloak do workspace (../keycloak/Dockerfile), fixada por digest (OWASP A03).
    private const string KeycloakImage =
        "quay.io/keycloak/keycloak:26.8.2@sha256:723b1d6e254c0fc96d5cffec860c7e0744d6a3277822b865bb538cf891575e41";

    private readonly KeycloakContainer _keycloak = new KeycloakBuilder(KeycloakImage)
        .WithRealm(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "workspace-realm.json"))
        .Build();

    /// <summary>Endereço base do Keycloak no host (porta mapeada).</summary>
    public string KeycloakUrl => _keycloak.GetBaseAddress().TrimEnd('/');

    /// <inheritdoc />
    protected override string KeycloakBaseAddress => KeycloakUrl;

    /// <summary>Com o Keycloak de verdade, a sonda de readiness também é real.</summary>
    protected override void ConfigureKeycloakProbe(IServiceCollection services)
    {
    }

    /// <summary>Sobe o Keycloak antes do primeiro teste da coleção.</summary>
    public async ValueTask InitializeAsync() => await _keycloak.StartAsync(TestContext.Current.CancellationToken);

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _keycloak.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Host separado (mesmo Keycloak) com relógio controlável, para simular o vencimento do access token sem
    /// afetar a validação de tokens dos outros testes.
    /// </summary>
    public WebApplicationFactory<Program> WithClock(FakeTimeProvider clock) =>
        WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock))));
}

/// <summary>Testes que sobem o Keycloak compartilham um único container (subir o Keycloak é caro).</summary>
[CollectionDefinition(Name)]
public sealed class KeycloakTestGroup : ICollectionFixture<KeycloakApiFactory>
{
    public const string Name = "keycloak";
}
