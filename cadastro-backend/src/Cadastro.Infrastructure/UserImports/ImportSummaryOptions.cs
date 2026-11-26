using Cadastro.Application.UserImports;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.UserImports;

/// <summary>Opções da importação em lote (<c>UserImports:*</c>). A chave nunca é versionada: só ambiente, user-secrets ou <c>.env</c>.</summary>
public sealed class ImportSummaryOptions
{
    /// <summary>Nome da seção de configuração.</summary>
    public const string SectionName = "UserImports";

    /// <summary>Chave HMAC (Base64, no mínimo 32 bytes) que assina o token do resumo do lote.</summary>
    public string SummaryTokenKey { get; set; } = string.Empty;
}

/// <summary>Falha a subida quando a chave do token do resumo falta ou é curta (fail fast, como a chave dos dados pessoais).</summary>
internal sealed class ImportSummaryOptionsValidator : IValidateOptions<ImportSummaryOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ImportSummaryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return TryDecode(options.SummaryTokenKey, out _)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"UserImports:SummaryTokenKey must be a Base64 string of at least {HmacImportSummarySigner.MinKeyBytes} bytes.");
    }

    /// <summary>Decodifica a chave e confere o tamanho mínimo; nunca expõe o valor.</summary>
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
            // Valor inválido: o chamador trata o false, sem repetir o conteúdo da chave.
            return false;
        }

        return key.Length >= HmacImportSummarySigner.MinKeyBytes;
    }
}
