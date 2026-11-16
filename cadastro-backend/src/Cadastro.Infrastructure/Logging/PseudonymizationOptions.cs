namespace Cadastro.Infrastructure.Logging;

/// <summary>
/// Chaves HMAC da pseudonimização dos logs (ADR-0003 do graylog), em <c>Logging:Pseudonymization</c>. A mesma chave
/// por ambiente no cadastro e no legado; valores só em user-secrets/variável de ambiente, nunca no repositório.
/// </summary>
internal sealed class PseudonymizationOptions
{
    /// <summary>Seção de configuração (<c>Logging:Pseudonymization</c>).</summary>
    public const string SectionName = "Logging:Pseudonymization";

    /// <summary>Tamanho mínimo da chave: 256 bits.</summary>
    public const int MinKeyBytes = 32;

    /// <summary>
    /// Id da chave usada para emitir tokens (<c>k1</c>, <c>k2</c>...). Na rotação anual passa a <c>k(N+1)</c>; o Cadastro,
    /// que pesquisa, mantém a chave anterior em <see cref="Keys"/> até o fim da retenção dos logs (30 dias).
    /// </summary>
    public string CurrentKeyId { get; set; } = string.Empty;

    /// <summary>Id da chave → chave em Base64 (ex.: <c>Logging__Pseudonymization__Keys__k1</c>). Valor vazio = chave ausente.</summary>
    public Dictionary<string, string> Keys { get; init; } = new(StringComparer.Ordinal);
}
