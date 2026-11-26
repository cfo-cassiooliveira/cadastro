using Cadastro.Application.UserImports;

namespace Cadastro.Application.Tests.UserImports;

/// <summary>Validação de CPF, e-mail, telefone e duplicidade; os problemas são códigos, nunca o valor.</summary>
public sealed class UserRowValidationTests
{
    private static ExtractedRow Row(int line, string name = "Ana Souza", string email = "ana@example.com", string cpf = "529.982.247-25", string? phone = "") =>
        new(line, name, email, cpf, phone);

    [Theory]
    [InlineData("52998224725", true)]
    [InlineData("11144477735", true)]
    [InlineData("52998224724", false)]
    [InlineData("11111111111", false)]
    [InlineData("1234567890", false)]
    [InlineData("529982247250", false)]
    [InlineData("5299822472a", false)]
    [InlineData("", false)]
    public void CpfIsValid_ChecksLengthRepetitionAndCheckDigits(string cpf, bool expected) =>
        CpfValidator.IsValid(cpf).ShouldBe(expected);

    [Fact]
    public void Validate_ValidRow_NormalizesTheCpfAndTrimsTheFields()
    {
        var issues = UserRowValidator.Validate(Row(3, "  Ana  ", " ana@example.com ", "529.982.247-25", " +55 (11) 99999-0000 "), out var user);

        issues.ShouldBeEmpty();
        user.ShouldBe(new ValidatedUser(3, "Ana", "ana@example.com", "52998224725", "+55 (11) 99999-0000"));
    }

    [Theory]
    [InlineData("", "a@example.com", "52998224725", "row.name_required")]
    [InlineData("Ana", "", "52998224725", "row.email_required")]
    [InlineData("Ana", "sem-arroba", "52998224725", "row.email_invalid")]
    [InlineData("Ana", "a@b", "52998224725", "row.email_invalid")]
    [InlineData("Ana", "a b@example.com", "52998224725", "row.email_invalid")]
    [InlineData("Ana", "a@example.com", "", "row.cpf_required")]
    [InlineData("Ana", "a@example.com", "123", "row.cpf_invalid")]
    public void Validate_InvalidField_ReturnsOnlyTheCode(string name, string email, string cpf, string code)
    {
        var issues = UserRowValidator.Validate(Row(1, name, email, cpf), out var user);

        user.ShouldBeNull();
        issues.ShouldBe([code]);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("abcdefghijk")]
    [InlineData("1234567890123456")]
    public void Validate_InvalidPhone_ReturnsPhoneCode(string phone) =>
        UserRowValidator.Validate(Row(1, phone: phone), out _).ShouldBe(["row.phone_invalid"]);

    // Lista do validador person-name-prohibited-characters do Keycloak 26: o nome seria recusado (400) só na criação do usuário.
    [Theory]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('&')]
    [InlineData('"')]
    [InlineData('$')]
    [InlineData('%')]
    [InlineData('!')]
    [InlineData('#')]
    [InlineData('?')]
    [InlineData('§')]
    [InlineData(';')]
    [InlineData('*')]
    [InlineData('~')]
    [InlineData('/')]
    [InlineData('\\')]
    [InlineData('|')]
    [InlineData('^')]
    [InlineData('=')]
    [InlineData('[')]
    [InlineData(']')]
    [InlineData('{')]
    [InlineData('}')]
    [InlineData('(')]
    [InlineData(')')]
    public void Validate_NameWithKeycloakProhibitedCharacter_IsInvalidAndNeverEchoesTheValue(char prohibited)
    {
        var issues = UserRowValidator.Validate(Row(1, name: $"Ana {prohibited}Souza"), out var user);

        user.ShouldBeNull();
        issues.ShouldBe(["row.name_invalid"]);
    }

    [Theory]
    [InlineData("José da Conceição")]
    [InlineData("Maria D'Ávila")]
    [InlineData("Ana-Clara Souza")]
    [InlineData("Dr. João Müller")]
    [InlineData("Zoë O'Brien-Lima Jr.")]
    public void Validate_RealisticNamesWithAccentApostropheHyphenAndDot_AreValid(string name)
    {
        UserRowValidator.Validate(Row(1, name: name), out var user).ShouldBeEmpty();
        user!.Name.ShouldBe(name);
    }

    [Fact]
    public void Validate_NameWithControlCharacterOrTooLong_IsInvalid()
    {
        UserRowValidator.Validate(Row(1, name: "Ana\u0007"), out _).ShouldBe(["row.name_invalid"]);
        UserRowValidator.Validate(Row(1, name: new string('a', 201)), out _).ShouldBe(["row.name_invalid"]);
    }

    [Fact]
    public void Validate_StructuralIssue_IsReturnedAsIs() =>
        UserRowValidator.Validate(new ExtractedRow(7, null, null, null, null, "row.malformed"), out _).ShouldBe(["row.malformed"]);

    [Fact]
    public void Validation_DuplicateEmailIgnoringCaseAndSpaces_CountsAsDuplicateAndKeepsTheFirst()
    {
        var validation = new UserImportValidation();

        validation.Add(Row(1, email: "ana@example.com", cpf: "52998224725"));
        validation.Add(Row(2, email: " ANA@Example.com ", cpf: "11144477735"));

        validation.Users.Count.ShouldBe(1);
        validation.DuplicateLines.ShouldBe(1);
        validation.InvalidLines.ShouldBe(0);
        validation.Issues.ShouldBe([new ImportRowIssue(2, "row.duplicate_email")]);
    }

    [Fact]
    public void Validation_DuplicateCpf_CountsAsDuplicate()
    {
        var validation = new UserImportValidation();

        validation.Add(Row(1, email: "a@example.com", cpf: "529.982.247-25"));
        validation.Add(Row(2, email: "b@example.com", cpf: "52998224725"));

        validation.DuplicateLines.ShouldBe(1);
        validation.Issues.Single().Code.ShouldBe("row.duplicate_cpf");
    }

    [Fact]
    public void Validation_DuplicateRow_DoesNotReserveItsOtherValue()
    {
        var validation = new UserImportValidation();

        validation.Add(Row(1, email: "a@example.com", cpf: "52998224725"));
        validation.Add(Row(2, email: "a@example.com", cpf: "11144477735"));
        validation.Add(Row(3, email: "c@example.com", cpf: "11144477735"));

        validation.Users.Select(u => u.LineNumber).ShouldBe([1, 3]);
    }

    [Fact]
    public void Validation_InvalidRowDoesNotReserveEmail()
    {
        var validation = new UserImportValidation();

        validation.Add(Row(1, email: "a@example.com", cpf: "111"));
        validation.Add(Row(2, email: "a@example.com", cpf: "52998224725"));

        validation.InvalidLines.ShouldBe(1);
        validation.Users.Count.ShouldBe(1);
    }

    [Fact]
    public void Validation_IssuesAreCapped()
    {
        var validation = new UserImportValidation();

        for (var i = 1; i <= ImportLimits.MaxReportedIssues + 5; i++)
        {
            validation.Add(Row(i, cpf: "1"));
        }

        validation.InvalidLines.ShouldBe(ImportLimits.MaxReportedIssues + 5);
        validation.Issues.Count.ShouldBe(ImportLimits.MaxReportedIssues);
        validation.IssuesTruncated.ShouldBeTrue();
    }

    private static readonly HmacImportSummarySigner Signer = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public void SummaryToken_IsBoundToBatchOwnerFileAndCounts()
    {
        var batch = Guid.CreateVersion7();
        var hash = new string('a', 64);
        var token = Signer.Sign(batch, "admin-1", hash, 3, 1, 0);

        token.ShouldBe(Signer.Sign(batch, "admin-1", hash, 3, 1, 0));
        Signer.Matches(token, batch, "admin-1", hash, 3, 1, 0).ShouldBeTrue();
        Signer.Matches(token, Guid.CreateVersion7(), "admin-1", hash, 3, 1, 0).ShouldBeFalse();
        Signer.Matches(token, batch, "admin-2", hash, 3, 1, 0).ShouldBeFalse();
        Signer.Matches(token, batch, "admin-1", new string('b', 64), 3, 1, 0).ShouldBeFalse();
        Signer.Matches(token, batch, "admin-1", hash, 4, 1, 0).ShouldBeFalse();
        Signer.Matches(null, batch, "admin-1", hash, 3, 1, 0).ShouldBeFalse();
        Signer.Matches("", batch, "admin-1", hash, 3, 1, 0).ShouldBeFalse();
    }

    [Fact]
    public void SummaryToken_DependsOnTheServerKeyAndRejectsShortKeys()
    {
        var batch = Guid.CreateVersion7();
        var other = new HmacImportSummarySigner(Enumerable.Range(2, 32).Select(i => (byte)i).ToArray());

        Signer.Sign(batch, "a", new string('c', 64), 1, 0, 0).ShouldNotBe(other.Sign(batch, "a", new string('c', 64), 1, 0, 0));
        Should.Throw<ArgumentException>(() => new HmacImportSummarySigner(new byte[31]));
    }

    [Fact]
    public void Envelope_CarriesOnlyIdsAndTheSteeringFields()
    {
        var messageId = Guid.CreateVersion7();
        var batchId = Guid.CreateVersion7();
        var lineId = Guid.CreateVersion7();

        var json = System.Text.Json.JsonDocument.Parse(
            EventEnvelope.CreateLineAccepted(messageId, batchId, lineId, "corr-1", "cause-1", new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(-3))));

        var root = json.RootElement;
        root.GetProperty("messageId").GetGuid().ShouldBe(messageId);
        root.GetProperty("type").GetString().ShouldBe("cadastro.user-import-line.accepted.v1");
        root.GetProperty("source").GetString().ShouldBe("cadastro-api");
        root.GetProperty("occurredAt").GetDateTimeOffset().ShouldBe(new DateTimeOffset(2026, 11, 25, 13, 0, 0, TimeSpan.Zero));
        root.GetProperty("correlationId").GetString().ShouldBe("corr-1");
        root.GetProperty("causationId").GetString().ShouldBe("cause-1");
        var data = root.GetProperty("data");
        data.EnumerateObject().Select(p => p.Name).ShouldBe(["batchId", "lineId"]);
        data.GetProperty("lineId").GetGuid().ShouldBe(lineId);
    }
}
