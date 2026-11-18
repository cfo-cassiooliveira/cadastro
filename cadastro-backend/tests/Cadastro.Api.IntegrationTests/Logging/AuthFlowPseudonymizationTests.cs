using System.Globalization;
using System.Net;
using Cadastro.Api.Auth;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Cadastro.Application.LogSearch;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;

namespace Cadastro.Api.IntegrationTests.Logging;

/// <summary>
/// Login e acesso negado reais pelo BFF (Keycloak de verdade): o <c>UserName</c> (claim <c>preferred_username</c>) sai
/// como campo estruturado só mascarado (ADR-0002 do graylog) com <c>UserName_token</c>, nunca em claro na mensagem,
/// nas propriedades nem no GELF; valor fora do formato vira <c>***</c> sem token.
/// </summary>
[Collection(KeycloakTestGroup.Name)]
public sealed class AuthFlowPseudonymizationTests(KeycloakApiFactory factory)
{
    private const string Password = "Test-Only-Password-1!";
    private const string UserName = "usuario.teste";
    private const string SignedInTemplate = "User {UserId} signed in (user name {UserName})";
    private const string DeniedTemplate = "Access denied for user {UserId} on {Path} (user name {UserName})";
    private static readonly TimeSpan GelfTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task SignIn_LogsMaskedUserNameWithTokenAndNoClearValue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var gelf = new GelfTcpCapture();
        await using var host = GraylogHost(gelf);
        using var browser = new BrowserSession(host);

        using var _ = await browser.SignInAsync(UserName, Password);

        var raw = await gelf.WaitForAsync("signed in (user name", GelfTimeout, ct);
        var fields = GelfTcpCapture.Fields(raw);
        var expectedToken = ExpectedToken(host, UserName);
        fields["_UserName"].ShouldBe("usu***");
        fields["_UserName_token"].ShouldBe(expectedToken);
        fields["_UserName_token"].ShouldStartWith("k1-");
        fields["short_message"].ShouldBe($"User \"{fields["_UserId"]}\" signed in (user name \"usu***\")");
        ShouldNotLeak(raw);
        fields.Where(f => f.Key != "_UserName_token").ShouldAllBe(f => !f.Value.Contains("k1-", StringComparison.Ordinal));
        var logEvent = EventsOf(SignedInTemplate).Last(e => Scalar(e, "UserName_token") == expectedToken);
        logEvent.Level.ShouldBe(LogEventLevel.Information);
        ShouldNotLeak(logEvent);
    }

    [Fact]
    public async Task AccessDenied_LogsWarningWithMaskedUserNameWithTokenAndNoClearValue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var gelf = new GelfTcpCapture();
        // Nenhuma rota atual exige role pelo cookie (a de log-search tem handler próprio): a policy da rota real
        // /users/me passa a exigir admin só neste host, e o Forbid do cookie percorre o pipeline de verdade.
        await using var host = GraylogHost(gelf).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<AuthorizationOptions>(options =>
                options.AddPolicy(BffAuthenticationSetup.UsersSelfPolicy, policy => policy.RequireRole("admin")))));
        using var browser = new BrowserSession(host);
        using var _ = await browser.SignInAsync(UserName, Password);

        using var response = await browser.Bff.GetAsync(new Uri("/api/v1/users/me", UriKind.Relative), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var raw = await gelf.WaitForAsync("Access denied for user", GelfTimeout, ct);
        var fields = GelfTcpCapture.Fields(raw);
        var expectedToken = ExpectedToken(host, UserName);
        fields["_UserName"].ShouldBe("usu***");
        fields["_UserName_token"].ShouldBe(expectedToken);
        fields["short_message"].ShouldContain("(user name \"usu***\")");
        ShouldNotLeak(raw);
        fields.Where(f => f.Key != "_UserName_token").ShouldAllBe(f => !f.Value.Contains("k1-", StringComparison.Ordinal));
        var logEvent = EventsOf(DeniedTemplate).Last(e => Scalar(e, "UserName_token") == expectedToken);
        logEvent.Level.ShouldBe(LogEventLevel.Warning);
        ShouldNotLeak(logEvent);
    }

    [Fact]
    public async Task SignIn_WithUserNameOutOfFormat_LogsRedactedWithoutToken()
    {
        var ct = TestContext.Current.CancellationToken;
        // Login que começa com "@" (como algo digitado no campo errado): fora do formato aceito pelo pseudonimizador.
        const string odd = "@abc.com";
        await KeycloakAdmin.CreateUserAsync(factory.KeycloakUrl, odd, Password);
        await using var gelf = new GelfTcpCapture();
        await using var host = GraylogHost(gelf);
        using var browser = new BrowserSession(host);

        using var _ = await browser.SignInAsync(odd, Password);

        var raw = await gelf.WaitForAsync("signed in (user name", GelfTimeout, ct);
        var fields = GelfTcpCapture.Fields(raw);
        fields["_UserName"].ShouldBe("***");
        fields.ShouldNotContainKey("_UserName_token");
        raw.ShouldNotContain("abc.com", Case.Insensitive);
    }

    // Contrato: todo campo dos templates novos que não é técnico (UserId, Path) precisa estar na lista de dados pessoais;
    // nome digitado errado sairia em claro.
    [Theory]
    [InlineData(SignedInTemplate)]
    [InlineData(DeniedTemplate)]
    public void NewTemplates_PersonalFieldsAreInPersonalDataFields(string template)
    {
        var parsed = new Serilog.Parsing.MessageTemplateParser().Parse(template);
        var personal = parsed.Tokens.OfType<Serilog.Parsing.PropertyToken>().Select(t => t.PropertyName)
            .Where(name => name is not ("UserId" or "Path")).ToList();

        personal.ShouldNotBeEmpty();
        personal.ShouldAllBe(name => PersonalDataFields.ByName.ContainsKey(name));
        PersonalDataFields.ByName["UserName"].ShouldBe(PersonalDataKind.Login);
    }

    private static IReadOnlyList<LogEvent> EventsOf(string template, KeycloakApiFactory source) =>
        [.. source.SerilogEvents.Events.Where(e => e.MessageTemplate.Text == template)];

    private IReadOnlyList<LogEvent> EventsOf(string template) => EventsOf(template, factory);

    private static string ExpectedToken(WebApplicationFactory<Program> host, string userName) =>
        host.Services.GetRequiredService<ILogPseudonymizer>().TryPseudonymize(PersonalDataKind.Login, userName, out var value)
            ? value.Token
            : throw new InvalidOperationException("Invalid probe value.");

    private static string? Scalar(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) ? CollectingLogSink.Scalar(value) : null;

    private static void ShouldNotLeak(string raw) =>
        raw.ShouldNotContain(UserName, Case.Insensitive, "GELF leaked the user name");

    private static void ShouldNotLeak(LogEvent logEvent)
    {
        logEvent.RenderMessage(CultureInfo.InvariantCulture).ShouldNotContain(UserName, Case.Insensitive);
        foreach (var property in logEvent.Properties)
        {
            property.Value.ToString().ShouldNotContain(UserName, Case.Insensitive, $"property {property.Key} leaked the user name");
        }
    }

    private WebApplicationFactory<Program> GraylogHost(GelfTcpCapture gelf) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Graylog:Host", "127.0.0.1");
            builder.UseSetting("Graylog:Port", gelf.Port.ToString(CultureInfo.InvariantCulture));
        });
}
