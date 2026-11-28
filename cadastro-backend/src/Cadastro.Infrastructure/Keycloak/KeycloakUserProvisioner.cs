using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadastro.Application.UserImports;
using Cadastro.Application.Users;
using Microsoft.Extensions.Options;
using Polly;

namespace Cadastro.Infrastructure.Keycloak;

/// <summary>
/// Cria os usuários da importação na Admin API do Keycloak com o client <c>cadastro-importer</c>: sem senha, com a ação
/// obrigatória <c>UPDATE_PASSWORD</c>, atributo <c>importLineId</c> e e-mail de ativação com validade de 72 h.
/// Só usa busca, criação de usuário e <c>execute-actions-email</c> (a role <c>manage-users</c> permite mais, risco aceito).
/// <para>A mensagem pode chegar mais de uma vez e a criação no Keycloak não participa da transação do banco: o
/// <c>importLineId</c> é o que permite reconhecer o usuário já criado por uma tentativa interrompida. Custo: buscas antes de
/// cada criação, e o e-mail de ativação pode ser reenviado enquanto a senha não for definida.</para>
/// </summary>
internal sealed class KeycloakUserProvisioner(
    HttpClient httpClient,
    KeycloakServiceTokenProvider tokenProvider,
    IOptions<KeycloakAdminOptions> adminOptions,
    CreationRateLimiter rateLimiter) : IIdentityUserProvisioner
{
    /// <summary>Chave do provedor de token do importador no contêiner de DI.</summary>
    public const string TokenProviderKey = "keycloak-importer-token";

    /// <summary>Validade do link de ativação: 72 h (o realm fica no padrão de 12 h; só a importação pede 72 h).</summary>
    public const int ActivationLifespanSeconds = 259200;

    private const string ImportLineAttribute = "importLineId";
    private const string UpdatePasswordAction = "UPDATE_PASSWORD";
    private const int MobilePhoneDigits = 11;

    /// <inheritdoc />
    public async Task<ProvisionedUser> ProvisionAsync(ProvisionUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            try
            {
                return await ProvisionOnceAsync(request, cancellationToken);
            }
            catch (TokenRejectedException)
            {
                // Token em cache recusado antes de vencer: descarta e tenta UMA vez com outro; se recusar de novo, é falha operacional.
                tokenProvider.Invalidate();
                try
                {
                    return await ProvisionOnceAsync(request, cancellationToken);
                }
                catch (TokenRejectedException ex)
                {
                    throw new IdentityAccessDeniedException("Keycloak Admin API refused the importer token twice.", ex);
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ExecutionRejectedException
                                              && !cancellationToken.IsCancellationRequested)
        {
            throw new IdentityProviderUnavailableException("Keycloak Admin API unreachable.", exception);
        }
    }

    private async Task<ProvisionedUser> ProvisionOnceAsync(ProvisionUserRequest request, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetTokenAsync(cancellationToken);
        var existing = await FindByLineAsync(request.LineId, token, cancellationToken);
        if (existing is not null)
        {
            // Retomada: só reenvia o e-mail se a ação UPDATE_PASSWORD ainda estiver pendente (a pessoa ainda não ativou) e,
            // como é uma chamada a mais na Admin API, passa pelo mesmo limitador da criação.
            if (existing.PasswordActionPending)
            {
                await rateLimiter.WaitAsync(cancellationToken);
                await SendActivationEmailAsync(existing.Id, token, cancellationToken);
            }

            return new ProvisionedUser(existing.Id, ProvisionOutcome.Created);
        }

        if (await FindByEmailAsync(request.Email, token, cancellationToken) is { } sameEmail)
        {
            return new ProvisionedUser(sameEmail.Id, ProvisionOutcome.AlreadyExists);
        }

        var created = await CreateAsync(request, token, cancellationToken);
        if (created.Outcome == ProvisionOutcome.Created)
        {
            await SendActivationEmailAsync(created.UserId, token, cancellationToken);
        }

        return created;
    }

    /// <summary>Usuário achado na busca e se ainda tem a ação UPDATE_PASSWORD pendente.</summary>
    private sealed record FoundUser(string Id, bool PasswordActionPending);

    /// <summary>A Admin API recusou o token (401): sinaliza para descartar o cache e repetir uma vez.</summary>
    private sealed class TokenRejectedException : Exception;

    /// <summary>Nome de usuário não pessoal e estável: o login real do usuário é o e-mail.</summary>
    internal static string UserNameFor(Guid lineId) =>
        "imp." + Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(lineId.ToString("D"))))[..24];

    /// <summary>Divide o nome completo no primeiro espaço (nome, sobrenome); mesma regra e limites do upload.</summary>
    internal static (string FirstName, string LastName) SplitName(string fullName) => PersonNameRules.Split(fullName);

    /// <summary>Celular no formato do perfil (<c>+55</c> e 11 dígitos); <c>null</c> se não der para normalizar (o campo é opcional).</summary>
    internal static string? NormalizeMobilePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = new string([.. phone.Where(char.IsAsciiDigit)]);
        if (digits.StartsWith("55", StringComparison.Ordinal) && digits.Length == MobilePhoneDigits + 2)
        {
            digits = digits[2..];
        }

        return digits.Length == MobilePhoneDigits ? $"+55{digits}" : null;
    }

    private string UsersPath => $"admin/realms/{adminOptions.Value.Realm}/users";

    private async Task<FoundUser?> FindByLineAsync(Guid lineId, string token, CancellationToken cancellationToken) =>
        await SearchAsync($"{UsersPath}?q={ImportLineAttribute}:{lineId:D}", token, cancellationToken);

    private async Task<FoundUser?> FindByEmailAsync(string email, string token, CancellationToken cancellationToken) =>
        await SearchAsync($"{UsersPath}?email={Uri.EscapeDataString(email.Trim().ToLowerInvariant())}&exact=true", token, cancellationToken);

    private async Task<FoundUser?> SearchAsync(string relativeUrl, string token, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, relativeUrl, token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        EnsureSuccess(response);
        var users = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (users.ValueKind != JsonValueKind.Array || users.GetArrayLength() == 0)
        {
            return null;
        }

        var user = users[0];
        var pending = user.TryGetProperty("requiredActions", out var actions) && actions.ValueKind == JsonValueKind.Array
            && actions.EnumerateArray().Any(a => a.GetString() == UpdatePasswordAction);
        return new FoundUser(user.GetProperty("id").GetString()!, pending);
    }

    private async Task<ProvisionedUser> CreateAsync(ProvisionUserRequest user, string token, CancellationToken cancellationToken)
    {
        await rateLimiter.WaitAsync(cancellationToken);
        var (firstName, lastName) = SplitName(user.Name);
        var attributes = new JsonObject { [ImportLineAttribute] = new JsonArray(user.LineId.ToString("D")) };
        if (NormalizeMobilePhone(user.Phone) is { } mobilePhone)
        {
            attributes["mobilePhone"] = new JsonArray(mobilePhone);
        }

        var body = new JsonObject
        {
            ["username"] = UserNameFor(user.LineId),
            ["email"] = user.Email.Trim(),
            ["firstName"] = firstName,
            ["lastName"] = lastName,
            ["enabled"] = true,
            ["requiredActions"] = new JsonArray(UpdatePasswordAction),
            ["attributes"] = attributes,
        };
        using var request = NewRequest(HttpMethod.Post, UsersPath, token);
        request.Content = JsonContent.Create(body);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Created)
        {
            return new ProvisionedUser(
                response.Headers.Location is { } location
                    ? location.Segments[^1].TrimEnd('/')
                    : throw new IdentityProviderUnavailableException("Keycloak did not return the created user location."),
                ProvisionOutcome.Created);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // Reenvio após timeout (já é desta linha: segue e envia o e-mail) ou corrida de e-mail (já é de outra pessoa: AlreadyExists).
            if (await FindByLineAsync(user.LineId, token, cancellationToken) is { } own)
            {
                return new ProvisionedUser(own.Id, ProvisionOutcome.Created);
            }

            return await FindByEmailAsync(user.Email, token, cancellationToken) is { } other
                ? new ProvisionedUser(other.Id, ProvisionOutcome.AlreadyExists)
                : throw new IdentityUserRejectedException(ImportFailureCodes.IdentityRejected);
        }

        throw Classify(response.StatusCode, ImportFailureCodes.IdentityRejected);
    }

    private async Task SendActivationEmailAsync(string userId, string token, CancellationToken cancellationToken)
    {
        // Sempre com lifespan: sem o parâmetro o e-mail usaria o padrão do realm (12 h).
        using var request = NewRequest(
            HttpMethod.Put, $"{UsersPath}/{Uri.EscapeDataString(userId)}/execute-actions-email?lifespan={ActivationLifespanSeconds}", token);
        request.Content = JsonContent.Create(new[] { UpdatePasswordAction });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        EnsureSuccess(response, "identity.activation_email_rejected");
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string relativeUrl, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(relativeUrl, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string rejectedCode = ImportFailureCodes.IdentityRejected)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw Classify(response.StatusCode, rejectedCode);
        }
    }

    // 401 = token recusado (descarta o cache e repete uma vez); 403 = permissão do importador removida (falha operacional); 5xx, 408 e
    // 429 são transitórios; demais 4xx são definitivos.
    private static Exception Classify(HttpStatusCode status, string rejectedCode) =>
        status switch
        {
            HttpStatusCode.Unauthorized => new TokenRejectedException(),
            HttpStatusCode.Forbidden => new IdentityAccessDeniedException("Keycloak Admin API answered 403 to the importer."),
            _ when (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests =>
                new IdentityProviderUnavailableException($"Keycloak Admin API answered {(int)status}."),
            _ => new IdentityUserRejectedException(rejectedCode),
        };
}
