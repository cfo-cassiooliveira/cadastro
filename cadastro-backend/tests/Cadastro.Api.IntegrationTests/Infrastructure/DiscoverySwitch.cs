using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Cadastro.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Envolve o discovery OIDC do BFF para os testes simularem o Keycloak saindo do ar depois do login
/// (o container continua de pé para os outros testes).
/// </summary>
public sealed class DiscoverySwitch
{
    /// <summary>Quando ligado, o discovery falha como falharia com o Keycloak fora do ar.</summary>
    public bool IsDown { get; set; }

    /// <summary>Envolve o gerenciador de configuração original.</summary>
    public IConfigurationManager<OpenIdConnectConfiguration> Wrap(IConfigurationManager<OpenIdConnectConfiguration> inner) =>
        new SwitchableConfigurationManager(this, inner);

    private sealed class SwitchableConfigurationManager(
        DiscoverySwitch discovery,
        IConfigurationManager<OpenIdConnectConfiguration> inner) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            discovery.IsDown
                ? throw new InvalidOperationException("IDX20803: Unable to obtain configuration (simulated outage).")
                : inner.GetConfigurationAsync(cancel);

        public void RequestRefresh() => inner.RequestRefresh();
    }
}
