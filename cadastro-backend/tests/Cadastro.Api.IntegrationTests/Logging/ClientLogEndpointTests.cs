using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Cadastro.Api.ClientLogs;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Cadastro.Application.LogSearch;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Serilog.Events;

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
        message.ShouldContain("a***@exemplo"); // Parte local com até 3 caracteres mostra só 1 (ADR-0002).
        message.ShouldContain("***.456.789-**");
    }

    [Theory]
    [InlineData("ana.souza@gmail.com", "ana***@gmail")]
    [InlineData("123.456.789-09", "***.456.789-**")]
    [InlineData("+55 (11) 98765-4321", "11*****4321")]
    public async Task PersonalData_InMessageStackAndSource_IsLoggedOnlyWithAdr0002Mask(string value, string mask)
    {
        var marker = UniqueSource();

        await PostAsync(new
        {
            level = "error",
            @event = "app.unhandled_error",
            message = $"Error: falhou para {value}",
            source = $"/users/{value}/{marker}",
            stack = $"Error: falhou para {value}\n    at main.js:1:2",
        });

        var logged = SerilogBrowserEvent(marker);
        string[] texts =
        [
            CollectingLogSink.Scalar(logged.Properties["ClientMessage"])!,
            CollectingLogSink.Scalar(logged.Properties["ClientSource"])!,
            CollectingLogSink.Scalar(logged.Properties["ClientStack"])!,
            logged.RenderMessage(CultureInfo.InvariantCulture),
        ];
        texts.ShouldAllBe(text => !text.Contains(value, StringComparison.Ordinal) && text.Contains(mask, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("a", 5000)]
    [InlineData("a.", 5000)]
    [InlineData("%40", 3000)]
    [InlineData("a@a.", 3000)]
    [InlineData("1", 5000)]
    [InlineData("1.", 5000)]
    public async Task PathologicalText_IsAcceptedWithoutServerError(string unit, int repeat)
    {
        var text = string.Concat(Enumerable.Repeat(unit, repeat));

        using var response = await PostAsync(new { level = "error", @event = "app.unhandled_error", message = text, source = text, stack = text });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Source_DropsQueryStringAndFragment_AndMasksEncodedEmail()
    {
        var marker = UniqueSource();

        await PostAsync(new
        {
            level = "warning",
            @event = "auth.session_expired",
            message = marker,
            source = "/users/ana.souza%40gmail.com?cpf=12345678909&login=maria.silva#phone=11987654321",
        });

        CollectingLogSink.Scalar(SerilogBrowserEvent(marker).Properties["ClientSource"]).ShouldBe("/users/ana***@gmail");
    }

    [Fact]
    public async Task MaskingTimeout_RedactsFieldsAndLogsWarningWithoutOriginalText()
    {
        const string secret = "123.456.789-09";
        using var timeoutFactory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ILogPseudonymizer, TimingOutPseudonymizer>()));
        using var client = timeoutFactory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            Route,
            new { level = "error", @event = "app.unhandled_error", message = $"falhou {secret}", source = $"/x/{secret}", stack = $"at {secret}" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var records = timeoutFactory.Services.GetRequiredService<FakeLogCollector>().GetSnapshot().Where(r => r.Category == ClientLogEndpoints.LoggerCategory).ToList();
        records.ShouldContain(r => r.Level == LogLevel.Error && r.Message == "Browser app.unhandled_error at ***: ***");
        var warnings = records.Where(r => r.Level == LogLevel.Warning).ToList();
        warnings.Count.ShouldBe(3); // Mensagem, origem e stack.
        warnings.ShouldAllBe(r => !r.Message.Contains(secret, StringComparison.Ordinal) && !r.Message.Contains("falhou", StringComparison.Ordinal));
    }

    /// <summary>Simula o estouro de tempo do reconhecimento por formato quando o texto traz o CPF de teste.</summary>
    private sealed class TimingOutPseudonymizer : ILogPseudonymizer
    {
        public bool TryPseudonymize(PersonalDataKind kind, string raw, [NotNullWhen(true)] out PseudonymizedValue? value) =>
            throw new RegexMatchTimeoutException();

        public IReadOnlyList<PseudonymToken> Tokenize(PersonalDataKind kind, string raw) => [];
    }

    [Fact]
    public async Task LongMessage_IsTruncated()
    {
        var marker = UniqueSource();

        await PostAsync(new { level = "warning", @event = "app.long_message", message = marker + new string('x', 5000), source = "x" });

        CollectingLogSink.Scalar(SerilogBrowserEvent(marker).Properties["ClientMessage"])!.Length.ShouldBe(500);
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

    /// <summary>Evento do Serilog (com as propriedades reais) cujo texto contém o marcador do teste.</summary>
    private LogEvent SerilogBrowserEvent(string marker) =>
        factory.SerilogEvents.Events.Single(e => e.RenderMessage(CultureInfo.InvariantCulture).Contains(marker, StringComparison.Ordinal));

    private FakeLogRecord BrowserEvent(string source) => BrowserEvents(source).ShouldHaveSingleItem();

    // Cada teste usa uma origem única para achar só o próprio evento no coletor compartilhado.
    private static string UniqueSource() => "test-" + Guid.NewGuid().ToString("N")[..12];
}
