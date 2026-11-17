using System.Globalization;
using System.Security.Cryptography;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Cadastro.Application.LogSearch;
using Cadastro.Infrastructure.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadastro.Api.IntegrationTests.Logging;

/// <summary>
/// Pseudonimização na origem com o pipeline real da API (ADR-0003 do graylog): o que sai pelo sink GELF TCP nunca
/// leva o valor em claro, o texto já vai mascarado (sem token) e cada campo pessoal ganha <c>*_token</c>, também em
/// objeto destruturado, escopo e LogContext. Sem chave atual válida, a API não sobe.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed partial class PseudonymizedGelfTests(CadastroApiFactory factory)
{
    private const string Cpf = "123.456.789-09";
    private const string Cnpj = "12.345.678/0001-90";
    private const string Phone = "+55 (11) 98765-4321";
    private const string UserName = " Maria.Silva ";
    private const string Email = "Ana.Souza@Gmail.com";
    private const string ExceptionText = "Simulated failure without personal data";

    // Valores de entrada e seus canônicos: nenhum pode aparecer em campo algum da mensagem GELF.
    private static readonly string[] ClearValues =
        ["123.456.789-09", "12345678909", "12.345.678/0001-90", "12345678000190", "98765-4321", "11987654321",
            "Maria.Silva", "maria.silva", "Ana.Souza", "ana.souza"];

    private static readonly TimeSpan GelfTimeout = TimeSpan.FromSeconds(15);

    private static readonly Func<ILogger, string, string, IDisposable?> SignupScope =
        LoggerMessage.DefineScope<string, string>("Signup of {Email} with {Cpf}");

    [Fact]
    public async Task GelfMessage_WithPersonalDataAndException_CarriesOnlyMasksAndTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var gelf = new GelfTcpCapture();
        await using var host = GraylogHost(gelf);
        var marker = Guid.NewGuid().ToString("N");
        var logger = host.Services.GetRequiredService<ILogger<PseudonymizedGelfTests>>();
        var pseudonymizer = host.Services.GetRequiredService<ILogPseudonymizer>();

        LogPersonalDataFailure(logger, new InvalidOperationException(ExceptionText), Cpf, Cnpj, Phone, UserName, Email, marker);

        var raw = await gelf.WaitForAsync(marker, GelfTimeout, ct);
        var fields = GelfTcpCapture.Fields(raw);
        ShouldNotLeak(raw, ClearValues);
        fields["_Cpf"].ShouldBe("***.456.789-**");
        fields["_Cnpj"].ShouldBe("12.3**.***/***1-90");
        fields["_Phone"].ShouldBe("11*****4321");
        fields["_UserName"].ShouldBe("mar***");
        fields["_Email"].ShouldBe("ana***@gmail");
        fields["_Cpf_token"].ShouldBe(CurrentToken(pseudonymizer, PersonalDataKind.Cpf, Cpf));
        fields["_Cnpj_token"].ShouldBe(CurrentToken(pseudonymizer, PersonalDataKind.Cnpj, Cnpj));
        fields["_Phone_token"].ShouldBe(CurrentToken(pseudonymizer, PersonalDataKind.Phone, Phone));
        fields["_UserName_token"].ShouldBe(CurrentToken(pseudonymizer, PersonalDataKind.Login, UserName));
        fields["_Email_token"].ShouldBe(CurrentToken(pseudonymizer, PersonalDataKind.Login, Email));
        fields["_Email_token"].ShouldStartWith("k1-");
        // A exceção segue sem alteração (o Serilog não permite trocá-la): por isso ela nunca pode levar dado pessoal.
        raw.ShouldContain(ExceptionText);

        // Texto livre (short_message/full_message): só a máscara, nunca o token.
        var message = fields["short_message"];
        message.ShouldContain("***.456.789-**");
        message.ShouldContain("12.3**.***/***1-90");
        message.ShouldContain("11*****4321");
        message.ShouldContain("mar***");
        message.ShouldContain("ana***@gmail");
        ShouldHaveTokensOnlyInTokenFields(fields);
    }

    [Fact]
    public async Task GelfMessage_WithDestructuredObjectScopeAndLogContext_LeaksNoClearValue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var gelf = new GelfTcpCapture();
        await using var host = GraylogHost(gelf);
        var marker = Guid.NewGuid().ToString("N");
        var logger = host.Services.GetRequiredService<ILogger<PseudonymizedGelfTests>>();
        var pseudonymizer = host.Services.GetRequiredService<ILogPseudonymizer>();
        var customer = new ProbeCustomer(Email, new ProbeAddress(Phone));

        // Escopo com template: o provider soma o campo Email e o texto renderizado em "Scope", ambos em claro.
        using (SignupScope(logger, Email, Cpf))
        using (logger.BeginScope(new Dictionary<string, object> { ["Cnpj"] = Cnpj }))
        using (Serilog.Context.LogContext.PushProperty("UserName", UserName))
        {
            LogCustomer(logger, customer, marker);
        }

        var raw = await gelf.WaitForAsync(marker, GelfTimeout, ct);
        var fields = GelfTcpCapture.Fields(raw);
        ShouldNotLeak(raw, ClearValues);
        fields["_Customer.Email"].ShouldBe("ana***@gmail");
        fields["_Customer_Email_token"].ShouldBe(CurrentToken(pseudonymizer, PersonalDataKind.Login, Email));
        fields["_Customer.Address.Phone"].ShouldBe("11*****4321");
        fields["_Customer_Address_Phone_token"].ShouldBe(CurrentToken(pseudonymizer, PersonalDataKind.Phone, Phone));
        fields["_Email_token"].ShouldBe(CurrentToken(pseudonymizer, PersonalDataKind.Login, Email));
        fields["_Cpf_token"].ShouldBe(CurrentToken(pseudonymizer, PersonalDataKind.Cpf, Cpf));
        fields["_Cnpj"].ShouldBe("12.3**.***/***1-90");
        fields["_Cnpj_token"].ShouldBe(CurrentToken(pseudonymizer, PersonalDataKind.Cnpj, Cnpj));
        fields["_UserName"].ShouldBe("mar***");
        raw.ShouldContain("Signup of ana***@gmail with ***.456.789-**");
        fields["short_message"].ShouldContain("ana***@gmail");
        ShouldHaveTokensOnlyInTokenFields(fields);
    }

    [Fact]
    public async Task GelfMessage_AfterRotationWithPreviousKeyRemoved_EmitsCurrentKeyToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var newKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var gelf = new GelfTcpCapture();
        // Como o docker-compose.yml depois da rotação: CURRENT_KEY_ID=k2, K2 preenchida e K1 vazia.
        await using var host = GraylogHost(gelf, ("Logging:Pseudonymization:CurrentKeyId", "k2"),
            ("Logging:Pseudonymization:Keys:k2", newKey), ("Logging:Pseudonymization:Keys:k1", string.Empty));
        var marker = Guid.NewGuid().ToString("N");
        var logger = host.Services.GetRequiredService<ILogger<PseudonymizedGelfTests>>();

        LogPersonalDataFailure(logger, new InvalidOperationException(ExceptionText), Cpf, Cnpj, Phone, UserName, Email, marker);

        var fields = GelfTcpCapture.Fields(await gelf.WaitForAsync(marker, GelfTimeout, ct));
        var expected = new LogPseudonymizer(Options.Create(new PseudonymizationOptions { CurrentKeyId = "k2", Keys = { ["k2"] = newKey } }));
        fields["_Cpf_token"].ShouldBe(CurrentToken(expected, PersonalDataKind.Cpf, Cpf));
        fields["_Cpf_token"].ShouldStartWith("k2-");
    }

    [Theory]
    [InlineData("Logging:Pseudonymization:CurrentKeyId", "", "CurrentKeyId must look like 'k1'")]
    [InlineData("Logging:Pseudonymization:CurrentKeyId", "k9", "Keys:k9 is not configured")]
    [InlineData("Logging:Pseudonymization:Keys:k1", "c2hvcnQ=", "Keys:k1 must be Base64")]
    public void Startup_WithoutValidCurrentKey_FailsFastWithoutExposingTheKey(string setting, string value, string expectedMessage)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting(setting, value));

        var error = Should.Throw<OptionsValidationException>(() => host.CreateClient());

        error.Message.ShouldContain(expectedMessage);
        error.Message.ShouldNotContain(CadastroApiFactory.PseudonymizationKey);
        error.Message.ShouldNotContain("c2hvcnQ=");
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Pseudonymization probe for {Cpf} {Cnpj} {Phone} {UserName} {Email} marker {TestMarker}")]
    private static partial void LogPersonalDataFailure(ILogger logger, Exception exception, string cpf, string cnpj, string phone,
        string userName, string email, string testMarker);

    [LoggerMessage(Level = LogLevel.Information, Message = "Customer {@Customer} marker {TestMarker}")]
    private static partial void LogCustomer(ILogger logger, ProbeCustomer customer, string testMarker);

    private static string CurrentToken(ILogPseudonymizer pseudonymizer, PersonalDataKind kind, string raw) =>
        pseudonymizer.TryPseudonymize(kind, raw, out var value) ? value.Token : throw new InvalidOperationException("Invalid probe value.");

    private static void ShouldNotLeak(string raw, IEnumerable<string> clearValues)
    {
        foreach (var value in clearValues)
        {
            raw.ShouldNotContain(value, Case.Insensitive, $"GELF leaked '{value}'");
        }
    }

    private static void ShouldHaveTokensOnlyInTokenFields(IReadOnlyDictionary<string, string> fields)
    {
        foreach (var text in fields.Where(f => !f.Key.EndsWith(PersonalDataFields.TokenSuffix, StringComparison.Ordinal)))
        {
            text.Value.ShouldNotContain("k1-", Case.Sensitive, $"token leaked into {text.Key}");
        }
    }

    private WebApplicationFactory<Program> GraylogHost(GelfTcpCapture gelf, params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Graylog:Host", "127.0.0.1");
            builder.UseSetting("Graylog:Port", gelf.Port.ToString(CultureInfo.InvariantCulture));
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    /// <summary>Objeto destruturado de teste (<c>{@Customer}</c>), com dado pessoal aninhado.</summary>
    public sealed record ProbeCustomer(string Email, ProbeAddress Address);

    /// <summary>Nível aninhado do objeto de teste.</summary>
    public sealed record ProbeAddress(string Phone);
}
