using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Keycloak;

/// <summary>
/// Readiness do provedor de identidade: sem o Keycloak não há login, perfil nem cadastro. Consulta o discovery do
/// realm (leve e anônimo) com timeout curto; a resposta não expõe host nem detalhe da falha.
/// </summary>
internal sealed partial class KeycloakHealthCheck(
    IHttpClientFactory httpClientFactory,
    IOptions<KeycloakAdminOptions> options,
    ILogger<KeycloakHealthCheck> logger) : IHealthCheck
{
    /// <summary>Nome do HttpClient da sonda.</summary>
    public const string HttpClientName = "health-keycloak";

    /// <summary>Healthy com o discovery respondendo 200; Unhealthy caso contrário.</summary>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).GetAsync(
                new Uri($"realms/{options.Value.Realm}/.well-known/openid-configuration", UriKind.Relative), cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("Identity provider reachable.")
                : HealthCheckResult.Unhealthy($"Identity provider answered {(int)response.StatusCode}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            LogProbeFailed(exception);
            return HealthCheckResult.Unhealthy("Identity provider unreachable.");
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Identity provider health probe failed")]
    private partial void LogProbeFailed(Exception exception);
}
