using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Cadastro.Api.Health;

/// <summary>Limites do check de processo (configuráveis em <c>HealthChecks:Process</c>).</summary>
public sealed class ProcessHealthOptions
{
    /// <summary>Seção de configuração (<c>HealthChecks:Process</c>).</summary>
    public const string SectionName = "HealthChecks:Process";

    /// <summary>Acima deste uso de memória gerenciada o serviço é considerado degradado.</summary>
    public long MaxAllocatedMegabytes { get; init; } = 512;
}

/// <summary>Expõe métricas do processo e degrada quando o consumo de memória passa do limite.</summary>
internal sealed class ProcessHealthCheck(IOptions<ProcessHealthOptions> options, TimeProvider timeProvider) : IHealthCheck
{
    private const long BytesPerMegabyte = 1024 * 1024;

    /// <summary>Coleta memória, GC, threads e uptime; degrada se a memória alocada passar do limite.</summary>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var process = Process.GetCurrentProcess();
        var allocatedMegabytes = GC.GetTotalMemory(forceFullCollection: false) / BytesPerMegabyte;
        var uptime = timeProvider.GetUtcNow() - new DateTimeOffset(process.StartTime.ToUniversalTime());

        var data = new Dictionary<string, object>
        {
            ["allocated_mb"] = allocatedMegabytes,
            ["working_set_mb"] = process.WorkingSet64 / BytesPerMegabyte,
            ["gc_gen0_collections"] = GC.CollectionCount(0),
            ["gc_gen2_collections"] = GC.CollectionCount(2),
            ["thread_count"] = process.Threads.Count,
            ["uptime_seconds"] = (long)uptime.TotalSeconds,
            ["version"] = typeof(ProcessHealthCheck).Assembly.GetName().Version?.ToString() ?? "unknown",
        };

        var limit = options.Value.MaxAllocatedMegabytes;
        var result = allocatedMegabytes > limit
            ? HealthCheckResult.Degraded($"Allocated memory above {limit} MB.", data: data)
            : HealthCheckResult.Healthy("Process metrics within limits.", data);
        return Task.FromResult(result);
    }
}
