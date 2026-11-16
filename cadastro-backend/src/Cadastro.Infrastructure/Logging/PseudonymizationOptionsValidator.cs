using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Logging;

/// <summary>
/// Valida as chaves na subida (fail fast): sem chave atual válida a API não sobe, porque logaria dado pessoal sem
/// token ou deixaria de pseudonimizar. As mensagens citam só o id da chave, nunca o valor.
/// </summary>
internal sealed partial class PseudonymizationOptionsValidator : IValidateOptions<PseudonymizationOptions>
{
    /// <summary>Confere o formato dos ids (<c>k1</c>, <c>k2</c>...), o Base64 de 256+ bits e a presença da chave atual.</summary>
    public ValidateOptionsResult Validate(string? name, PseudonymizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (!KeyIdPattern().IsMatch(options.CurrentKeyId))
        {
            failures.Add($"{PseudonymizationOptions.SectionName}:CurrentKeyId must look like 'k1'.");
        }
        else if (!options.Keys.TryGetValue(options.CurrentKeyId, out var current) || string.IsNullOrWhiteSpace(current))
        {
            failures.Add($"{PseudonymizationOptions.SectionName}:Keys:{options.CurrentKeyId} is not configured.");
        }

        // Chave vazia conta como ausente: o compose mapeia k1 e k2 como opcionais (rotação sem editar o compose).
        foreach (var (keyId, value) in options.Keys.Where(k => !string.IsNullOrWhiteSpace(k.Value)))
        {
            if (!KeyIdPattern().IsMatch(keyId))
            {
                failures.Add($"{PseudonymizationOptions.SectionName}:Keys has an invalid key id (expected 'k1', 'k2'...).");
            }
            else if (!IsValidKey(value))
            {
                failures.Add($"{PseudonymizationOptions.SectionName}:Keys:{keyId} must be Base64 with at least {PseudonymizationOptions.MinKeyBytes} bytes.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Decodifica a chave em Base64; <c>null</c> quando o texto não é Base64 válido.</summary>
    public static byte[]? Decode(string? value)
    {
        var buffer = new byte[(value?.Length ?? 0) * 3 / 4 + 3];
        return !string.IsNullOrWhiteSpace(value) && Convert.TryFromBase64String(value.Trim(), buffer, out var written)
            ? buffer[..written]
            : null;
    }

    private static bool IsValidKey(string? value) => Decode(value) is { Length: >= PseudonymizationOptions.MinKeyBytes };

    [GeneratedRegex("^k[1-9][0-9]{0,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();
}
