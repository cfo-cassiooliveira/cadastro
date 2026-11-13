using Cadastro.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cadastro.Api.Health;

/// <summary>
/// Health checks padronizados (steering de observabilidade): <c>/health/live</c> (processo),
/// <c>/health/ready</c> (dependências obrigatórias) e <c>/health</c> (relatório detalhado).
/// Anônimos e acessíveis só pela rede interna (o nginx não os publica).
/// </summary>
internal static class HealthChecksSetup
{
    /// <summary>Tag das dependências obrigatórias: se falharem, a instância não deve receber tráfego.</summary>
    public const string ReadyTag = "ready";

    /// <summary>Tag das métricas do processo (memória, threads, uptime).</summary>
    public const string ProcessTag = "process";

    /// <summary>Registra o check do processo; cada dependência obrigatória entra com a tag <see cref="ReadyTag"/>.</summary>
    public static IServiceCollection AddCadastroHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProcessHealthOptions>().Bind(configuration.GetSection(ProcessHealthOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddHealthChecks()
            .AddKeycloakHealthCheck(ReadyTag)
            .AddCheck<ProcessHealthCheck>("process", HealthStatus.Degraded, [ProcessTag]);
        return services;
    }

    /// <summary>Mapeia <c>/health/live</c>, <c>/health/ready</c> e <c>/health</c>.</summary>
    public static IEndpointRouteBuilder MapCadastroHealthChecks(this IEndpointRouteBuilder app)
    {
        // Anônimos de propósito: consumidos pela orquestração/monitoramento; o nginx não os publica.
        // Liveness: só confirma que o processo responde (não consulta dependências, evita reinícios em cascata).
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();

        // Readiness: só dependências obrigatórias. Unhealthy => 503 e a instância sai do balanceamento.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = HealthResponseWriter.WriteAsync,
        }).AllowAnonymous();

        // Relatório completo para monitoramento: dependências e métricas do processo.
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.WriteAsync })
            .AllowAnonymous();

        return app;
    }
}
