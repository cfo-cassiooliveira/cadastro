using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Cadastro.Api.IntegrationTests.Infrastructure;

/// <summary>Sink do Serilog que guarda os eventos em memória para os testes inspecionarem as propriedades reais dos logs.</summary>
public sealed class CollectingLogSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    /// <summary>Eventos recebidos até agora.</summary>
    public IReadOnlyList<LogEvent> Events => [.. _events];

    /// <inheritdoc />
    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    /// <summary>Eventos com a propriedade escalar <paramref name="name"/> igual a <paramref name="value"/>.</summary>
    public IReadOnlyList<LogEvent> WithProperty(string name, string value) =>
        [.. _events.Where(e => e.Properties.TryGetValue(name, out var property) && Scalar(property) == value)];

    /// <summary>Valor "cru" de uma propriedade escalar (sem as aspas que o Serilog adiciona ao renderizar strings).</summary>
    public static string? Scalar(LogEventPropertyValue? value) =>
        value is ScalarValue scalar ? Convert.ToString(scalar.Value, System.Globalization.CultureInfo.InvariantCulture) : value?.ToString();
}
