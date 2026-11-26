namespace Cadastro.Domain.UserImports;

/// <summary>Resultado da marcação de login migrado no Legado (passo posterior à criação no Keycloak).</summary>
public enum LegacyMarkStatus
{
    /// <summary>Marcação ainda não tentada.</summary>
    NotAttempted = 0,

    /// <summary>Login do Legado marcado como migrado.</summary>
    Marked = 1,

    /// <summary>Nenhum login do Legado com este e-mail (não é erro).</summary>
    NoCandidate = 2,

    /// <summary>Mais de um candidato: nada foi marcado e a linha ganha um aviso.</summary>
    Ambiguous = 3,

    /// <summary>A API do Legado falhou; o usuário do Keycloak permanece.</summary>
    Failed = 4,
}
