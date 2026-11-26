using System.Security.Cryptography;
using System.Text;

namespace Cadastro.Application.UserImports;

/// <summary>
/// Assina o resumo devolvido no upload. A confirmação exige o mesmo valor: prova que quem confirma é o autor do lote e
/// recebeu o resumo deste arquivo (anti-confirmação acidental e camada extra ao antiforgery). A assinatura depende de uma
/// chave do servidor, então não pode ser calculada por quem só conhece o arquivo nem lida em consultas.
/// </summary>
public interface IImportSummarySigner
{
    /// <summary>Calcula o token (HMAC-SHA256 em hexadecimal minúsculo) do resumo do lote.</summary>
    string Sign(Guid batchId, string ownerId, string fileHash, int validLines, int invalidLines, int duplicateLines);

    /// <summary>Confere em tempo constante se <paramref name="provided"/> é o token do resumo informado.</summary>
    bool Matches(string? provided, Guid batchId, string ownerId, string fileHash, int validLines, int invalidLines, int duplicateLines);
}

/// <summary>HMAC-SHA256 com chave do servidor sobre hash do arquivo, contagens, id do lote e <c>sub</c> do autor.</summary>
public sealed class HmacImportSummarySigner : IImportSummarySigner
{
    /// <summary>Tamanho mínimo da chave em bytes.</summary>
    public const int MinKeyBytes = 32;

    private readonly byte[] _key;

    /// <summary>Cria o assinador; chave curta é recusada (fail fast).</summary>
    public HmacImportSummarySigner(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < MinKeyBytes)
        {
            throw new ArgumentException($"The summary token key must have at least {MinKeyBytes} bytes.", nameof(key));
        }

        _key = (byte[])key.Clone();
    }

    /// <inheritdoc />
    public string Sign(Guid batchId, string ownerId, string fileHash, int validLines, int invalidLines, int duplicateLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileHash);
        var input = $"{batchId:D}|{ownerId}|{fileHash}|{validLines}|{invalidLines}|{duplicateLines}";
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(input)));
    }

    /// <inheritdoc />
    public bool Matches(string? provided, Guid batchId, string ownerId, string fileHash, int validLines, int invalidLines, int duplicateLines) =>
        provided is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(Sign(batchId, ownerId, fileHash, validLines, invalidLines, duplicateLines)));
}
