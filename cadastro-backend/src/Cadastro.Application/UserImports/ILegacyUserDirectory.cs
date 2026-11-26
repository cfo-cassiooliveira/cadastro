using System.Text;
using Cadastro.Domain.UserImports;

namespace Cadastro.Application.UserImports;

/// <summary>Conta do Legado devolvida pela consulta por e-mail.</summary>
/// <param name="Id">Id da conta no Legado.</param>
/// <param name="Email">E-mail da conta.</param>
public sealed record LegacyUserCandidate(Guid Id, string Email);

/// <summary>
/// Porta da API interna do Legado (consulta por e-mail e marcação de conta migrada). Falha de rede, timeout, circuito aberto
/// ou resposta inesperada lançam <see cref="LegacyDirectoryUnavailableException"/>.
/// </summary>
public interface ILegacyUserDirectory
{
    /// <summary>Contas do Legado encontradas para o e-mail (0 ou mais; o contrato atual devolve no máximo uma).</summary>
    Task<IReadOnlyList<LegacyUserCandidate>> FindByEmailAsync(string email, CancellationToken cancellationToken);

    /// <summary>Marca a conta do Legado como migrada para o usuário do Keycloak (idempotente).</summary>
    Task MarkMigratedAsync(Guid legacyUserId, string keycloakUserId, CancellationToken cancellationToken);
}

/// <summary>A API interna do Legado não respondeu como esperado.</summary>
public sealed class LegacyDirectoryUnavailableException : Exception
{
    /// <summary>Cria a exceção sem detalhe.</summary>
    public LegacyDirectoryUnavailableException()
    {
    }

    /// <summary>Cria a exceção com a mensagem técnica.</summary>
    public LegacyDirectoryUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção preservando a causa.</summary>
    public LegacyDirectoryUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Decisão sobre marcar o Legado, a partir dos candidatos encontrados.</summary>
/// <param name="Status">Estado a registrar quando não há marcação (<see cref="LegacyMarkStatus.NoCandidate"/> ou <see cref="LegacyMarkStatus.Ambiguous"/>).</param>
/// <param name="Target">Conta a marcar; <c>null</c> quando não há marcação.</param>
public sealed record LegacyMarkDecision(LegacyMarkStatus Status, LegacyUserCandidate? Target)
{
    /// <summary>
    /// Marca só com UM candidato de e-mail exatamente igual (aparado, NFC, sem diferença de caixa): zero vira
    /// <see cref="LegacyMarkStatus.NoCandidate"/> (não é erro) e vários viram <see cref="LegacyMarkStatus.Ambiguous"/>.
    /// Candidato cujo e-mail difere (achado por login, por exemplo) não conta.
    /// </summary>
    public static LegacyMarkDecision Decide(string email, IReadOnlyList<LegacyUserCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(candidates);
        var key = Normalize(email);
        var exact = candidates.Where(c => Normalize(c.Email) == key).ToList();
        return exact.Count switch
        {
            0 => new LegacyMarkDecision(LegacyMarkStatus.NoCandidate, null),
            1 => new LegacyMarkDecision(LegacyMarkStatus.Marked, exact[0]),
            _ => new LegacyMarkDecision(LegacyMarkStatus.Ambiguous, null),
        };
    }

    private static string Normalize(string email) => email.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();
}
