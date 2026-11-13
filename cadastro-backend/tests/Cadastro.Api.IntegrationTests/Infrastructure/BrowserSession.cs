using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cadastro.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Simula o navegador no fluxo OIDC: chama o BFF (TestServer, com cookies) e o Keycloak real (tela de login e envio
/// das credenciais), seguindo os redirecionamentos à mão para os testes inspecionarem cada passo.
/// </summary>
public sealed partial class BrowserSession : IDisposable
{
    private const int MaxKeycloakRedirects = 5;

    private readonly HttpClient _keycloak = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    /// <summary>Cria a sessão sobre a API informada; o endereço https faz o cliente aceitar os cookies <c>Secure</c>.</summary>
    public BrowserSession(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Bff = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });
    }

    /// <summary>Cliente do BFF com o cookie jar da sessão.</summary>
    public HttpClient Bff { get; }

    /// <summary>Faz o login completo e devolve a resposta do callback do BFF (302 para o destino).</summary>
    public async Task<HttpResponseMessage> SignInAsync(string userName, string password, string returnUrl = "/")
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var login = await Bff.GetAsync(new Uri($"/bff/login?returnUrl={Uri.EscapeDataString(returnUrl)}", UriKind.Relative), cancellation);
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // O Keycloak redireciona dentro dele mesmo (auth → login-actions) antes de mostrar a tela, como no navegador.
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        var next = login.Headers.Location!;
        HttpResponseMessage page;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            AddCookies(request, cookies);
            page = await _keycloak.SendAsync(request, cancellation);
            CollectCookies(page, cookies);
            if (page.StatusCode != HttpStatusCode.Found || hop == MaxKeycloakRedirects)
            {
                break;
            }

            next = new Uri(next, page.Headers.Location!);
            page.Dispose();
        }

        using var _ = page;
        var html = await page.Content.ReadAsStringAsync(cancellation);
        var formTag = LoginFormTag().Match(html).Value;
        var action = WebUtility.HtmlDecode(FormAction().Match(formTag).Groups[1].Value);
        action.ShouldNotBeNullOrEmpty($"a tela de login do Keycloak não foi exibida ({(int)page.StatusCode} {page.Headers.Location}): {html[..Math.Min(html.Length, 400)]}");

        // Os cookies do Keycloak têm "Secure": um navegador os envia para http://localhost, o HttpClient não.
        using var submit = new HttpRequestMessage(HttpMethod.Post, action)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = userName, ["password"] = password }),
        };
        AddCookies(submit, cookies);
        using var submitted = await _keycloak.SendAsync(submit, cancellation);
        submitted.StatusCode.ShouldBe(HttpStatusCode.Found, "o Keycloak recusou as credenciais de teste");

        return await Bff.GetAsync(new Uri(submitted.Headers.Location!.PathAndQuery, UriKind.Relative), cancellation);
    }

    /// <summary>Consulta <c>/bff/user</c> e devolve o token anti-CSRF da sessão atual (vem no corpo).</summary>
    public async Task<string> GetAntiforgeryTokenAsync()
    {
        var user = await Bff.GetFromJsonAsync<Cadastro.Api.Auth.BffUserResponse>(
            new Uri("/bff/user", UriKind.Relative), TestContext.Current.CancellationToken);
        return user!.AntiforgeryToken;
    }

    /// <summary>Valor completo do <c>Set-Cookie</c> do cookie informado, ou <c>null</c>.</summary>
    public static string? SetCookie(HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal))
            : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _keycloak.Dispose();
        Bff.Dispose();
    }

    private static void CollectCookies(HttpResponseMessage response, Dictionary<string, string> cookies)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        foreach (var pair in values.Select(value => value.Split(';')[0].Split('=', 2)))
        {
            cookies[pair[0]] = pair.Length > 1 ? pair[1] : string.Empty;
        }
    }

    private static void AddCookies(HttpRequestMessage request, Dictionary<string, string> cookies)
    {
        if (cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
        }
    }

    [GeneratedRegex("<form[^>]*id=\"kc-form-login\"[^>]*>")]
    private static partial Regex LoginFormTag();

    [GeneratedRegex("action=\"([^\"]+)\"")]
    private static partial Regex FormAction();
}
