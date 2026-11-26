using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Cadastro.Application.UserImports;
using Cadastro.Application.Users;
using Microsoft.Extensions.Options;
using Polly;

namespace Cadastro.Infrastructure.Keycloak;

/// <summary>
/// Token da conta de serviço (client credentials, <c>scope=kc-admin-api</c>) para a Admin API, reaproveitado até
/// 30 s antes de vencer: evita um pedido de token por requisição.
/// </summary>
internal sealed class KeycloakServiceTokenProvider : IDisposable
{
    /// <summary>Nome do HttpClient (com resiliência) usado no token endpoint.</summary>
    public const string HttpClientName = "keycloak-service-token";

    private static readonly TimeSpan RenewMargin = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<ServiceCredentials> _credentials;
    private readonly TimeProvider _timeProvider;

    /// <summary>Cria o provedor com as credenciais do client <c>cadastro</c> (leitura de perfis).</summary>
    public KeycloakServiceTokenProvider(IHttpClientFactory httpClientFactory, IOptions<KeycloakAdminOptions> options, TimeProvider timeProvider)
        : this(httpClientFactory, () => new ServiceCredentials(options.Value.Realm, options.Value.ClientId, options.Value.ClientSecret), timeProvider)
    {
    }

    /// <summary>Cria o provedor com credenciais explícitas (ex.: client <c>cadastro-importer</c>).</summary>
    public KeycloakServiceTokenProvider(IHttpClientFactory httpClientFactory, Func<ServiceCredentials> credentials, TimeProvider timeProvider)
    {
        _httpClientFactory = httpClientFactory;
        _credentials = credentials;
        _timeProvider = timeProvider;
    }

    /// <summary>Realm, client e segredo da conta de serviço.</summary>
    /// <param name="Realm">Realm.</param>
    /// <param name="ClientId">Client confidencial.</param>
    /// <param name="ClientSecret">Segredo (nunca logado).</param>
    public sealed record ServiceCredentials(string Realm, string ClientId, string ClientSecret);

    /// <summary>Devolve um token válido, pedindo outro ao Keycloak só quando o atual está perto de vencer.</summary>
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && _timeProvider.GetUtcNow() < _expiresAt)
        {
            return _token;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_token is null || _timeProvider.GetUtcNow() >= _expiresAt)
            {
                var response = await RequestTokenAsync(cancellationToken);
                _token = response.AccessToken;
                _expiresAt = _timeProvider.GetUtcNow().AddSeconds(response.ExpiresIn) - RenewMargin;
            }

            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Descarta o token em cache (o Keycloak o recusou antes de vencer); o próximo pedido busca outro.</summary>
    public void Invalidate()
    {
        _lock.Wait();
        try
        {
            _token = null;
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
        var settings = _credentials();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = settings.ClientId,
            ["client_secret"] = settings.ClientSecret,
            ["scope"] = "kc-admin-api",
        });
        try
        {
            using var response = await _httpClientFactory.CreateClient(HttpClientName).PostAsync(
                new Uri($"realms/{settings.Realm}/protocol/openid-connect/token", UriKind.Relative), form, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // 400/401/403 no token endpoint = segredo errado ou scope removido: falha de configuração, não transitória.
                if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    throw new IdentityAccessDeniedException($"Service account token request was refused with status {(int)response.StatusCode}.");
                }

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
