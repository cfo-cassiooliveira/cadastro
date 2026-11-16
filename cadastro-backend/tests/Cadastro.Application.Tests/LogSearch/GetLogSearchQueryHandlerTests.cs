using Cadastro.Application.LogSearch;
using NSubstitute;

namespace Cadastro.Application.Tests.LogSearch;

public sealed class GetLogSearchQueryHandlerTests
{
    private readonly ILogPseudonymizer _pseudonymizer = Substitute.For<ILogPseudonymizer>();
    private readonly GetLogSearchQueryHandler _handler;

    public GetLogSearchQueryHandlerTests() => _handler = new GetLogSearchQueryHandler(_pseudonymizer);

    [Fact]
    public void Handle_WithTwoKeys_BuildsOrOverEveryFieldAndEveryToken()
    {
        _pseudonymizer.Tokenize(PersonalDataKind.Cpf, "123.456.789-09")
            .Returns([new PseudonymToken("k1", "k1-aaa"), new PseudonymToken("k2", "k2-bbb")]);

        var result = _handler.Handle(new GetLogSearchQuery("cpf", "123.456.789-09", "INC-1234"));

        result.Errors.ShouldBeEmpty();
        result.Query!.Tokens.Count.ShouldBe(2);
        result.Query.GraylogQuery.ShouldBe(
            "CPF_token:(\"k1-aaa\" OR \"k2-bbb\") OR Cpf_token:(\"k1-aaa\" OR \"k2-bbb\") OR cpf_token:(\"k1-aaa\" OR \"k2-bbb\")");
    }

    [Theory]
    [InlineData("email")]
    [InlineData("login")]
    public void Handle_ForEmailAndLogin_UsesTheSameKindAndFieldList(string dataType)
    {
        _pseudonymizer.Tokenize(PersonalDataKind.Login, "ana@x.com").Returns([new PseudonymToken("k1", "k1-t")]);

        var result = _handler.Handle(new GetLogSearchQuery(dataType, "ana@x.com", "INC-1234"));

        result.Query!.DataType.ShouldBe(dataType);
        foreach (var field in LogSearchFields.For(PersonalDataKind.Login))
        {
            result.Query.GraylogQuery.ShouldContain($"{field}_token:(\"k1-t\")");
        }
    }

    [Theory]
    [InlineData("cpf", "12345", "INC-1234", "value", "log_search.invalid_value")]
    [InlineData("rg", "12345678909", "INC-1234", "dataType", "log_search.invalid_data_type")]
    [InlineData(null, "12345678909", "INC-1234", "dataType", "log_search.required")]
    [InlineData("cpf", "  ", "INC-1234", "value", "log_search.required")]
    [InlineData("cpf", "12345678909", null, "caseReference", "log_search.required")]
    [InlineData("cpf", "12345678909", "ab", "caseReference", "log_search.invalid_case_reference")]
    [InlineData("cpf", "12345678909", "INC 1234", "caseReference", "log_search.invalid_case_reference")]
    [InlineData("cpf", "12345678909", "INC-1234\n", "caseReference", "log_search.invalid_case_reference")]
    public void Handle_WithInvalidInput_ReturnsOnlyCodesForTheField(string? dataType, string? value, string? caseReference, string field, string code)
    {
        _pseudonymizer.Tokenize(Arg.Any<PersonalDataKind>(), "12345").Returns([]);
        _pseudonymizer.Tokenize(Arg.Any<PersonalDataKind>(), "12345678909").Returns([new PseudonymToken("k1", "k1-t")]);

        var result = _handler.Handle(new GetLogSearchQuery(dataType, value, caseReference));

        result.Query.ShouldBeNull();
        result.Errors[field].ShouldBe([code]);
        // Os códigos nunca ecoam o valor informado.
        result.Errors.Values.SelectMany(codes => codes).ShouldAllBe(c => c.StartsWith("log_search.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("123.456.789-09")]
    [InlineData("12345678909")]
    [InlineData("INC-12345678")]
    [InlineData("12.345.678/0001-90")]
    [InlineData("(11) 98765-4321")]
    [InlineData("11987654321")]
    public void Handle_WithCaseReferenceThatLooksLikePersonalData_IsInvalid(string caseReference)
    {
        _pseudonymizer.Tokenize(Arg.Any<PersonalDataKind>(), "12345678909").Returns([new PseudonymToken("k1", "k1-t")]);

        var result = _handler.Handle(new GetLogSearchQuery("cpf", "12345678909", caseReference));

        result.Errors["caseReference"].ShouldBe(["log_search.invalid_case_reference"]);
    }

    [Fact]
    public void Handle_ForEmailWithoutAtSign_IsInvalidWithoutTokenizing()
    {
        var result = _handler.Handle(new GetLogSearchQuery("email", "maria.silva", "INC-1234"));

        result.Errors["value"].ShouldBe(["log_search.invalid_value"]);
        _pseudonymizer.DidNotReceiveWithAnyArgs().Tokenize(default, default!);
    }

    [Fact]
    public void Handle_WithValueTooLong_IsInvalidWithoutTokenizing()
    {
        var result = _handler.Handle(new GetLogSearchQuery("login", new string('a', 255), "INC-1234"));

        result.Errors["value"].ShouldBe(["log_search.invalid_value"]);
        _pseudonymizer.DidNotReceiveWithAnyArgs().Tokenize(default, default!);
    }
}
