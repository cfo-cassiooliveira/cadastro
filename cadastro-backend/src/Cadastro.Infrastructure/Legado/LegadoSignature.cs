using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cadastro.Infrastructure.Legado;

/// <summary>
/// Assinatura das chamadas à API interna do Legado, no formato do ADR-0005 do Legado:
/// <c>keyId=..., t=..., n=..., v1=base64(HMAC-SHA256(chave, canônico))</c>, com canônico =
/// <c>t \n n \n MÉTODO \n rota+query \n hex(SHA-256(corpo))</c>.
/// </summary>
internal static class LegadoSignature
{
    /// <summary>Header com a assinatura.</summary>
    public const string HeaderName = "X-Legado-Signature";

    /// <summary>Texto canônico assinado.</summary>
    public static string Canonical(long timestamp, string nonce, string method, string pathAndQuery, ReadOnlySpan<byte> body) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{timestamp}\n{nonce}\n{method.ToUpperInvariant()}\n{pathAndQuery}\n{Convert.ToHexStringLower(SHA256.HashData(body))}");

    /// <summary>Nonce novo (16 bytes aleatórios em base64url); um por tentativa, inclusive nos retries.</summary>
    public static string NewNonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    /// <summary>Valor completo do header.</summary>
    public static string Header(string keyId, byte[] key, long timestamp, string nonce, string method, string pathAndQuery, ReadOnlySpan<byte> body)
    {
        var signature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(timestamp, nonce, method, pathAndQuery, body)));
        return string.Create(CultureInfo.InvariantCulture, $"keyId={keyId}, t={timestamp}, n={nonce}, v1={Convert.ToBase64String(signature)}");
    }
}
