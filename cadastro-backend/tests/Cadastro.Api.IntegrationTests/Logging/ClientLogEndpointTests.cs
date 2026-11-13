using System.Net;
using System.Net.Http.Json;
using Cadastro.Api.ClientLogs;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Cadastro.Api.IntegrationTests.Logging;

/// <summary>Logs vindos do navegador: anônimos, nível respeitado, entrada validada e saneada (dado não confiável).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ClientLogEndpointTests(CadastroApiFactory factory)
{
    private static readonly Uri Route = new("/api/v1/client-logs", UriKind.Relative);

    [Theory]
    [InlineData("error", LogLevel.Error)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("information", LogLevel.Information)]
    public async Task ValidEvent_IsLoggedAnonymouslyWithRequestedLevel(string level, LogLevel expected)
    {
        var source = UniqueSource();

        using var response = await PostAsync(new { level, @event = "profile.load_failed", message = "HTTP 503", source, status = 503 });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var logged = BrowserEvent(source);
        logged.Level.ShouldBe(expected);
        logged.Message.ShouldContain("profile.load_failed");
    }

    [Fact]
    public async Task Message_IsSanitizedAgainstLogInjectionAndPersonalData()
    {
        var source = UniqueSource();

        await PostAsync(new
        {
            level = "error",
            @event = "app.unhandled_error",
            message = "falhou\r\nINFO forged line para ana@exemplo.com cpf 123.456.789-09",
            source,
        });

        var message = BrowserEvent(source).Message;
        message.ShouldNotContain("\n");
        message.ShouldNotContain("\r");
        message.ShouldNotContain("ana@exemplo.com");
        message.ShouldNotContain("123.456.789-09");
        message.ShouldContain("***@***");
    }

    [Theory]
    [InlineData("critical", "auth.session_expired")]
    [InlineData("error", "Not A Code")]
    [InlineData(null, "auth.session_expired")]
    public async Task InvalidLevelOrEvent_IsRejectedAndNotLogged(string? level, string clientEvent)
    {
        var source = UniqueSource();

        using var response = await PostAsync(new { level, @event = clientEvent, message = "x", source });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        BrowserEvents(source).ShouldBeEmpty();
    }

    private async Task<HttpResponseMessage> PostAsync(object body) =>
        await factory.CreateClient().PostAsJsonAsync(Route, body, TestContext.Current.CancellationToken);

    private IReadOnlyList<FakeLogRecord> BrowserEvents(string source) =>
        [.. factory.Logs.GetSnapshot().Where(record =>
            record.Category == ClientLogEndpoints.LoggerCategory && record.Message.Contains(source, StringComparison.Ordinal))];

    private FakeLogRecord BrowserEvent(string source) => BrowserEvents(source).ShouldHaveSingleItem();

    // Cada teste usa uma origem única para achar só o próprio evento no coletor compartilhado.
    private static string UniqueSource() => "test-" + Guid.NewGuid().ToString("N")[..12];
}
