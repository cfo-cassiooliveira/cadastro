using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Cadastro.Application.Users;
using Microsoft.Extensions.Options;
using Polly;

namespace Cadastro.Infrastructure.Keycloak;

/// <summary>
/// Lê usuários na Admin API do Keycloak com a conta de serviço do Cadastro e converte a representação do Keycloak
/// (atributos multivalorados do User Profile) para o modelo da aplicação.
/// </summary>
internal sealed class KeycloakAdminClient(
    HttpClient httpClient,
    KeycloakServiceTokenProvider tokenProvider,
    IOptions<KeycloakAdminOptions> options) : IIdentityUserReadService
{
    /// <summary>Busca o usuário pelo id; 404 vira <c>null</c> e qualquer outra falha vira 503 (Keycloak indisponível).</summary>
    public async Task<MyProfile?> FindProfileAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri($"admin/realms/{options.Value.Realm}/users/{Uri.EscapeDataString(userId)}", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokenProvider.GetTokenAsync(cancellationToken));
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new IdentityProviderUnavailableException($"Keycloak Admin API answered {(int)response.StatusCode}.");
            }

            var user = await response.Content.ReadFromJsonAsync<KeycloakUser>(cancellationToken)
                ?? throw new IdentityProviderUnavailableException("Empty user representation.");
            return user.ToProfile();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ExecutionRejectedException
                                              && !cancellationToken.IsCancellationRequested)
        {
            throw new IdentityProviderUnavailableException("Keycloak Admin API unreachable.", exception);
        }
    }

    /// <summary>Representação de usuário da Admin API (só os campos usados).</summary>
    /// <param name="UserName">Login.</param>
    /// <param name="FirstName">Primeiro nome.</param>
    /// <param name="LastName">Sobrenome.</param>
    /// <param name="Email">E-mail.</param>
    /// <param name="Attributes">Atributos do User Profile (cada um é uma lista de valores).</param>
    private sealed record KeycloakUser(
        [property: JsonPropertyName("username")] string UserName,
        [property: JsonPropertyName("firstName")] string? FirstName,
        [property: JsonPropertyName("lastName")] string? LastName,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("attributes")] Dictionary<string, string[]>? Attributes)
    {
        public MyProfile ToProfile()
        {
            var postalCode = Attribute("postalCode");
            var address = postalCode is null
                ? null
                : new ProfileAddress(
                    postalCode,
                    Attribute("street") ?? string.Empty,
                    Attribute("addressNumber") ?? string.Empty,
                    Attribute("complement"),
                    Attribute("district") ?? string.Empty,
                    Attribute("city") ?? string.Empty,
                    Attribute("state") ?? string.Empty);
            return new MyProfile(UserName, FirstName, LastName, Email, Attribute("mobilePhone"), address);
        }

        private string? Attribute(string name) =>
            Attributes is not null && Attributes.TryGetValue(name, out var values) && values.Length > 0 ? values[0] : null;
    }
}
