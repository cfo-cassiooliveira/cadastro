using Cadastro.Application.LogSearch;

namespace Cadastro.Application.Tests.LogSearch;

/// <summary>A pesquisa usa exatamente as listas do ADR-0002 (as <c>LGPD_*_FIELDS</c> do graylog): sem lista própria.</summary>
public sealed class LogSearchFieldsTests
{
    // Cópia literal das listas LGPD_*_FIELDS do ../graylog/provisioning/provision.sh.
    private static readonly Dictionary<PersonalDataKind, string[]> Adr0002 = new()
    {
        [PersonalDataKind.Cpf] = ["cpf", "Cpf", "CPF"],
        [PersonalDataKind.Cnpj] = ["cnpj", "Cnpj", "CNPJ"],
        [PersonalDataKind.Phone] = ["phone", "Phone", "mobilePhone", "MobilePhone", "mobile_phone", "celular", "Celular", "telefone", "Telefone"],
        [PersonalDataKind.Login] = ["username", "user_name", "UserName", "Username", "login", "Login", "preferred_username", "email", "Email"],
    };

    // Campos que o graylog mascara, mas que NÃO têm "_token": o Keycloak não gera token nem recebe a chave HMAC
    // (ADR-0003 do graylog e ADR-0007 do keycloak). Não existe pesquisa por token para eles, então ficam fora da lista.
    private static readonly HashSet<string> MaskOnlyFields = new(StringComparer.Ordinal) { "masked_login" };

    [Theory]
    [InlineData(PersonalDataKind.Cpf)]
    [InlineData(PersonalDataKind.Cnpj)]
    [InlineData(PersonalDataKind.Phone)]
    [InlineData(PersonalDataKind.Login)]
    public void For_ReturnsExactlyTheAdr0002List(PersonalDataKind kind) =>
        LogSearchFields.For(kind).ShouldBe(Adr0002[kind], ignoreOrder: true);

    [Fact]
    public void For_CoversEveryPersonalDataFieldExactlyOnce() =>
        Enum.GetValues<PersonalDataKind>().SelectMany(LogSearchFields.For)
            .ShouldBe(PersonalDataFields.ByName.Keys, ignoreOrder: true);

    [Fact]
    public void For_MatchesTheGraylogProvisioningScriptWhenItIsCheckedOut()
    {
        var script = FindProvisionScript();
        Assert.SkipUnless(script is not null, "../graylog/provisioning/provision.sh não está ao lado deste repositório.");

        var lines = File.ReadAllLines(script!);
        foreach (var (kind, expected) in new Dictionary<PersonalDataKind, string>
        {
            [PersonalDataKind.Cpf] = "LGPD_CPF_FIELDS",
            [PersonalDataKind.Cnpj] = "LGPD_CNPJ_FIELDS",
            [PersonalDataKind.Phone] = "LGPD_PHONE_FIELDS",
            [PersonalDataKind.Login] = "LGPD_LOGIN_FIELDS",
        })
        {
            var line = lines.Single(l => l.StartsWith(expected + "=(", StringComparison.Ordinal));
            var names = line[(line.IndexOf('(', StringComparison.Ordinal) + 1)..line.LastIndexOf(')')]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(name => !MaskOnlyFields.Contains(name));
            LogSearchFields.For(kind).ShouldBe(names, ignoreOrder: true);
        }
    }

    [Fact]
    public void BuildQuery_ForPhone_UsesTokenSuffixOnEveryField()
    {
        var query = LogSearchFields.BuildQuery(PersonalDataKind.Phone, [new PseudonymToken("k1", "k1-x")]);

        query.Split(" OR ").Length.ShouldBe(Adr0002[PersonalDataKind.Phone].Length);
        foreach (var field in Adr0002[PersonalDataKind.Phone])
        {
            query.ShouldContain($"{field}_token:(\"k1-x\")");
        }
    }

    private static string? FindProvisionScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "graylog", "provisioning", "provision.sh");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
