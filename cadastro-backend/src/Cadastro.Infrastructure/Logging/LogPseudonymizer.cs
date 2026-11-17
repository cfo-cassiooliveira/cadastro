using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Cadastro.Application.LogSearch;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Logging;

/// <summary>
/// Pseudonimiza com HMAC-SHA256 conforme o ADR-0003 do graylog:
/// <c>"&lt;keyId&gt;-" + hex(HMAC-SHA256(chave, "&lt;domínio&gt;|" + canônico))[0..32]</c>, hex minúsculo. Emite com a
/// chave atual e pesquisa com todas as chaves configuradas (rotação).
/// </summary>
internal sealed class LogPseudonymizer : ILogPseudonymizer
{
    private const int TruncatedBytes = 16;

    private readonly KeyValuePair<string, byte[]> _current;
    private readonly IReadOnlyList<KeyValuePair<string, byte[]>> _keys;

    /// <summary>Carrega as chaves não vazias; as opções já foram validadas (<see cref="PseudonymizationOptionsValidator"/>).</summary>
    public LogPseudonymizer(IOptions<PseudonymizationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var current = options.Value;
        _keys = current.Keys.Where(k => !string.IsNullOrWhiteSpace(k.Value))
            .OrderBy(k => k.Key, StringComparer.Ordinal)
            .Select(k => KeyValuePair.Create(k.Key, PseudonymizationOptionsValidator.Decode(k.Value) ?? throw InvalidKey()))
            .ToList();
        _current = _keys.FirstOrDefault(k => k.Key == current.CurrentKeyId);
        if (_current.Value is null)
        {
            throw InvalidKey();
        }
    }

    /// <summary>Canônico → máscara do ADR-0002 e token da chave atual.</summary>
    public bool TryPseudonymize(PersonalDataKind kind, string raw, [NotNullWhen(true)] out PseudonymizedValue? value)
    {
        value = PersonalDataFormat.TryCanonicalize(kind, raw, out var canonical)
            ? new PseudonymizedValue(PersonalDataFormat.Mask(kind, canonical), Token(_current, kind, canonical))
            : null;
        return value is not null;
    }

    /// <summary>Tokens do canônico com cada chave configurada, em ordem de id.</summary>
    public IReadOnlyList<PseudonymToken> Tokenize(PersonalDataKind kind, string raw) =>
        PersonalDataFormat.TryCanonicalize(kind, raw, out var canonical)
            ? _keys.Select(key => new PseudonymToken(key.Key, Token(key, kind, canonical))).ToList()
            : [];

    private static string Token(KeyValuePair<string, byte[]> key, PersonalDataKind kind, string canonical)
    {
        var message = Encoding.UTF8.GetBytes($"{PersonalDataFormat.Domain(kind)}|{canonical}");
        var mac = HMACSHA256.HashData(key.Value, message);
        return $"{key.Key}-{Convert.ToHexStringLower(mac, 0, TruncatedBytes)}";
    }

    private static InvalidOperationException InvalidKey() =>
        new($"{PseudonymizationOptions.SectionName}: current key is missing or invalid.");
}
