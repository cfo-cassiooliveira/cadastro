namespace Cadastro.Api.Auth;

/// <summary>Valida o destino pós-login contra open redirect (OWASP A01): só caminhos locais da própria aplicação.</summary>
internal static class LocalReturnUrl
{
    /// <summary>Aceita só "/caminho"; recusa URL absoluta, "//host" e "/\host" (que navegadores tratam como outro host).</summary>
    public static bool IsLocal(string url) =>
        url.Length > 0
        && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.Any(char.IsControl);
}
