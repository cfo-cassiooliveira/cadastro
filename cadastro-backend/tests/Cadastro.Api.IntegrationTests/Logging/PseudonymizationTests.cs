using System.Globalization;
using Cadastro.Api.Common;
using Cadastro.Application.LogSearch;
using Cadastro.Infrastructure.Logging;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Cadastro.Api.IntegrationTests.Logging;

/// <summary>
/// Pseudonimização na origem (ADR-0003 do graylog), sem subir a API: vetores obrigatórios do plan.md §2 da spec 001
/// (os mesmos do legado), canônicos, máscaras do ADR-0002, valores fora do formato, e-mail = login, duas chaves,
/// validação da chave e o enricher (topo, aninhado, escopo e todos os nomes das listas <c>LGPD_*_FIELDS</c>).
/// </summary>
public sealed class PseudonymizationTests
{
    // Chave dos vetores (bytes 00 01 02 … 1f): pública por definição, só para conferir o algoritmo entre implementações.
    private const string VectorKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string CpfVectorToken = "k1-b81c27bb5f38080e6ca0ed38b828f262";

    [Theory]
    [InlineData(PersonalDataKind.Cpf, "123.456.789-09", "k1-b81c27bb5f38080e6ca0ed38b828f262")]
    [InlineData(PersonalDataKind.Cnpj, "12.345.678/0001-90", "k1-f528c9e51a504dbaf6a9f245ba2cea10")]
    [InlineData(PersonalDataKind.Phone, "+55 (11) 98765-4321", "k1-412838d1c85d64c0ed202ae3b1546615")]
    [InlineData(PersonalDataKind.Login, " Maria.Silva ", "k1-c5b5c3feef6c408ecea5b17d95af9793")]
    [InlineData(PersonalDataKind.Login, "Ana.Souza@Gmail.com", "k1-c362504bb56a3e35cc245073244c3da6")]
    public void TryPseudonymize_WithPlanVectors_ReproducesExpectedTokens(PersonalDataKind kind, string raw, string expected)
    {
        Pseudonymizer("k1", ("k1", VectorKey)).TryPseudonymize(kind, raw, out var value).ShouldBeTrue();

        value.Token.ShouldBe(expected);
    }

    [Theory]
    [InlineData(PersonalDataKind.Cpf, "123.456.789-09", "12345678909", "***.456.789-**")]
    [InlineData(PersonalDataKind.Cpf, "12345678909", "12345678909", "***.456.789-**")]
    [InlineData(PersonalDataKind.Cnpj, "12.345.678/0001-90", "12345678000190", "12.3**.***/***1-90")]
    [InlineData(PersonalDataKind.Cnpj, "12.abc.345/01de-35", "12ABC34501DE35", "12.A**.***/***E-35")]
    [InlineData(PersonalDataKind.Phone, "+55 (11) 98765-4321", "11987654321", "11*****4321")]
    [InlineData(PersonalDataKind.Phone, "(11) 3456-7890", "1134567890", "11*****7890")]
    [InlineData(PersonalDataKind.Phone, "551134567890", "1134567890", "11*****7890")]
    [InlineData(PersonalDataKind.Login, " Maria.Silva ", "maria.silva", "mar***")]
    [InlineData(PersonalDataKind.Login, "Ana.Souza@Gmail.com", "ana.souza@gmail.com", "ana***@gmail")]
    [InlineData(PersonalDataKind.Login, "bia@x.com", "bia@x.com", "b***@x")]
    public void TryCanonicalize_WithValidValue_ReturnsCanonicalAndMask(PersonalDataKind kind, string raw, string canonical, string mask)
    {
        PersonalDataFormat.TryCanonicalize(kind, raw, out var actual).ShouldBeTrue();

        actual.ShouldBe(canonical);
        PersonalDataFormat.Mask(kind, actual).ShouldBe(mask);
    }

    [Theory]
    [InlineData(PersonalDataKind.Cpf, "1234567890")]
    [InlineData(PersonalDataKind.Cpf, "***.456.789-**")]
    [InlineData(PersonalDataKind.Cnpj, "12.345.678/0001-9X")]
    [InlineData(PersonalDataKind.Cnpj, "123")]
    [InlineData(PersonalDataKind.Phone, "98765-4321")]
    [InlineData(PersonalDataKind.Phone, "+1 (212) 555-01234")]
    [InlineData(PersonalDataKind.Login, "ab")]
    [InlineData(PersonalDataKind.Login, "  abc  ")]
    [InlineData(PersonalDataKind.Login, "@gmail.com")]
    public void Pseudonymizer_WithValueOutOfFormat_ReturnsNoMaskNorToken(PersonalDataKind kind, string raw)
    {
        var pseudonymizer = Pseudonymizer("k1", ("k1", VectorKey));

        pseudonymizer.TryPseudonymize(kind, raw, out var value).ShouldBeFalse();
        value.ShouldBeNull();
        pseudonymizer.Tokenize(kind, raw).ShouldBeEmpty();
    }

    [Fact]
    public void TryPseudonymize_EmailAndLoginOfSameValue_ProduceSameToken()
    {
        var pseudonymizer = Pseudonymizer("k1", ("k1", VectorKey));

        pseudonymizer.TryPseudonymize(PersonalDataKind.Login, "ANA.SOUZA@gmail.com ", out var fromLogin).ShouldBeTrue();
        pseudonymizer.TryPseudonymize(PersonalDataKind.Login, "ana.souza@GMAIL.COM", out var fromEmail).ShouldBeTrue();

        fromLogin.Token.ShouldBe(fromEmail.Token);
    }

    [Fact]
    public void Pseudonymizer_WithTwoKeys_EmitsCurrentKeyAndTokenizesWithEveryKey()
    {
        var otherKey = Convert.ToBase64String(Enumerable.Range(100, 32).Select(b => (byte)b).ToArray());
        var rotated = Pseudonymizer("k2", ("k1", VectorKey), ("k2", otherKey));

        rotated.TryPseudonymize(PersonalDataKind.Cpf, "123.456.789-09", out var emitted).ShouldBeTrue();
        var tokens = rotated.Tokenize(PersonalDataKind.Cpf, "123.456.789-09");

        emitted.Token.ShouldStartWith("k2-");
        emitted.Token.Length.ShouldBe(35);
        emitted.Token[3..].ShouldNotBe(CpfVectorToken[3..]);
        // A pesquisa acha eventos emitidos antes (k1) e depois (k2) da rotação.
        tokens.ShouldBe([new PseudonymToken("k1", CpfVectorToken), new PseudonymToken("k2", emitted.Token)]);
    }

    [Fact]
    public void Pseudonymizer_WithEmptyPreviousKey_IgnoresIt()
    {
        // Compose com k1 e k2 opcionais: chave vazia é ausente e não entra na pesquisa.
        var tokens = Pseudonymizer("k1", ("k1", VectorKey), ("k2", "")).Tokenize(PersonalDataKind.Cpf, "12345678909");

        tokens.ShouldBe([new PseudonymToken("k1", CpfVectorToken)]);
    }

    [Theory]
    [InlineData("k1", "k1", VectorKey, true)]
    [InlineData("k2", "k1", VectorKey, false)]
    [InlineData("key1", "key1", VectorKey, false)]
    [InlineData("k1", "k1", "not base64!", false)]
    [InlineData("k1", "k1", "AAECAwQFBgcICQoLDA0ODw==", false)]
    [InlineData("", "k1", VectorKey, false)]
    public void Validator_ChecksCurrentKeyIdAndKeyStrength(string current, string keyId, string key, bool expected)
    {
        var options = new PseudonymizationOptions { CurrentKeyId = current, Keys = { [keyId] = key } };

        var result = new PseudonymizationOptionsValidator().Validate(null, options);

        result.Succeeded.ShouldBe(expected);
        if (!expected)
        {
            // A mensagem cita o id, nunca o valor da chave.
            result.FailureMessage.ShouldNotBeNull().ShouldNotContain(key);
        }
    }

    [Fact]
    public void Validator_WithEmptyKey_TreatsItAsAbsent()
    {
        var validator = new PseudonymizationOptionsValidator();

        validator.Validate(null, new PseudonymizationOptions { CurrentKeyId = "k2", Keys = { ["k1"] = "", ["k2"] = VectorKey } })
            .Succeeded.ShouldBeTrue();
        validator.Validate(null, new PseudonymizationOptions { CurrentKeyId = "k1", Keys = { ["k1"] = " ", ["k2"] = VectorKey } })
            .FailureMessage.ShouldNotBeNull().ShouldContain("Keys:k1 is not configured");
    }

    // Cópia literal das listas LGPD_*_FIELDS do ../graylog/provisioning/provision.sh: remover um nome falha aqui.
    public static TheoryData<string, string, string> AllFieldNames()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var name in new[] { "cpf", "Cpf", "CPF" })
        {
            data.Add(name, "123.456.789-09", "***.456.789-**");
        }

        foreach (var name in new[] { "cnpj", "Cnpj", "CNPJ" })
        {
            data.Add(name, "12.345.678/0001-90", "12.3**.***/***1-90");
        }

        foreach (var name in new[] { "phone", "Phone", "mobilePhone", "MobilePhone", "mobile_phone", "celular", "Celular", "telefone", "Telefone" })
        {
            data.Add(name, "+55 (11) 98765-4321", "11*****4321");
        }

        foreach (var name in new[] { "username", "user_name", "UserName", "Username", "login", "Login", "preferred_username", "email", "Email" })
        {
            data.Add(name, "Ana.Souza@Gmail.com", "ana***@gmail");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllFieldNames))]
    public void Enrich_EveryProvisionedFieldName_IsMaskedAndTokenized(string field, string raw, string mask)
    {
        var logEvent = Event("{" + field + "}", (field, raw));

        Enricher().Enrich(logEvent, new PropertyFactory());

        Scalar(logEvent, field).ShouldBe(mask);
        Scalar(logEvent, field + PersonalDataFields.TokenSuffix).ShouldNotBeNull().ShouldStartWith("k1-");
    }

    [Fact]
    public void Enrich_ReplacesPersonalDataWithMaskAndAddsToken()
    {
        var logEvent = Event("User {UserName} with {Cpf} and {Phone}", ("UserName", "Maria.Silva"), ("Cpf", "123.456.789-09"),
            ("Phone", "+55 (11) 98765-4321"), ("OrderId", "123.456.789-09"));

        Enricher().Enrich(logEvent, new PropertyFactory());

        Scalar(logEvent, "UserName").ShouldBe("mar***");
        Scalar(logEvent, "UserName_token").ShouldBe("k1-c5b5c3feef6c408ecea5b17d95af9793");
        Scalar(logEvent, "Cpf").ShouldBe("***.456.789-**");
        Scalar(logEvent, "Cpf_token").ShouldBe(CpfVectorToken);
        Scalar(logEvent, "Phone").ShouldBe("11*****4321");
        Scalar(logEvent, "Phone_token").ShouldBe("k1-412838d1c85d64c0ed202ae3b1546615");
        // Campo fora das listas não é tratado (por isso dado pessoal só vai com o nome do tipo).
        Scalar(logEvent, "OrderId").ShouldBe("123.456.789-09");
        // O texto é renderizado depois do enricher: só a máscara, sem token nem valor.
        logEvent.RenderMessage(CultureInfo.InvariantCulture).ShouldBe("User \"mar***\" with \"***.456.789-**\" and \"11*****4321\"");
    }

    [Fact]
    public void Enrich_WithValueOutOfFormatOrNotScalar_RedactsWithoutToken()
    {
        var logEvent = Event("{Email} {Cpf} {Login} {Phone}", ("Email", "ab"), ("Cpf", "123"), ("Login", null), ("Phone", "abc"));
        logEvent.AddOrUpdateProperty(new LogEventProperty("MobilePhone",
            new StructureValue([new LogEventProperty("Number", new ScalarValue("11987654321"))])));

        Enricher().Enrich(logEvent, new PropertyFactory());

        Scalar(logEvent, "Email").ShouldBe(PseudonymizationEnricher.Redacted);
        Scalar(logEvent, "Cpf").ShouldBe(PseudonymizationEnricher.Redacted);
        Scalar(logEvent, "Phone").ShouldBe(PseudonymizationEnricher.Redacted);
        Scalar(logEvent, "MobilePhone").ShouldBe(PseudonymizationEnricher.Redacted);
        Scalar(logEvent, "Login").ShouldBeNull();
        logEvent.Properties.Keys.ShouldNotContain(key => key.EndsWith(PersonalDataFields.TokenSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public void Enrich_WithNumericCpf_UsesInvariantDigits()
    {
        var logEvent = Event("{Cpf}", ("Cpf", 12345678909L));

        Enricher().Enrich(logEvent, new PropertyFactory());

        Scalar(logEvent, "Cpf").ShouldBe("***.456.789-**");
        Scalar(logEvent, "Cpf_token").ShouldBe(CpfVectorToken);
    }

    [Fact]
    public void Enrich_WithDestructuredObject_MasksNestedFieldsAndAddsTopLevelPathToken()
    {
        var address = new StructureValue([new LogEventProperty("Phone", new ScalarValue("(11) 98765-4321")),
            new LogEventProperty("City", new ScalarValue("São Paulo"))]);
        var customer = new StructureValue([new LogEventProperty("Email", new ScalarValue("Ana.Souza@Gmail.com")),
            new LogEventProperty("Address", address), new LogEventProperty("Cpf", new ScalarValue("123"))], "Customer");
        var logEvent = Event("{@Customer}");
        logEvent.AddOrUpdateProperty(new LogEventProperty("Customer", customer));

        Enricher().Enrich(logEvent, new PropertyFactory());

        var result = (StructureValue)logEvent.Properties["Customer"];
        result.TypeTag.ShouldBe("Customer");
        Field(result, "Email").ShouldBe("ana***@gmail");
        // Token fora do objeto (senão sairia no texto renderizado), no topo com o caminho unido por "_".
        Field(result, "Email_token").ShouldBeNull();
        Scalar(logEvent, "Customer_Email_token").ShouldBe("k1-c362504bb56a3e35cc245073244c3da6");
        Field(result, "Cpf").ShouldBe(PseudonymizationEnricher.Redacted);
        Scalar(logEvent, "Customer_Cpf_token").ShouldBeNull();
        var nested = (StructureValue)result.Properties.Single(p => p.Name == "Address").Value;
        Field(nested, "Phone").ShouldBe("11*****4321");
        Scalar(logEvent, "Customer_Address_Phone_token").ShouldBe("k1-412838d1c85d64c0ed202ae3b1546615");
        Field(nested, "City").ShouldBe("São Paulo");
        var rendered = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        rendered.ShouldContain("ana***@gmail");
        rendered.ShouldNotContain("Souza", Case.Insensitive);
        rendered.ShouldNotContain("k1-");
    }

    [Fact]
    public void Enrich_WithDictionaryAndSequence_MasksNestedFields()
    {
        var dictionary = new DictionaryValue([
            KeyValuePair.Create(new ScalarValue("cpf"), (LogEventPropertyValue)new ScalarValue("123.456.789-09")),
            KeyValuePair.Create(new ScalarValue("note"), (LogEventPropertyValue)new ScalarValue("ok")),
        ]);
        var people = new SequenceValue([new StructureValue([new LogEventProperty("login", new ScalarValue(" Maria.Silva "))])]);
        var logEvent = Event("{Data} {People}");
        logEvent.AddOrUpdateProperty(new LogEventProperty("Data", dictionary));
        logEvent.AddOrUpdateProperty(new LogEventProperty("People", people));

        Enricher().Enrich(logEvent, new PropertyFactory());

        var data = ((DictionaryValue)logEvent.Properties["Data"]).Elements.ToDictionary(e => (string)e.Key.Value!, e => ((ScalarValue)e.Value).Value);
        data["cpf"].ShouldBe("***.456.789-**");
        data.ShouldNotContainKey("cpf_token");
        Scalar(logEvent, "Data_cpf_token").ShouldBe(CpfVectorToken);
        data["note"].ShouldBe("ok");
        var person = (StructureValue)((SequenceValue)logEvent.Properties["People"]).Elements.Single();
        Field(person, "login").ShouldBe("mar***");
        Scalar(logEvent, "People_0_login_token").ShouldBe("k1-c5b5c3feef6c408ecea5b17d95af9793");
    }

    [Fact]
    public void Enrich_WithRenderedScope_ReplacesClearValuesByMasks()
    {
        // BeginScope("... {Email} ... {Phone}") gera o campo de topo e o texto já renderizado em "Scope".
        var logEvent = Event("x", ("Email", "Ana.Souza@Gmail.com"), ("Phone", "98765-4321"), ("UserName", "ab"));
        logEvent.AddOrUpdateProperty(new LogEventProperty(PseudonymizationEnricher.ScopeProperty, new SequenceValue([
            new ScalarValue("Signup for Ana.Souza@Gmail.com phone 98765-4321 user ab in tabela"),
            new ScalarValue("Other scope"),
        ])));

        Enricher().Enrich(logEvent, new PropertyFactory());

        var scope = ((SequenceValue)logEvent.Properties[PseudonymizationEnricher.ScopeProperty]).Elements
            .Select(e => ((ScalarValue)e).Value).ToList();
        // Telefone sem DDD (fora do formato, 8+ dígitos) vira ***; login curto não é trocado, para não corromper "tabela".
        scope.ShouldBe(["Signup for ana***@gmail phone *** user ab in tabela", "Other scope"]);
    }

    private static LogPseudonymizer Pseudonymizer(string current, params (string Id, string Key)[] keys)
    {
        var options = new PseudonymizationOptions { CurrentKeyId = current };
        foreach (var (id, key) in keys)
        {
            options.Keys[id] = key;
        }

        return new LogPseudonymizer(Options.Create(options));
    }

    private static PseudonymizationEnricher Enricher() => new(Pseudonymizer("k1", ("k1", VectorKey)));

    private static LogEvent Event(string template, params (string Name, object? Value)[] properties) =>
        new(DateTimeOffset.UtcNow, LogEventLevel.Information, null, new MessageTemplateParser().Parse(template),
            properties.Select(p => new LogEventProperty(p.Name, new ScalarValue(p.Value))));

    private static string? Field(StructureValue structure, string name) =>
        structure.Properties.SingleOrDefault(p => p.Name == name)?.Value is ScalarValue scalar ? scalar.Value?.ToString() : null;

    private static string? Scalar(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar ? scalar.Value?.ToString() : null;

    private sealed class PropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }
}
