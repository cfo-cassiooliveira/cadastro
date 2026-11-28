using System.Text;
using System.Text.RegularExpressions;

namespace Cadastro.Application.UserImports;

/// <summary>Pessoa de uma linha válida, normalizada (CPF só com dígitos; e-mail e nome aparados). Nunca vai a log.</summary>
/// <param name="LineNumber">Número da linha de dados no arquivo.</param>
/// <param name="Name">Nome.</param>
/// <param name="Email">E-mail aparado (a normalização completa para o índice é da porta de cifra).</param>
/// <param name="Cpf">CPF somente com os 11 dígitos.</param>
/// <param name="Phone">Telefone aparado; <c>null</c> quando não informado.</param>
internal sealed record ValidatedUser(int LineNumber, string Name, string Email, string Cpf, string? Phone);

/// <summary>
/// Valida uma linha extraída (obrigatórios, e-mail, CPF e telefone) devolvendo só códigos de erro, nunca o valor. Repete
/// regras do Keycloak para a recusa aparecer antes da confirmação; o custo é manter as duas iguais quando o realm ou a versão
/// do Keycloak mudar.
/// </summary>
internal static partial class UserRowValidator
{
    private const int MaxNameLength = 200;

    // Mesma lista do validador person-name-prohibited-characters do Keycloak 26 (User Profile): nome com qualquer destes caracteres
    // seria recusado só na criação (400); barrar no upload deixa a pessoa ver o problema antes de confirmar o lote.
    private static readonly System.Buffers.SearchValues<char> ProhibitedNameCharacters =
        System.Buffers.SearchValues.Create("<>&\"$%!#?§;*~/\\|^=[]{}()");
    private const int MaxEmailLength = 254;
    private const int MinPhoneDigits = 10;
    private const int MaxPhoneDigits = 15;

    /// <summary>Valida a linha; lista de códigos vazia significa válida e <paramref name="user"/> vem preenchido.</summary>
    public static IReadOnlyList<string> Validate(ExtractedRow row, out ValidatedUser? user)
    {
        ArgumentNullException.ThrowIfNull(row);
        user = null;
        if (row.StructuralIssue is { } structural)
        {
            return [structural];
        }

        var issues = new List<string>();
        var name = row.Name?.Trim() ?? string.Empty;
        var email = row.Email?.Trim() ?? string.Empty;
        var cpf = CpfValidator.Normalize(row.Cpf);
        var phone = string.IsNullOrWhiteSpace(row.Phone) ? null : row.Phone.Trim();

        if (name.Length == 0)
        {
            issues.Add("row.name_required");
        }
        else if (name.Length > MaxNameLength || ContainsControlCharacter(name) || name.AsSpan().IndexOfAny(ProhibitedNameCharacters) >= 0
            || PersonNameRules.ExceedsKeycloakLimits(name))
        {
            issues.Add("row.name_invalid");
        }

        if (email.Length == 0)
        {
            issues.Add("row.email_required");
        }
        else if (email.Length > MaxEmailLength || !EmailPattern().IsMatch(email))
        {
            issues.Add("row.email_invalid");
        }

        if (string.IsNullOrWhiteSpace(row.Cpf))
        {
            issues.Add("row.cpf_required");
        }
        else if (!CpfValidator.IsValid(cpf))
        {
            issues.Add("row.cpf_invalid");
        }

        if (phone is not null && !IsValidPhone(phone))
        {
            issues.Add("row.phone_invalid");
        }

        if (issues.Count == 0)
        {
            user = new ValidatedUser(row.LineNumber, name, email, cpf, phone);
        }

        return issues;
    }

    /// <summary>Chave de duplicidade do e-mail: aparado, normalizado em NFC e minúsculo (mesma regra do índice cego).</summary>
    public static string EmailKey(string email) => email.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();

    private static bool IsValidPhone(string phone)
    {
        var digits = 0;
        foreach (var c in phone)
        {
            if (char.IsAsciiDigit(c))
            {
                digits++;
            }
            else if (c is not ('+' or ' ' or '(' or ')' or '-'))
            {
                return false;
            }
        }

        return digits is >= MinPhoneDigits and <= MaxPhoneDigits;
    }

    private static bool ContainsControlCharacter(string value) => value.Any(char.IsControl);

    // Parte local em átomos separados por ponto (sem ponto no início, no fim ou duplo), até 64 caracteres: o Keycloak 26 recusa
    // (400 error-invalid-email) as três formas, e o upload as barra antes da confirmação. Domínio: rótulos sem hífen nas pontas e TLD de letras.
    [GeneratedRegex(@"^(?=[^@]{1,64}@)[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,}$")]
    private static partial Regex EmailPattern();
}

/// <summary>Validação do CPF pelos dígitos verificadores.</summary>
internal static class CpfValidator
{
    private const int Length = 11;

    /// <summary>Remove a formatação (pontos e hífen); qualquer outro caractere é mantido para a validação recusar.</summary>
    public static string Normalize(string? value) =>
        value is null ? string.Empty : value.Trim().Replace(".", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);

    /// <summary>Indica se são 11 dígitos, não todos iguais, com os dois dígitos verificadores corretos.</summary>
    public static bool IsValid(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        if (normalized.Length != Length || !normalized.All(char.IsAsciiDigit) || normalized.Distinct().Count() == 1)
        {
            return false;
        }

        return CheckDigit(normalized, 9) == normalized[9] - '0' && CheckDigit(normalized, 10) == normalized[10] - '0';
    }

    private static int CheckDigit(string cpf, int length)
    {
        var sum = 0;
        for (var i = 0; i < length; i++)
        {
            sum += (cpf[i] - '0') * (length + 1 - i);
        }

        var remainder = sum * 10 % 11;
        return remainder == 10 ? 0 : remainder;
    }
}
