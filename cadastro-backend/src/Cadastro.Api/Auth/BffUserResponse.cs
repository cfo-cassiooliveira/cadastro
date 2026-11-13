namespace Cadastro.Api.Auth;

/// <summary>Sessão atual vista pela SPA: só o necessário para montar o cabeçalho e os guards (sem token OAuth, sem dado pessoal).</summary>
/// <param name="IsAuthenticated">Se há sessão ativa.</param>
/// <param name="UserName">Login do usuário (ausente para visitante).</param>
/// <param name="Roles">Roles do realm (<c>admin</c>, <c>user</c>).</param>
/// <param name="AntiforgeryToken">
/// Token anti-CSRF ligado à sessão atual: a SPA o guarda em memória e o envia no header <c>X-XSRF-TOKEN</c> nas chamadas
/// que mudam estado. Muda ao entrar ou sair, por isso a SPA relê a sessão nesses momentos.
/// </param>
public sealed record BffUserResponse(bool IsAuthenticated, string? UserName, IReadOnlyList<string> Roles, string AntiforgeryToken)
{
    /// <summary>Resposta para visitante sem sessão.</summary>
    public static BffUserResponse Anonymous(string antiforgeryToken) => new(false, null, [], antiforgeryToken);
}

/// <summary>Resposta do logout: URL do Keycloak para onde o navegador segue e encerra a sessão de SSO.</summary>
/// <param name="LogoutUrl">End session endpoint com <c>id_token_hint</c>, redirect de volta e <c>state</c>.</param>
public sealed record LogoutResponse(string LogoutUrl);
