using System.Diagnostics;
using Cadastro.Api.Common;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Serilog.Sinks.Graylog.Core.Transport;

namespace Cadastro.Api.IntegrationTests.Logging;

/// <summary>Testes isolados da configuração de logging (sem subir a API).</summary>
public sealed class LoggingConfigurationTests
{
    [Fact]
    public void GraylogOptions_WithoutHost_IsDisabledAndLogsStayOnConsole()
    {
        LoggingSetup.BuildGraylogOptions(Config()).ShouldBeNull();
        LoggingSetup.BuildGraylogOptions(Config(("Graylog:Host", "  "))).ShouldBeNull();
    }

    [Fact]
    public void GraylogOptions_WithHost_DefaultsToTcpWithTemplateAndServiceFacility()
    {
        var options = LoggingSetup.BuildGraylogOptions(Config(("Graylog:Host", "graylog")));

        options.ShouldNotBeNull();
        options.HostnameOrAddress.ShouldBe("graylog");
        options.Port.ShouldBe(12201);
        options.TransportType.ShouldBe(TransportType.Tcp);
        options.Facility.ShouldBe("cadastro-api");
        options.IncludeMessageTemplate.ShouldBeTrue();
        options.MessageTemplateFieldName.ShouldBe("message_template");
    }

    [Fact]
    public void GraylogOptions_HonorsConfiguredPortAndTransport()
    {
        var options = LoggingSetup.BuildGraylogOptions(Config(("Graylog:Host", "logs"), ("Graylog:Port", "5555"), ("Graylog:Transport", "Udp")));

        options.ShouldNotBeNull();
        options.Port.ShouldBe(5555);
        options.TransportType.ShouldBe(TransportType.Udp);
    }

    [Fact]
    public void ServiceName_MatchesTheGraylogStreams()
    {
        // Os streams "Cadastro - *" do ../graylog/provisioning/provision.sh filtram por service_name:cadastro-api.
        LoggingSetup.ServiceName.ShouldBe("cadastro-api");
    }

    [Fact]
    public void TraceContextEnricher_AddsTraceSpanAndThread()
    {
        using var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();
        var logEvent = NewEvent();

        new TraceContextEnricher().Enrich(logEvent, new PropertyFactory());

        logEvent.Properties["trace_id"].ToString().ShouldContain(activity.TraceId.ToHexString());
        logEvent.Properties["span_id"].ToString().ShouldContain(activity.SpanId.ToHexString());
        logEvent.Properties.ShouldContainKey("thread_id");
    }

    [Fact]
    public void TraceContextEnricher_WithoutActivity_AddsOnlyThread()
    {
        Activity.Current = null;
        var logEvent = NewEvent();

        new TraceContextEnricher().Enrich(logEvent, new PropertyFactory());

        logEvent.Properties.ShouldNotContainKey("trace_id");
        logEvent.Properties.ShouldNotContainKey("span_id");
        logEvent.Properties.ShouldContainKey("thread_id");
    }

    [Fact]
    public void EntityFrameworkCommands_AreWarningByDefaultButApplicationLogsStayInformation()
    {
        // Lê o appsettings.json REAL da API: o polling do relay gerava dezenas de milhares de linhas Information por execução.
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        var captured = new List<(string Source, LogEventLevel Level)>();
        using var logger = new Serilog.LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .WriteTo.Sink(new DelegateSink(e => captured.Add((((ScalarValue)e.Properties["SourceContext"]).Value!.ToString()!, e.Level))))
            .CreateLogger();

        foreach (var source in new[] { "Microsoft.EntityFrameworkCore.Database.Command", "Microsoft.EntityFrameworkCore.Infrastructure", "Cadastro.Application.UserImports.X" })
        {
            logger.ForContext("SourceContext", source).Information("info");
            logger.ForContext("SourceContext", source).Warning("warn");
        }

        captured.Where(c => c.Source.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)).ShouldAllBe(c => c.Level == LogEventLevel.Warning);
        captured.Count(c => c.Source.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)).ShouldBe(2);
        captured.Count(c => c.Source == "Cadastro.Application.UserImports.X").ShouldBe(2);
    }

    private sealed class DelegateSink(Action<LogEvent> write) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => write(logEvent);
    }

    private static LogEvent NewEvent() =>
        new(DateTimeOffset.UtcNow, LogEventLevel.Information, null, new MessageTemplateParser().Parse("x"), []);

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private sealed class PropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }
}
