using System.Net;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;

namespace Cadastro.Api.IntegrationTests.Logging;

/// <summary>O IP do cliente é a chave do rate limit, mas nunca pode aparecer em log (dado pessoal, LGPD).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class RateLimitLoggingTests(CadastroApiFactory factory)
{
    // O TestServer não tem IP remoto: o X-Forwarded-For (confiado na Program.cs, atrás do nginx) define o RemoteIpAddress.
    [Fact]
    public async Task RateLimitedRequests_NeverLogClientIpOrAuthorizationHeader()
    {
        const string clientIp = "203.0.113.77";
        var token = "token-reconhecivel-" + Guid.NewGuid().ToString("N");
        await using var limited = factory.WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:SessionPermitsPerMinute", "1"));
        using var client = limited.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/user");
            request.Headers.Add("X-Forwarded-For", clientIp);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        statuses[2].ShouldBe(HttpStatusCode.TooManyRequests);
        var microsoftLogs = limited.Services.GetRequiredService<FakeLogCollector>().GetSnapshot()
            .Select(r => r.Message + " " + r.Exception);
        var serilogLogs = factory.SerilogEvents.Events.Select(e =>
            string.Join(" | ", [e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture), e.Exception?.ToString() ?? string.Empty,
                .. e.Properties.Select(p => $"{p.Key}={p.Value}")]));
        var allLogText = string.Join("\n", microsoftLogs.Concat(serilogLogs));
        allLogText.ShouldContain("Rate limit exceeded", Case.Sensitive, "o limiter precisa ter rejeitado a requisição");
        foreach (var secret in new[] { clientIp, token, "Bearer" })
        {
            allLogText.ShouldNotContain(secret, Case.Insensitive, $"o log não pode conter '{secret}'");
        }
    }
}
