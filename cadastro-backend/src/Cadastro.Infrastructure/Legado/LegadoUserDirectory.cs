using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cadastro.Application.UserImports;
using Polly;

namespace Cadastro.Infrastructure.Legado;

/// <summary>
/// Cliente da API interna do Legado: consulta por e-mail (<c>user-lookups</c>) e marcação da conta migrada
/// (<c>PUT users/{id}/migration</c>). E-mail vai no corpo, nunca na URL. Qualquer falha vira
/// <see cref="LegacyDirectoryUnavailableException"/>; o chamador não desfaz o usuário do Keycloak por causa dela.
/// </summary>
internal sealed class LegadoUserDirectory(HttpClient httpClient) : ILegacyUserDirectory
{
    private const string Prefix = "api/v1/internal";

    /// <inheritdoc />
    public async Task<IReadOnlyList<LegacyUserCandidate>> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return await CallAsync(async () =>
        {
            using var response = await httpClient.PostAsJsonAsync(
                new Uri($"{Prefix}/user-lookups", UriKind.Relative), new { login = email.Trim() }, cancellationToken);
            EnsureStatus(response, HttpStatusCode.OK);
            var lookup = await response.Content.ReadFromJsonAsync<LookupResponse>(cancellationToken)
                ?? throw new LegacyDirectoryUnavailableException("Empty Legado lookup response.");
            return lookup is { Outcome: "found", User: { } user }
                ? (IReadOnlyList<LegacyUserCandidate>)[new LegacyUserCandidate(user.Id, user.Email ?? string.Empty)]
                : [];
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkMigratedAsync(Guid legacyUserId, string keycloakUserId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keycloakUserId);
        if (!Guid.TryParse(keycloakUserId, out var keycloakGuid))
        {
            throw new LegacyDirectoryUnavailableException("The Keycloak user id is not a GUID.");
        }

        await CallAsync(async () =>
        {
            using var response = await httpClient.PutAsJsonAsync(
                new Uri($"{Prefix}/users/{legacyUserId:D}/migration", UriKind.Relative), new { keycloakUserId = keycloakGuid }, cancellationToken);
            EnsureStatus(response, HttpStatusCode.NoContent);
            return true;
        }, cancellationToken);
    }

    private static async Task<T> CallAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ExecutionRejectedException or JsonException
                                              && !cancellationToken.IsCancellationRequested)
        {
            throw new LegacyDirectoryUnavailableException("Legado internal API unreachable or invalid response.", exception);
        }
    }

    private static void EnsureStatus(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            throw new LegacyDirectoryUnavailableException($"Legado internal API answered {(int)response.StatusCode}.");
        }
    }

    private sealed record LookupResponse(
        [property: JsonPropertyName("outcome")] string Outcome,
        [property: JsonPropertyName("user")] LookupUser? User);

    private sealed record LookupUser(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("email")] string? Email);
}
