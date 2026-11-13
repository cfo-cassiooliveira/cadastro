using System.ComponentModel.DataAnnotations;

namespace Cadastro.Api.Auth;

/// <summary>Configuração do login OIDC do BFF no Keycloak (seção <c>Oidc</c>). O segredo vem só de variável de ambiente.</summary>
public sealed class BffOidcOptions
{
    /// <summary>Seção de configuração (<c>Oidc</c>).</summary>
    public const string SectionName = "Oidc";

    /// <summary>Emissor dos tokens, como o navegador o vê (ex.: <c>http://localhost:8180/realms/workspace</c>).</summary>
    [Required]
    public string Authority { get; init; } = string.Empty;

    /// <summary>
    /// Endereço do discovery pela rede interna (ex.: <c>http://keycloak:8080/...</c>); vazio usa o da <see cref="Authority"/>.
    /// </summary>
    public string? MetadataAddress { get; init; }

    /// <summary>Client confidencial do BFF no realm.</summary>
    [Required]
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Segredo do client (nunca versionado: user-secrets ou variável de ambiente).</summary>
    [Required]
    public string ClientSecret { get; init; } = string.Empty;

    /// <summary>
    /// Origem pública do Cadastro (ex.: <c>http://localhost:4300</c>): monta o redirect URI exato registrado no Keycloak,
    /// sem depender do Host que chega pelo proxy.
    /// </summary>
    [Required]
    public string PublicOrigin { get; init; } = string.Empty;

    /// <summary>Exige HTTPS no discovery; só o ambiente local desliga.</summary>
    public bool RequireHttpsMetadata { get; init; } = true;
}
