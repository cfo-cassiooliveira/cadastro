using Cadastro.Application.UserImports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.UserImports;

/// <summary>
/// Executa a limpeza por retenção em segundo plano, a cada <see cref="ImportRetentionOptions.IntervalMinutes"/> minutos. Cada
/// ciclo usa um escopo próprio (o <c>DbContext</c> é scoped). Falha de um ciclo é logada uma vez e o serviço tenta no seguinte.
/// </summary>
internal sealed partial class ImportRetentionService(
    IServiceScopeFactory scopeFactory, IOptions<ImportRetentionOptions> options, ILogger<ImportRetentionService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(settings.InitialDelaySeconds), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.IntervalMinutes));
            do
            {
                await RunCycleAsync(settings.BatchSize, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada normal do host.
        }
    }

    private async Task RunCycleAsync(int batchSize, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IPurgeExpiredImportDataHandler>().HandleAsync(batchSize, cancellationToken);
            if (result.RemovedAnything)
            {
                LogPurged(result.Batches, result.Lines, result.AuditEntries);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Banco fora ou erro inesperado: loga uma vez e tenta de novo no próximo ciclo; o job nunca derruba o processo.
            LogCycleFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Import retention removed {Batches} batches, {Lines} lines and {AuditEntries} audit entries")]
    private partial void LogPurged(int batches, int lines, int auditEntries);

    [LoggerMessage(Level = LogLevel.Error, Message = "Import retention cycle failed; will retry on the next interval")]
    private partial void LogCycleFailed(Exception exception);
}
