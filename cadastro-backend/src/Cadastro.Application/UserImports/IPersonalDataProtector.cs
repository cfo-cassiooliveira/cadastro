namespace Cadastro.Application.UserImports;

/// <summary>
/// Porta de cifra dos dados pessoais da importação (AES-256-GCM na implementação). A chave nunca sai da implementação.
/// </summary>
public interface IPersonalDataProtector
{
    /// <summary>Versão da chave usada para cifrar novos valores; é gravada no registro junto do dado.</summary>
    string CurrentKeyVersion { get; }

    /// <summary>
    /// Cifra um valor. <paramref name="context"/> (ex.: <c>{lineId}:email</c>) é autenticado junto: um valor copiado
    /// para outra linha ou coluna deixa de decifrar. Cada chamada usa um nonce novo.
    /// </summary>
    byte[] Protect(string plaintext, string context);

    /// <summary>Decifra um valor gravado com <paramref name="keyVersion"/>; lança se foi adulterado, o contexto difere ou a chave não existe.</summary>
    string Unprotect(byte[] payload, string keyVersion, string context);

    /// <summary>
    /// Índice cego (HMAC-SHA256) do e-mail normalizado (trim, NFC e minúsculas), determinístico para a chave atual:
    /// permite achar/duplicar por e-mail sem guardar o e-mail em claro.
    /// </summary>
    byte[] ComputeEmailIndex(string email);

    /// <summary>
    /// Igual a <see cref="ComputeEmailIndex(string)"/>, mas com a chave da versão gravada na linha
    /// (<c>EmailIndexKeyVersion</c>); é o que a busca deve usar depois de uma rotação. Versão desconhecida lança.
    /// </summary>
    byte[] ComputeEmailIndex(string email, string keyVersion);
}
