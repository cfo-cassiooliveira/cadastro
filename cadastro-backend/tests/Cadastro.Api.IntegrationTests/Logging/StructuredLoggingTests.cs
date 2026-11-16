using System.Net;
using System.Net.Http.Json;
using Cadastro.Api.ClientLogs;
using Cadastro.Api.Common;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Serilog.Events;

namespace Cadastro.Api.IntegrationTests.Logging;

/// <summary>Eventos reais do Serilog levam os campos padrão de observabilidade que o sink GELF envia ao Graylog.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class StructuredLoggingTests(CadastroApiFactory factory)
{
    [Fact]
    public async Task EventInsideRequest_CarriesStandardObservabilityFields()
    {
        var correlationId = "test-" + Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/client-logs")
        {
            Content = JsonContent.Create(new { level = "warning", @event = "profile.load_failed", message = "HTTP 503", source = "structured-logging" }),
        };
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, correlationId);

        using var response = await factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var logged = factory.SerilogEvents.WithProperty("correlation_id", correlationId)
            .Where(e => Prop(e, "SourceContext") == ClientLogEndpoints.LoggerCategory)
            .ShouldHaveSingleItem();
        logged.Level.ShouldBe(LogEventLevel.Warning);
        Prop(logged, "service_name").ShouldBe("cadastro-api");
        Prop(logged, "service_version").ShouldNotBeNullOrWhiteSpace();
        Prop(logged, "environment").ShouldNotBeNullOrWhiteSpace();
        Prop(logged, "trace_id")!.Length.ShouldBe(32);
        Prop(logged, "span_id")!.Length.ShouldBe(16);
    }

    private static string? Prop(LogEvent logEvent, string name) =>
        CollectingLogSink.Scalar(logEvent.Properties.GetValueOrDefault(name));
}
