using System.Collections.Frozen;

namespace Cadastro.Application.LogSearch;

/// <summary>
/// Nomes de campo de log que carregam dado pessoal, por tipo. São as mesmas listas <c>LGPD_*_FIELDS</c> do
/// <c>../graylog/provisioning/provision.sh</c> (ADR-0002 do graylog) e do legado: nome novo entra nos três juntos.
/// </summary>
public static class PersonalDataFields
{
    /// <summary>Sufixo do campo com o token pseudônimo (<c>Cpf</c> → <c>Cpf_token</c>, ADR-0003 do graylog).</summary>
    public const string TokenSuffix = "_token";

    /// <summary>Nome do campo → tipo do dado (comparação exata: o Graylog também diferencia maiúsculas).</summary>
    public static FrozenDictionary<string, PersonalDataKind> ByName { get; } = Build();

    private static FrozenDictionary<string, PersonalDataKind> Build()
    {
        string[] cpf = ["cpf", "Cpf", "CPF"];
        string[] cnpj = ["cnpj", "Cnpj", "CNPJ"];
        string[] phone = ["phone", "Phone", "mobilePhone", "MobilePhone", "mobile_phone", "celular", "Celular", "telefone", "Telefone"];
        string[] login = ["username", "user_name", "UserName", "Username", "login", "Login", "preferred_username", "email", "Email"];

        return cpf.Select(name => KeyValuePair.Create(name, PersonalDataKind.Cpf))
            .Concat(cnpj.Select(name => KeyValuePair.Create(name, PersonalDataKind.Cnpj)))
            .Concat(phone.Select(name => KeyValuePair.Create(name, PersonalDataKind.Phone)))
            .Concat(login.Select(name => KeyValuePair.Create(name, PersonalDataKind.Login)))
            .ToFrozenDictionary(StringComparer.Ordinal);
    }
}
