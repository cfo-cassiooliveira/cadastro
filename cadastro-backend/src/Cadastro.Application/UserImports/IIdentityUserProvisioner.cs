using Cadastro.Application.Users;

namespace Cadastro.Application.UserImports;

/// <summary>Pessoa a criar no provedor de identidade a partir de uma linha do lote (dados já decifrados; nunca vão a log).</summary>
/// <param name="LineId">Id da linha: vira o atributo <c>importLineId</c> e torna a criação idempotente.</param>
/// <param name="Name">Nome completo.</param>
/// <param name="Email">E-mail.</param>
/// <param name="Phone">Telefone como veio no arquivo; <c>null</c> quando não informado.</param>
public sealed record ProvisionUserRequest(Guid LineId, string Name, string Email, string? Phone);

/// <summary>Resultado de provisionar uma pessoa.</summary>
public enum ProvisionOutcome
{
    /// <summary>Usuário criado por esta linha (ou criado antes por ela, numa tentativa interrompida) e e-mail de ativação enviado.</summary>
    Created = 0,

    /// <summary>Já existia um usuário com o mesmo e-mail que não veio desta linha; nada foi criado nem enviado.</summary>
    AlreadyExists = 1,
}

/// <summary>Usuário resultante do provisionamento.</summary>
/// <param name="UserId">Id do usuário no provedor de identidade (Keycloak).</param>
/// <param name="Outcome">Se foi criado por esta linha ou já existia.</param>
public sealed record ProvisionedUser(string UserId, ProvisionOutcome Outcome);

/// <summary>
/// Porta de criação de usuários da importação. Cria o usuário sem senha (ação obrigatória <c>UPDATE_PASSWORD</c>) e dispara o
/// e-mail de ativação com validade de 72 h. Falha transitória lança <see cref="Users.IdentityProviderUnavailableException"/>;
/// recusa definitiva lança <see cref="IdentityUserRejectedException"/>.
/// </summary>
public interface IIdentityUserProvisioner
{
    /// <summary>Cria (ou reconhece) o usuário da linha e garante o e-mail de ativação.</summary>
    Task<ProvisionedUser> ProvisionAsync(ProvisionUserRequest request, CancellationToken cancellationToken);
}

/// <summary>O provedor de identidade recusou o usuário de forma definitiva (validação, conflito): repetir não adianta.</summary>
public sealed class IdentityUserRejectedException : Exception
{
    /// <summary>Cria a exceção sem detalhe.</summary>
    public IdentityUserRejectedException()
        : this(ImportFailureCodes.IdentityRejected)
    {
    }

    /// <summary>Cria a exceção com o código do motivo (nunca texto livre nem dado pessoal).</summary>
    public IdentityUserRejectedException(string code)
        : base($"The identity provider rejected the user ({code}).") => Code = code;

    /// <summary>Cria a exceção com o código e a causa.</summary>
    public IdentityUserRejectedException(string code, Exception innerException)
        : base($"The identity provider rejected the user ({code}).", innerException) => Code = code;

    /// <summary>Código do motivo, gravado em <c>FailureReason</c> da linha.</summary>
    public string Code { get; } = ImportFailureCodes.IdentityRejected;
}

/// <summary>
/// O provedor de identidade recusou as credenciais ou a permissão do importador (segredo errado, role removida, scope ausente).
/// É falha operacional, não da linha: a mensagem volta à retry <b>sem consumir um ciclo</b>, para o problema de configuração
/// não transformar o lote inteiro em erro enquanto alguém corrige.
/// </summary>
public sealed class IdentityAccessDeniedException : IdentityProviderUnavailableException
{
    /// <summary>Cria a exceção sem detalhe.</summary>
    public IdentityAccessDeniedException()
    {
    }

    /// <summary>Cria a exceção com a mensagem técnica.</summary>
    public IdentityAccessDeniedException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção preservando a causa.</summary>
    public IdentityAccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
