using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cadastro.Tests.Shared;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.Keycloak;

namespace Cadastro.Infrastructure.Tests.UserImports;

/// <summary>
/// Keycloak real (mesma imagem por digest do ../keycloak) com o client <c>cadastro-importer</c> e o User Profile do realm de
/// produção, mais um Mailpit na mesma rede como SMTP do realm: o e-mail de ativação chega de verdade e o teste confere o
/// prazo do link (72 h).
/// </summary>
public sealed partial class KeycloakMailFixture : IAsyncLifetime
{
    /// <summary>Segredo do client de teste (o realm de teste tem o mesmo valor).</summary>
    public const string ImporterSecret = "test-only-importer-secret";

    private const string KeycloakImage =
        "quay.io/keycloak/keycloak:26.8.2@sha256:723b1d6e254c0fc96d5cffec860c7e0744d6a3277822b865bb538cf891575e41";

    private const string MailpitImage =
        "axllent/mailpit:v1.32.0@sha256:0790692be935ea4420d085694f7985b1f692ca6a8771ab0e0bd8ba75ac180498";

    private readonly INetwork _network = new NetworkBuilder().Build();
    private readonly IContainer _mailpit;
    private readonly KeycloakContainer _keycloak;
    private readonly HttpClient _http = new();

    /// <summary>Cria os containers (ainda parados).</summary>
    public KeycloakMailFixture()
    {
        _mailpit = new ContainerBuilder(MailpitImage)
            .WithNetwork(_network)
            .WithNetworkAliases("mailpit")
            .WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8025).ForPath("/livez")))
            .Build();
        _keycloak = new KeycloakBuilder(KeycloakImage)
            .WithNetwork(_network)
            .WithRealm(Path.Combine(AppContext.BaseDirectory, "UserImports", "workspace-realm.json"))
            .Build();
    }

    /// <summary>Endereço base do Keycloak no host.</summary>
    public string KeycloakUrl => _keycloak.GetBaseAddress().TrimEnd('/');

    private string MailUrl => $"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(8025)}";

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        using var startLock = await ContainerStartLock.AcquireAsync(TestContext.Current.CancellationToken);
        await _network.CreateAsync(TestContext.Current.CancellationToken);
        await _mailpit.StartAsync(TestContext.Current.CancellationToken);
        await _keycloak.StartAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _keycloak.DisposeAsync();
        await _mailpit.DisposeAsync();
        await _network.DisposeAsync();
    }

    /// <summary>Usuários do realm com o e-mail exato (Admin API, admin do container).</summary>
    public async Task<JsonElement> FindUsersByEmailAsync(string email)
    {
        using var request = await AdminRequestAsync(HttpMethod.Get, $"users?email={Uri.EscapeDataString(email)}&exact=true");
        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).Clone();
    }

    /// <summary>Credenciais do usuário (vazio = sem senha).</summary>
    public async Task<int> CredentialCountAsync(string userId)
    {
        using var request = await AdminRequestAsync(HttpMethod.Get, $"users/{userId}/credentials");
        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetArrayLength();
    }

    /// <summary>Cria diretamente um usuário (pessoa que já existia antes da importação).</summary>
    public async Task CreateUserAsync(string userName, string email)
    {
        using var request = await AdminRequestAsync(HttpMethod.Post, "users");
        request.Content = JsonContent.Create(new { username = userName, email, enabled = true });
        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Espera o e-mail de ativação para o endereço e devolve a validade do link em segundos (<c>exp - iat</c> do JWT do
    /// parâmetro <c>key</c>); <c>null</c> se nenhum e-mail chegar em 20 s.
    /// </summary>
    public async Task<long?> ActivationLinkLifespanSecondsAsync(string email)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var search = await _http.GetFromJsonAsync<JsonElement>(
                $"{MailUrl}/api/v1/search?query={Uri.EscapeDataString($"to:{email}")}", TestContext.Current.CancellationToken);
            if (search.GetProperty("messages").GetArrayLength() > 0)
            {
                var id = search.GetProperty("messages")[0].GetProperty("ID").GetString();
                var message = await _http.GetFromJsonAsync<JsonElement>($"{MailUrl}/api/v1/message/{id}", TestContext.Current.CancellationToken);
                var text = message.GetProperty("Text").GetString() + message.GetProperty("HTML").GetString();
                return LifespanOf(KeyParameter().Match(text.Replace("&amp;", "&", StringComparison.Ordinal)).Groups[1].Value);
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        return null;
    }

    /// <summary>Quantos e-mails o Mailpit recebeu para o endereço.</summary>
    public async Task<int> MailCountAsync(string email)
    {
        var search = await _http.GetFromJsonAsync<JsonElement>(
            $"{MailUrl}/api/v1/search?query={Uri.EscapeDataString($"to:{email}")}", TestContext.Current.CancellationToken);
        return search.GetProperty("messages").GetArrayLength();
    }

    private static long? LifespanOf(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
        return json.RootElement.GetProperty("exp").GetInt64() - json.RootElement.GetProperty("iat").GetInt64();
    }

    private async Task<HttpRequestMessage> AdminRequestAsync(HttpMethod method, string path)
    {
        using var tokenResponse = await _http.PostAsync(
            new Uri($"{KeycloakUrl}/realms/master/protocol/openid-connect/token"),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = "admin",
                ["password"] = "admin",
            }),
            TestContext.Current.CancellationToken);
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("access_token").GetString();
        var request = new HttpRequestMessage(method, new Uri($"{KeycloakUrl}/admin/realms/workspace/{path}"));
        request.Headers.Authorization = new("Bearer", token);
        return request;
    }

    [GeneratedRegex("key=([A-Za-z0-9_.-]+)")]
    private static partial Regex KeyParameter();
}
