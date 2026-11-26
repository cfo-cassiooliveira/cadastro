using System.ComponentModel.DataAnnotations;

namespace Cadastro.Infrastructure.Legado;

/// <summary>
/// API interna do Legado (seção <c>Legado</c>): HTTPS na porta interna 8443 confiando só na CA do Legado, com requisições
/// assinadas por HMAC (ADR-0005 do Legado). A chave nunca é versionada.
/// </summary>
public sealed class LegadoOptions
{
    /// <summary>Seção de configuração (<c>Legado</c>).</summary>
    public const string SectionName = "Legado";

    /// <summary>Endereço da API interna (ex.: <c>https://legado-api:8443/</c>); só <c>https</c> é aceito.</summary>
    [Required]
    public Uri? BaseUrl { get; init; }

    /// <summary>Id da chave do Cadastro no Legado (ex.: <c>cadastro-1</c>).</summary>
    [Required]
    public string KeyId { get; init; } = "cadastro-1";

    /// <summary>Chave HMAC em Base64 (<c>LEGADO_INTERNAL_CADASTRO_KEY</c> do Legado), mínimo de 32 bytes.</summary>
    [Required]
    public string SigningKey { get; init; } = string.Empty;

    /// <summary>Arquivo PEM da CA do Legado: única âncora de confiança da conexão.</summary>
    [Required]
    public string CaCertificatePath { get; init; } = string.Empty;
}
