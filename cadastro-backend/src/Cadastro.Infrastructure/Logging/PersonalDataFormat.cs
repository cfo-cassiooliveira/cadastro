using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Cadastro.Application.LogSearch;

namespace Cadastro.Infrastructure.Logging;

/// <summary>
/// Valor canônico e máscara parcial de dado pessoal, iguais aos do pipeline do Graylog (ADR-0002) e do
/// <c>PersonalDataMasker</c> do legado (plan.md §2 da spec 001 do graylog). Funções puras: o mesmo canônico gera o
/// mesmo token em todas as implementações.
/// </summary>
internal static class PersonalDataFormat
{
    /// <summary>Login/e-mail mais curto que isso é tratado como fora do formato (login do Cadastro tem 4–30).</summary>
    public const int MinLoginLength = 4;

    private const int CpfLength = 11;
    private const int CnpjLength = 14;
    private const int CnpjCheckDigits = 2;
    private const int PhoneMinLength = 10;
    private const int PhoneMaxLength = 11;
    private const int PhoneWithCountryMinLength = 12;
    private const string CountryCode = "55";
    private const int LoginVisiblePrefix = 3;

    /// <summary>
    /// Normaliza o valor para o formato canônico do tipo (base do token). <c>false</c> quando está fora do formato.
    /// CPF e telefone: só dígitos. CNPJ: sem <c>./-</c>, maiúsculo. Login/e-mail: <c>trim</c> e minúsculas.
    /// </summary>
    public static bool TryCanonicalize(PersonalDataKind kind, string raw, [NotNullWhen(true)] out string? canonical)
    {
        ArgumentNullException.ThrowIfNull(raw);
        canonical = kind switch
        {
            PersonalDataKind.Cpf => CanonicalCpf(raw),
            PersonalDataKind.Cnpj => CanonicalCnpj(raw),
            PersonalDataKind.Phone => CanonicalPhone(raw),
            PersonalDataKind.Login => CanonicalLogin(raw),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown personal data kind."),
        };
        return canonical is not null;
    }

    /// <summary>
    /// Máscara parcial do ADR-0002 a partir do valor canônico: <c>***.456.789-**</c>, <c>12.3**.***/***1-90</c>,
    /// <c>11*****4321</c>, <c>mar***</c> ou <c>ana***@gmail</c>.
    /// </summary>
    public static string Mask(PersonalDataKind kind, string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return kind switch
        {
            PersonalDataKind.Cpf => $"***.{canonical[3..6]}.{canonical[6..9]}-**",
            PersonalDataKind.Cnpj => $"{canonical[..2]}.{canonical[2]}**.***/***{canonical[11]}-{canonical[12..]}",
            PersonalDataKind.Phone => $"{canonical[..2]}*****{canonical[^4..]}",
            PersonalDataKind.Login => MaskLogin(canonical),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown personal data kind."),
        };
    }

    /// <summary>Domínio do HMAC por tipo: e-mail e login usam o mesmo (<c>login</c>), então geram o mesmo token.</summary>
    public static string Domain(PersonalDataKind kind) => kind switch
    {
        PersonalDataKind.Cpf => "cpf",
        PersonalDataKind.Cnpj => "cnpj",
        PersonalDataKind.Phone => "phone",
        PersonalDataKind.Login => "login",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown personal data kind."),
    };

    private static string? CanonicalCpf(string raw)
    {
        var digits = OnlyDigits(raw);
        return digits.Length == CpfLength ? digits : null;
    }

    private static string? CanonicalCnpj(string raw)
    {
        var stripped = raw.Trim().Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace("/", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        // CNPJ alfanumérico (IN RFB 2.229/2024): 12 posições [0-9A-Z] + 2 dígitos verificadores numéricos.
        var isValid = stripped.Length == CnpjLength
            && stripped[..^CnpjCheckDigits].All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c))
            && stripped[^CnpjCheckDigits..].All(char.IsAsciiDigit);
        return isValid ? stripped : null;
    }

    private static string? CanonicalPhone(string raw)
    {
        var digits = OnlyDigits(raw);
        if (digits.Length >= PhoneWithCountryMinLength && digits.StartsWith(CountryCode, StringComparison.Ordinal))
        {
            digits = digits[CountryCode.Length..];
        }

        return digits.Length is >= PhoneMinLength and <= PhoneMaxLength ? digits : null;
    }

    private static string? CanonicalLogin(string raw)
    {
        var normalized = raw.Trim().ToLower(CultureInfo.InvariantCulture);
        if (normalized.Length < MinLoginLength)
        {
            return null;
        }

        // E-mail sem parte local ("@gmail.com") não tem máscara possível: fora do formato.
        return normalized.StartsWith('@') ? null : normalized;
    }

    private static string MaskLogin(string canonical)
    {
        var at = canonical.IndexOf('@', StringComparison.Ordinal);
        var local = at < 0 ? canonical : canonical[..at];
        // Até 3 caracteres visíveis; parte local com até 3 caracteres mostra só 1 (regra do ADR-0007 do keycloak).
        var prefix = local.Length > LoginVisiblePrefix ? local[..LoginVisiblePrefix] : local[..1];
        if (at < 0)
        {
            return $"{prefix}***";
        }

        var domain = canonical[(at + 1)..];
        var labelEnd = domain.IndexOfAny(['.', '@']);
        return $"{prefix}***@{(labelEnd < 0 ? domain : domain[..labelEnd])}";
    }

    private static string OnlyDigits(string raw) => string.Concat(raw.Where(char.IsAsciiDigit));
}
