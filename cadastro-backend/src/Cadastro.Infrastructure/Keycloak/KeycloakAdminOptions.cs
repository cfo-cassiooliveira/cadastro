using System.ComponentModel.DataAnnotations;

namespace Cadastro.Infrastructure.Keycloak;

/// <summary>Acesso à Admin API do Keycloak pela conta de serviço do client <c>cadastro</c> (seção <c>KeycloakAdmin</c>).</summary>
public sealed class KeycloakAdminOptions
{
    /// <summary>Seção de configuração (<c>KeycloakAdmin</c>).</summary>
    public const string SectionName = "KeycloakAdmin";

    /// <summary>Endereço do Keycloak pela rede interna (ex.: <c>http://keycloak:8080</c>).</summary>
    [Required]
    public Uri? BaseUrl { get; init; }

    /// <summary>Realm dos usuários.</summary>
    [Required]
    public string Realm { get; init; } = "workspace";

    /// <summary>Client confidencial com a conta de serviço.</summary>
    [Required]
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Segredo do client (nunca versionado).</summary>
    [Required]
    public string ClientSecret { get; init; } = string.Empty;
}
