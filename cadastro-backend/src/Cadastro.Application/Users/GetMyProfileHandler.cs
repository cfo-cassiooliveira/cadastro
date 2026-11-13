namespace Cadastro.Application.Users;

/// <summary>Caso de uso "Meu perfil": devolve o perfil do usuário da própria sessão (US-06).</summary>
public interface IGetMyProfileHandler
{
    /// <summary>Busca o perfil do usuário autenticado; <c>null</c> se ele não existe mais no Keycloak.</summary>
    Task<MyProfile?> HandleAsync(string userId, CancellationToken cancellationToken);
}

/// <summary>
/// Lê o perfil pela porta <see cref="IIdentityUserReadService"/>. O id vem só da sessão (sem parâmetro na rota),
/// então não há como pedir o perfil de outra pessoa (IDOR).
/// </summary>
internal sealed class GetMyProfileHandler(IIdentityUserReadService users) : IGetMyProfileHandler
{
    /// <inheritdoc />
    public Task<MyProfile?> HandleAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return users.FindProfileAsync(userId, cancellationToken);
    }
}
