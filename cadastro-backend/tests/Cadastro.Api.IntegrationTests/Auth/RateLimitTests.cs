using System.Net;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Cadastro.Api.IntegrationTests.Auth;

/// <summary>Rate limit por IP nos endpoints anônimos (dotnet.md; OWASP A06): evita usar o BFF para inundar o Keycloak.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class RateLimitTests(CadastroApiFactory factory)
{
    [Theory]
    [InlineData("RateLimiting:LoginPermitsPerMinute", "/bff/login?returnUrl=https%3A%2F%2Fevil.example.com")]
    [InlineData("RateLimiting:SessionPermitsPerMinute", "/bff/user")]
    public async Task PublicEndpoint_AboveTheLimit_Returns429AndLogsWarning(string setting, string path)
    {
        await using var limited = factory.WithWebHostBuilder(builder => builder.UseSetting(setting, "2"));
        using var client = limited.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        var uri = new Uri(path, UriKind.Relative);

        using var first = await client.GetAsync(uri, TestContext.Current.CancellationToken);
        using var second = await client.GetAsync(uri, TestContext.Current.CancellationToken);
        using var third = await client.GetAsync(uri, TestContext.Current.CancellationToken);

        first.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        second.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        third.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        limited.Services.GetRequiredService<FakeLogCollector>().GetSnapshot().ShouldContain(record =>
            record.Level == LogLevel.Warning && record.Message == $"Rate limit exceeded on {uri.OriginalString.Split('?')[0]}");
    }
}
