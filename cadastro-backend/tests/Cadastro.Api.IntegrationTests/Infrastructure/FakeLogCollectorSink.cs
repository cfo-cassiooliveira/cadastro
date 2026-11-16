using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Cadastro.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Repassa os eventos do Serilog ao <see cref="FakeLogCollector"/>: com o Serilog como fábrica de loggers, os provedores
/// do Microsoft.Extensions.Logging não recebem mais nada, e os testes existentes continuam lendo categoria, nível e
/// mensagem renderizada como antes (strings sem as aspas que o Serilog acrescenta).
/// </summary>
internal sealed class FakeLogCollectorSink(FakeLogCollector collector) : ILogEventSink
{
    private const string SourceContext = "SourceContext";

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        var category = CollectingLogSink.Scalar(logEvent.Properties.GetValueOrDefault(SourceContext)) ?? string.Empty;
        var state = logEvent.Properties
            .Select(p => new KeyValuePair<string, object?>(p.Key, CollectingLogSink.Scalar(p.Value)))
            .ToList();
        var level = ToLevel(logEvent.Level);

        new FakeLogger(collector, category).Log(level, default, state, logEvent.Exception, (_, _) => Render(logEvent));
    }

    private static string Render(LogEvent logEvent)
    {
        var builder = new StringBuilder();
        using var writer = new StringWriter(builder, CultureInfo.InvariantCulture);
        foreach (var token in logEvent.MessageTemplate.Tokens)
        {
            switch (token)
            {
                case TextToken text:
                    writer.Write(text.Text);
                    break;
                case PropertyToken property when logEvent.Properties.TryGetValue(property.PropertyName, out var value):
                    if (value is ScalarValue { Value: string raw })
                    {
                        writer.Write(raw);
                    }
                    else
                    {
                        value.Render(writer, property.Format, CultureInfo.InvariantCulture);
                    }

                    break;
                default:
                    writer.Write(token.ToString());
                    break;
            }
        }

        return builder.ToString();
    }

    private static LogLevel ToLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevel.Trace,
        LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Information,
        LogEventLevel.Warning => LogLevel.Warning,
        LogEventLevel.Error => LogLevel.Error,
        _ => LogLevel.Critical,
    };
}
