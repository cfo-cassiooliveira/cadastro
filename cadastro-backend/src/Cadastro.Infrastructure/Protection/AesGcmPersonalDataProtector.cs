using System.Security.Cryptography;
using System.Text;
using Cadastro.Application.UserImports;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Protection;

/// <summary>
/// Cifra com AES-256-GCM os dados pessoais que a importação guarda até a linha terminar; com erro, até o reprocessamento ou a retenção (LGPD, ADR-0007).
/// <para>
/// Formato: <c>nonce(12) | tag(16) | texto cifrado</c>. Linha e campo entram como AAD: valor copiado para outro lugar não decifra.
/// A versão da chave fica no registro; sem recifragem, a chave antiga precisa continuar configurada enquanto houver dado dela.
/// O índice de e-mail é um HMAC-SHA256 com subchave derivada por HKDF, para não reutilizar a chave de cifra.
/// </para>
/// </summary>
internal sealed class AesGcmPersonalDataProtector : IPersonalDataProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] IndexKeyInfo = "cadastro-import-email-index-v1"u8.ToArray();

    private readonly Dictionary<string, byte[]> _keys;
    private readonly Dictionary<string, byte[]> _emailIndexKeys;

    /// <summary>Carrega as chaves validadas; sem a chave atual lança, em vez de cifrar sem proteção.</summary>
    public AesGcmPersonalDataProtector(IOptions<PersonalDataProtectionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        _keys = [];
        foreach (var (version, base64) in value.Keys)
        {
            if (PersonalDataProtectionOptionsValidator.TryDecode(base64, out var key))
            {
                _keys[version] = key;
            }
        }

        if (string.IsNullOrWhiteSpace(value.CurrentKeyVersion) || !_keys.ContainsKey(value.CurrentKeyVersion))
        {
            throw new InvalidOperationException("Personal data protection key is not configured.");
        }

        CurrentKeyVersion = value.CurrentKeyVersion;
        // Uma subchave de índice por versão: o índice de linhas antigas continua recalculável depois da troca de chave.
        _emailIndexKeys = _keys.ToDictionary(
            pair => pair.Key,
            pair => HKDF.DeriveKey(HashAlgorithmName.SHA256, pair.Value, PersonalDataProtectionOptions.KeySizeBytes, info: IndexKeyInfo));
    }

    /// <inheritdoc />
    public string CurrentKeyVersion { get; }

    /// <summary>Cifra com a chave atual, nonce novo e o contexto como dado autenticado.</summary>
    public byte[] Protect(string plaintext, string context)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrEmpty(context);

        var data = Encoding.UTF8.GetBytes(plaintext);
        var payload = new byte[NonceSize + TagSize + data.Length];
        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_keys[CurrentKeyVersion], TagSize);
        aes.Encrypt(nonce, data, payload.AsSpan(NonceSize + TagSize), tag, Encoding.UTF8.GetBytes(context));
        return payload;
    }

    /// <summary>Decifra e autentica; adulteração, contexto trocado ou versão de chave desconhecida lançam exceção.</summary>
    public string Unprotect(byte[] payload, string keyVersion, string context)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrEmpty(context);
        if (!_keys.TryGetValue(keyVersion, out var key))
        {
            throw new CryptographicException("Unknown personal data key version.");
        }

        if (payload.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Protected value is malformed.");
        }

        var cipherLength = payload.Length - NonceSize - TagSize;
        var plain = new byte[cipherLength];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(
            payload.AsSpan(0, NonceSize),
            payload.AsSpan(NonceSize + TagSize, cipherLength),
            payload.AsSpan(NonceSize, TagSize),
            plain,
            Encoding.UTF8.GetBytes(context));
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            // Não deixa o texto claro (em bytes) vivo na memória além do necessário.
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Calcula o HMAC-SHA256 do e-mail normalizado com a chave atual.</summary>
    public byte[] ComputeEmailIndex(string email) => ComputeEmailIndex(email, CurrentKeyVersion);

    /// <summary>Calcula o HMAC-SHA256 do e-mail normalizado com a versão de chave gravada na linha; versão desconhecida lança.</summary>
    public byte[] ComputeEmailIndex(string email, string keyVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyVersion);
        if (!_emailIndexKeys.TryGetValue(keyVersion, out var indexKey))
        {
            // Falha explícita: devolver um índice com outra chave faria a busca "não achar" sem avisar.
            throw new CryptographicException("Unknown personal data key version.");
        }

        // Trim + NFC + minúsculas: "é" composto e decomposto geram o mesmo índice.
        var normalized = email.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        return HMACSHA256.HashData(indexKey, Encoding.UTF8.GetBytes(normalized));
    }
}
