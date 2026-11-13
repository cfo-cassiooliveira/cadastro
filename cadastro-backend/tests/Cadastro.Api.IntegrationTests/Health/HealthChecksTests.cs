using System.Net;
using System.Text.Json;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Cadastro.Infrastructure.Keycloak;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cadastro.Api.IntegrationTests.Health;

[Collection(ApiTestGroup.Name)]
public sealed class HealthChecksTests(CadastroApiFactory factory)
{
    [Fact]
    public async Task Live_ReturnsHealthyWithoutCheckingDependencies()
    {
        var response = await factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("Healthy");
    }

    [Fact]
    public async Task Ready_DoesNotIncludeProcessMetrics()
    {
        using var report = await GetReportAsync("/health/ready");

        report.RootElement.GetProperty("status").GetString().ShouldBe("Healthy");
        report.RootElement.GetProperty("checks").TryGetProperty("process", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Ready_WhenKeycloakIsUnreachable_Returns503WithoutDetails()
    {
        using var down = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(KeycloakHealthCheck.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new UnreachableHandler())));

        var response = await down.CreateClient().GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        body.ShouldContain("\"keycloak\"");
        body.ShouldNotContain("Connection refused");
        body.ShouldNotContain("127.0.0.1");
    }

    [Fact]
    public async Task Detailed_ReportsProcessMetrics()
    {
        using var report = await GetReportAsync("/health");

        var process = report.RootElement.GetProperty("checks").GetProperty("process");
        process.GetProperty("status").GetString().ShouldBe("Healthy");
        process.GetProperty("data").GetProperty("allocated_mb").GetInt64().ShouldBeGreaterThan(0);
        process.GetProperty("data").GetProperty("uptime_seconds").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Detailed_WhenMemoryAboveLimit_ReportsDegraded()
    {
        using var lowLimit = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("HealthChecks:Process:MaxAllocatedMegabytes", "0"));

        var body = await lowLimit.CreateClient().GetStringAsync("/health", TestContext.Current.CancellationToken);
        using var report = JsonDocument.Parse(body);

        report.RootElement.GetProperty("status").GetString().ShouldBe("Degraded");
    }

    [Fact]
    public async Task Response_DoesNotRevealServerAndCarriesCorrelationId()
    {
        var response = await factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.Headers.Contains("Server").ShouldBeFalse();
        response.Headers.GetValues("X-Correlation-Id").Single().ShouldNotBeNullOrWhiteSpace();
    }

    private async Task<JsonDocument> GetReportAsync(string path)
    {
        var response = await factory.CreateClient().GetAsync(path, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
