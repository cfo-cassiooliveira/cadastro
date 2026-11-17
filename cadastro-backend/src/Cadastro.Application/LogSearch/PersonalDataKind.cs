namespace Cadastro.Application.LogSearch;

/// <summary>
/// Tipos de dado pessoal reconhecidos pelo nome do campo de log (ADR-0002 e ADR-0003 do projeto graylog).
/// E-mail e login são o mesmo tipo: o usuário entra com um ou outro no mesmo campo.
/// </summary>
public enum PersonalDataKind
{
    /// <summary>CPF (11 dígitos).</summary>
    Cpf,

    /// <summary>CNPJ numérico ou alfanumérico (14 posições).</summary>
    Cnpj,

    /// <summary>Telefone fixo ou celular com DDD.</summary>
    Phone,

    /// <summary>Login ou e-mail.</summary>
    Login,
}
