using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cadastro.Infrastructure.Messaging;

/// <summary>
/// Sonda do broker sem abrir conexão: informa se o publicador está conectado. Dependência não obrigatória (a outbox guarda as
/// mensagens enquanto o broker está fora), então a falha é <c>Degraded</c> e a sonda entra só em <c>/health</c>, nunca no ready.
/// </summary>
internal sealed class RabbitMqHealthCheck(RabbitMqOutboxPublisher publisher) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(publisher.IsDegraded
            ? HealthCheckResult.Degraded("Broker not connected; outbox messages stay pending.")
            : HealthCheckResult.Healthy("Broker reachable or not used yet."));
}
