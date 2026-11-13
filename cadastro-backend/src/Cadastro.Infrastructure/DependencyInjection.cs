using Cadastro.Application.Users;
using Cadastro.Infrastructure.Keycloak;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure;

/// <summary>Registro dos adaptadores de infraestrutura (Keycloak).</summary>
public static class DependencyInjection
{
    // Uma tentativa lenta não pode segurar a requisição do usuário: 3 s por tentativa, 10 s no total (Art. VII.1).
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Registra o acesso à Admin API do Keycloak com timeout, retry de falhas transitórias e circuit breaker.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<KeycloakAdminOptions>()
            .Bind(configuration.GetSection(KeycloakAdminOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient(KeycloakServiceTokenProvider.HttpClientName, ConfigureBaseAddress)
            .AddStandardResilienceHandler(ConfigureResilience);
        // O provedor guarda o token em cache: uma instância para a aplicação inteira.
        services.AddSingleton<KeycloakServiceTokenProvider>();
        services.AddHttpClient<IIdentityUserReadService, KeycloakAdminClient>(ConfigureBaseAddress)
            .AddStandardResilienceHandler(ConfigureResilience);
        return services;
    }

    /// <summary>
    /// Readiness do Keycloak (discovery do realm, timeout de 3 s), com as tags que o composition root define
    /// (a Api não conhece a sonda, só esta extensão).
    /// </summary>
    public static IHealthChecksBuilder AddKeycloakHealthCheck(this IHealthChecksBuilder builder, params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHttpClient(KeycloakHealthCheck.HttpClientName, (services, client) =>
        {
            ConfigureBaseAddress(services, client);
            client.Timeout = AttemptTimeout;
        });
        return builder.AddCheck<KeycloakHealthCheck>("keycloak", HealthStatus.Unhealthy, tags);
    }

    private static void ConfigureBaseAddress(IServiceProvider services, HttpClient client) =>
        client.BaseAddress = services.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value.BaseUrl;

    private static void ConfigureResilience(HttpStandardResilienceOptions options)
    {
        options.AttemptTimeout.Timeout = AttemptTimeout;
        options.TotalRequestTimeout.Timeout = TotalTimeout;
        options.CircuitBreaker.SamplingDuration = AttemptTimeout * 2 + TimeSpan.FromSeconds(1);
    }
}
