using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Protection;

/// <summary>Falha a subida quando a chave atual falta ou não é uma chave AES-256 em Base64 (fail fast, sem cifrar com chave fraca).</summary>
internal sealed class PersonalDataProtectionOptionsValidator : IValidateOptions<PersonalDataProtectionOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PersonalDataProtectionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.CurrentKeyVersion))
        {
            return ValidateOptionsResult.Fail("PersonalData:Protection:CurrentKeyVersion is required.");
        }

        foreach (var (version, value) in options.Keys)
        {
            if (!TryDecode(value, out _))
            {
                return ValidateOptionsResult.Fail($"PersonalData:Protection:Keys:{version} must be a Base64 string of {PersonalDataProtectionOptions.KeySizeBytes} bytes.");
            }
        }

        return options.Keys.ContainsKey(options.CurrentKeyVersion)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"PersonalData:Protection:Keys:{options.CurrentKeyVersion} (current key) is missing.");
    }

    /// <summary>Decodifica uma chave Base64 e confere o tamanho de AES-256.</summary>
    internal static bool TryDecode(string? value, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            key = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            // Valor inválido: o resultado false é tratado pelo chamador, sem expor o conteúdo da chave.
            return false;
        }

        return key.Length == PersonalDataProtectionOptions.KeySizeBytes;
    }
}
