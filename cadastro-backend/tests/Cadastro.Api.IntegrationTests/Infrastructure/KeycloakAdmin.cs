using System.Net.Http.Json;
using System.Text.Json;

namespace Cadastro.Api.IntegrationTests.Infrastructure;

/// <summary>Operações administrativas no Keycloak de teste (admin padrão do container), para montar cenários.</summary>
public static class KeycloakAdmin
{
    // Credenciais padrão do Keycloak do Testcontainers (container descartável, só de teste).
    private const string AdminUser = "admin";
    private const string AdminPassword = "admin";

    /// <summary>Cria uma conta habilitada, com senha definitiva, para cenários com login fora do formato esperado.</summary>
    public static async Task CreateUserAsync(string keycloakUrl, string userName, string password)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var http = await AuthenticatedClientAsync(keycloakUrl);
        using var response = await http.PostAsJsonAsync(new Uri($"{keycloakUrl}/admin/realms/workspace/users"), new
        {
            username = userName,
            enabled = true,
            credentials = new[] { new { type = "password", value = password, temporary = false } },
        }, cancellation);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<HttpClient> AuthenticatedClientAsync(string keycloakUrl)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var http = new HttpClient();
        using var tokenResponse = await http.PostAsync(new Uri($"{keycloakUrl}/realms/master/protocol/openid-connect/token"),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = AdminUser,
                ["password"] = AdminPassword,
            }), cancellation);
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(cancellation)).GetProperty("access_token").GetString();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return http;
    }

    /// <summary>Encerra todas as sessões do usuário no Keycloak (como o admin faria ao desativar a conta).</summary>
    public static async Task EndAllSessionsAsync(string keycloakUrl, string userName)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var http = await AuthenticatedClientAsync(keycloakUrl);

        var users = await http.GetFromJsonAsync<JsonElement>(
            new Uri($"{keycloakUrl}/admin/realms/workspace/users?username={userName}&exact=true"), cancellation);
        var userId = users[0].GetProperty("id").GetString();
        using var logout = await http.PostAsync(new Uri($"{keycloakUrl}/admin/realms/workspace/users/{userId}/logout"), null, cancellation);
        logout.EnsureSuccessStatusCode();
    }
}
