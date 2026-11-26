namespace Cadastro.Infrastructure.Protection;

/// <summary>
/// Chaves AES-256 dos dados pessoais da importação, lidas de <c>PersonalData:Protection</c> (variável de ambiente ou
/// user-secrets; nunca versionadas). Ex.: <c>PersonalData__Protection__Keys__k1</c>.
/// </summary>
public sealed class PersonalDataProtectionOptions
{
    /// <summary>Nome da seção de configuração.</summary>
    public const string SectionName = "PersonalData:Protection";

    /// <summary>Tamanho exigido da chave, em bytes (AES-256).</summary>
    public const int KeySizeBytes = 32;

    /// <summary>Identificador da chave que cifra novos valores (ex.: <c>k1</c>).</summary>
    public string CurrentKeyVersion { get; set; } = string.Empty;

    /// <summary>Chaves em Base64 por identificador; versões antigas permanecem para decifrar (sem rotação automática no MVP).</summary>
    public Dictionary<string, string> Keys { get; set; } = [];
}
