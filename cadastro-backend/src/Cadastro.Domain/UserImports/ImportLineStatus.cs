namespace Cadastro.Domain.UserImports;

/// <summary>Estado de processamento de uma linha (uma pessoa) do lote.</summary>
public enum ImportLineStatus
{
    /// <summary>Aguardando processamento.</summary>
    Pending = 0,

    /// <summary>Usuário criado no Keycloak.</summary>
    Created = 1,

    /// <summary>A pessoa já existia; nada foi criado.</summary>
    AlreadyExists = 2,

    /// <summary>Falha definitiva; o motivo fica em <see cref="ImportLine.FailureReason"/>.</summary>
    Error = 3,

    /// <summary>Cancelada junto com o lote antes da confirmação; os dados pessoais foram apagados.</summary>
    Cancelled = 4,
}
