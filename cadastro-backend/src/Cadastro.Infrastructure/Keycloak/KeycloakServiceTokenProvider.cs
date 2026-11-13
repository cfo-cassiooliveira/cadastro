using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Cadastro.Application.Users;
using Microsoft.Extensions.Options;
using Polly;

namespace Cadastro.Infrastructure.Keycloak;

/// <summary>
/// Token da conta de serviço (client credentials, <c>scope=kc-admin-api</c>) para a Admin API, reaproveitado até
/// 30 s antes de vencer: evita um pedido de token por requisição.
/// </summary>
internal sealed class KeycloakServiceTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<KeycloakAdminOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    /// <summary>Nome do HttpClient (com resiliência) usado no token endpoint.</summary>
    public const string HttpClientName = "keycloak-service-token";

    private static readonly TimeSpan RenewMargin = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    /// <summary>Devolve um token válido, pedindo outro ao Keycloak só quando o atual está perto de vencer.</summary>
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && timeProvider.GetUtcNow() < _expiresAt)
        {
            return _token;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_token is null || timeProvider.GetUtcNow() >= _expiresAt)
            {
                var response = await RequestTokenAsync(cancellationToken);
                _token = response.AccessToken;
                _expiresAt = timeProvider.GetUtcNow().AddSeconds(response.ExpiresIn) - RenewMargin;
            }

            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Libera o semáforo.</summary>
    public void Dispose() => _lock.Dispose();

    private async Task<TokenResponse> RequestTokenAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = settings.ClientId,
            ["client_secret"] = settings.ClientSecret,
            ["scope"] = "kc-admin-api",
        });
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).PostAsync(
                new Uri($"realms/{settings.Realm}/protocol/openid-connect/token", UriKind.Relative), form, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new IdentityProviderUnavailableException(
                    $"Service account token request failed with status {(int)response.StatusCode}.");
            }

            return await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                ?? throw new IdentityProviderUnavailableException("Empty service account token response.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ExecutionRejectedException
                                              && !cancellationToken.IsCancellationRequested)
        {
            throw new IdentityProviderUnavailableException("Keycloak token endpoint unreachable.", exception);
        }
    }

    /// <summary>Resposta do token endpoint.</summary>
    /// <param name="AccessToken">Access token da conta de serviço.</param>
    /// <param name="ExpiresIn">Validade em segundos.</param>
    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
