namespace Cadastro.Application.Users;

/// <summary>Porta de leitura dos usuários no provedor de identidade (Keycloak), implementada na Infrastructure.</summary>
public interface IIdentityUserReadService
{
    /// <summary>
    /// Busca o perfil pelo id técnico; <c>null</c> quando o usuário não existe mais.
    /// Lança <see cref="IdentityProviderUnavailableException"/> se o provedor não responder.
    /// </summary>
    Task<MyProfile?> FindProfileAsync(string userId, CancellationToken cancellationToken);
}

/// <summary>O provedor de identidade não respondeu ou respondeu com erro (falha transitória, vira 503).</summary>
public sealed class IdentityProviderUnavailableException : Exception
{
    /// <summary>Cria a exceção sem detalhe.</summary>
    public IdentityProviderUnavailableException()
    {
    }

    /// <summary>Cria a exceção com a mensagem técnica.</summary>
    public IdentityProviderUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção preservando a causa (timeout, erro HTTP).</summary>
    public IdentityProviderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
