using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace Cadastro.Api.Common;

/// <summary>
/// Acrescenta <c>trace_id</c>/<c>span_id</c> (W3C Trace Context, Art. VI.2) e <c>thread_id</c> a todo evento de log,
/// como propriedades comuns, para que apareçam como campos pesquisáveis no Graylog.
/// </summary>
internal sealed class TraceContextEnricher : ILogEventEnricher
{
    /// <summary>Copia o contexto de rastreamento do evento (capturado pelo Serilog) ou da Activity atual para o evento.</summary>
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        var traceId = logEvent.TraceId?.ToHexString() ?? Activity.Current?.TraceId.ToHexString();
        var spanId = logEvent.SpanId?.ToHexString() ?? Activity.Current?.SpanId.ToHexString();

        if (traceId is not null)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("trace_id", traceId));
        }

        if (spanId is not null)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("span_id", spanId));
        }

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("thread_id", Environment.CurrentManagedThreadId));
    }
}
