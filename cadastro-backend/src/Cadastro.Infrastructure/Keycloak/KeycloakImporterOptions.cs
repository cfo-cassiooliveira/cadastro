using System.ComponentModel.DataAnnotations;

namespace Cadastro.Infrastructure.Keycloak;

/// <summary>
/// Conta de serviço do client <c>cadastro-importer</c> (seção <c>KeycloakImporter</c>), usada só para criar usuários da
/// importação e enviar o e-mail de ativação. Endereço e realm vêm de <see cref="KeycloakAdminOptions"/>.
/// </summary>
public sealed class KeycloakImporterOptions
{
    /// <summary>Seção de configuração (<c>KeycloakImporter</c>).</summary>
    public const string SectionName = "KeycloakImporter";

    /// <summary>Client confidencial do importador.</summary>
    [Required]
    public string ClientId { get; init; } = "cadastro-importer";

    /// <summary>Segredo do client (<c>KC_CADASTRO_IMPORTER_CLIENT_SECRET</c>); nunca versionado.</summary>
    [Required]
    public string ClientSecret { get; init; } = string.Empty;

    /// <summary>Máximo de criações de usuário por segundo (protege a Admin API).</summary>
    [Range(1, 1000)]
    public int MaxCreationsPerSecond { get; init; } = 10;
}
