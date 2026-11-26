using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Messaging;

/// <summary>
/// Executa o relay da outbox em segundo plano. Cada ciclo usa um escopo próprio (o <c>DbContext</c> é scoped). Com o
/// broker indisponível o serviço pausa com backoff e jitter em vez de martelar o banco; nada derruba o processo.
/// </summary>
internal sealed partial class OutboxRelayService(
    IServiceScopeFactory scopeFactory, IOptions<RabbitMqOptions> options, ILogger<OutboxRelayService> logger) : BackgroundService
{
    private const int MaxPauseSeconds = 30;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var idle = TimeSpan.FromMilliseconds(settings.RelayIdleDelayMs);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = idle;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var cycle = await scope.ServiceProvider.GetRequiredService<OutboxRelay>().RunOnceAsync(stoppingToken);
                if (cycle.BrokerUnavailable)
                {
                    delay = OutboxBackoff.Next(++failures, idle, TimeSpan.FromSeconds(MaxPauseSeconds), Random.Shared.NextDouble());
                }
                else
                {
                    failures = 0;
                    // Havendo trabalho, o próximo ciclo começa já; sem trabalho, espera o intervalo ocioso.
                    delay = cycle.Selected > 0 ? TimeSpan.Zero : idle;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Falha inesperada (ex.: banco fora): loga uma vez e tenta de novo; o relay nunca morre.
                LogCycleFailed(ex);
                delay = OutboxBackoff.Next(++failures, idle, TimeSpan.FromSeconds(MaxPauseSeconds), Random.Shared.NextDouble());
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox relay cycle failed; retrying with backoff")]
    private partial void LogCycleFailed(Exception exception);
}
