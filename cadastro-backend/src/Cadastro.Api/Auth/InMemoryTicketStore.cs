using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;

namespace Cadastro.Api.Auth;

/// <summary>
/// Guarda a sessão (identidade e tokens do Keycloak) na memória do servidor; o cookie leva só a chave (ADR-0001).
/// A entrada expira por inatividade junto com a sessão.
/// </summary>
internal sealed class InMemoryTicketStore(IMemoryCache cache) : ITicketStore
{
    private const int KeySizeInBytes = 32;
    private const string KeyPrefix = "bff-session:";

    /// <summary>Grava a sessão nova sob uma chave aleatória de 256 bits e devolve a chave.</summary>
    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Base64UrlEncode(RandomNumberGenerator.GetBytes(KeySizeInBytes));
        Save(key, ticket);
        return Task.FromResult(key);
    }

    /// <summary>Atualiza a sessão (tokens renovados ou expiração deslizante).</summary>
    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        Save(key, ticket);
        return Task.CompletedTask;
    }

    /// <summary>Busca a sessão; <c>null</c> quando expirou, foi encerrada ou o servidor reiniciou.</summary>
    public Task<AuthenticationTicket?> RetrieveAsync(string key) =>
        Task.FromResult(cache.TryGetValue(KeyPrefix + key, out AuthenticationTicket? ticket) ? ticket : null);

    /// <summary>Encerra a sessão no servidor (logout).</summary>
    public Task RemoveAsync(string key)
    {
        cache.Remove(KeyPrefix + key);
        return Task.CompletedTask;
    }

    private void Save(string key, AuthenticationTicket ticket)
    {
        var entry = new MemoryCacheEntryOptions { SlidingExpiration = BffAuthenticationSetup.SessionIdleTimeout };
        if (ticket.Properties.ExpiresUtc is { } expiresUtc)
        {
            entry.AbsoluteExpiration = expiresUtc;
        }

        cache.Set(KeyPrefix + key, ticket, entry);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
