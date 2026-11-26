namespace Cadastro.Application.UserImports;

/// <summary>
/// Divisão do nome completo da planilha em nome e sobrenome e os limites de tamanho de cada parte no User Profile do Keycloak
/// (<c>workspace-user-profile.json</c>). O upload usa os mesmos limites para recusar o nome antes da confirmação em vez de
/// deixar o Keycloak recusá-lo (400) só na criação.
/// </summary>
public static class PersonNameRules
{
    /// <summary>Tamanho máximo do primeiro nome (<c>firstName</c> do User Profile).</summary>
    public const int FirstNameMaxLength = 100;

    /// <summary>Tamanho máximo do sobrenome (<c>lastName</c> do User Profile).</summary>
    public const int LastNameMaxLength = 150;

    /// <summary>Divide o nome completo no primeiro espaço: o que vem antes é o nome, o resto é o sobrenome.</summary>
    public static (string FirstName, string LastName) Split(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        var trimmed = fullName.Trim();
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (trimmed, string.Empty) : (trimmed[..space], trimmed[(space + 1)..].Trim());
    }

    /// <summary>Indica se o nome ou o sobrenome, depois da divisão, passa do limite do Keycloak.</summary>
    public static bool ExceedsKeycloakLimits(string fullName)
    {
        var (firstName, lastName) = Split(fullName);
        return firstName.Length > FirstNameMaxLength || lastName.Length > LastNameMaxLength;
    }
}
