using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Cadastro.Api.Auth;

/// <summary>
/// Mantém a sessão do BFF alinhada à do Keycloak: quando o access token vence, renova pelo refresh token.
/// Se o Keycloak recusar (sessão encerrada, usuário desativado) ou não responder, a sessão local termina (falha fechada).
/// Requisições simultâneas da mesma sessão compartilham uma única renovação: o realm não aceita reuso do refresh
/// token, e uma segunda renovação com o token antigo encerraria a sessão sem motivo.
/// </summary>
internal sealed partial class SessionTokenRefresher(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    TimeProvider timeProvider,
    ILogger<SessionTokenRefresher> logger)
{
    /// <summary>Nome do HttpClient usado no token endpoint.</summary>
    public const string HttpClientName = "keycloak-token";

    // Renova um pouco antes do vencimento para o token não expirar no meio da requisição.
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

    // Quanto tempo o resultado de uma renovação fica disponível para requisições que leram a sessão antes dela.
    private static readonly TimeSpan SharedRefreshLifetime = TimeSpan.FromSeconds(30);

    // Chave: hash do refresh token usado (o token em si não fica guardado como chave).
    private readonly ConcurrentDictionary<string, SharedRefresh> _refreshes = new(StringComparer.Ordinal);

    /// <summary>Evento <c>OnValidatePrincipal</c> do cookie: renova os tokens vencidos ou encerra a sessão.</summary>
    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var expiresAt = context.Properties.GetTokenValue("expires_at");
        if (expiresAt is not null
            && DateTimeOffset.TryParse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires)
            && timeProvider.GetUtcNow() < expires - RefreshMargin)
        {
            return;
        }

        var userId = context.Principal?.FindFirst(SessionPrincipal.SubjectClaim)?.Value ?? "unknown";
        var refreshed = await RefreshOnceAsync(context.Properties.GetTokenValue("refresh_token"), userId);
        if (refreshed is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        context.Properties.UpdateTokenValue("access_token", refreshed.AccessToken);
        context.Properties.UpdateTokenValue("refresh_token", refreshed.RefreshToken);
        context.Properties.UpdateTokenValue("expires_at",
            timeProvider.GetUtcNow().AddSeconds(refreshed.ExpiresIn).ToString("o", CultureInfo.InvariantCulture));
        context.ShouldRenew = true;
    }

    // Uma renovação por refresh token: quem chega junto (ou logo depois, com a sessão lida antes) aguarda e reaproveita
    // o mesmo resultado. Sem o token de cancelamento da requisição: uma aba fechada não cancela a renovação das outras.
    private Task<TokenResponse?> RefreshOnceAsync(string? refreshToken, string userId)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            LogRefreshRejected(userId, 0);
            return Task.FromResult<TokenResponse?>(null);
        }

        var now = timeProvider.GetUtcNow();
        foreach (var expired in _refreshes.Where(entry => now - entry.Value.StartedAt > SharedRefreshLifetime).ToList())
        {
            _refreshes.TryRemove(expired);
        }

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
        var shared = _refreshes.GetOrAdd(key, _ => new SharedRefresh(
            now, new Lazy<Task<TokenResponse?>>(() => TryRefreshAsync(refreshToken, userId, CancellationToken.None))));
        return shared.Result.Value;
    }

    private async Task<TokenResponse?> TryRefreshAsync(string refreshToken, string userId, CancellationToken cancellationToken)
    {

        var options = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        try
        {
            var configuration = await options.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
            using var request = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = options.ClientId!,
                ["client_secret"] = options.ClientSecret!,
            });
            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .PostAsync(new Uri(configuration.TokenEndpoint), request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogRefreshRejected(userId, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            LogRefreshFailed(exception, userId);
            return null;
        }
    }

    /// <summary>Renovação em andamento (ou recém-concluída) compartilhada pelas requisições da mesma sessão.</summary>
    /// <param name="StartedAt">Quando a renovação começou (para descartar depois de <see cref="SharedRefreshLifetime"/>).</param>
    /// <param name="Result">Resultado único da renovação.</param>
    private sealed record SharedRefresh(DateTimeOffset StartedAt, Lazy<Task<TokenResponse?>> Result);

    /// <summary>Resposta do token endpoint no refresh.</summary>
    /// <param name="AccessToken">Access token novo.</param>
    /// <param name="RefreshToken">Refresh token novo (rotação ativa no realm).</param>
    /// <param name="ExpiresIn">Validade do access token em segundos.</param>
    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in session of user {UserId} ended: token refresh rejected with status {StatusCode}")]
    private partial void LogRefreshRejected(string userId, int statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sign-in session of user {UserId} ended: identity provider unreachable during token refresh")]
    private partial void LogRefreshFailed(Exception exception, string userId);
}
