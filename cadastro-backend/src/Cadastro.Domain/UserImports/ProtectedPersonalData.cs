namespace Cadastro.Domain.UserImports;

/// <summary>
/// Dados pessoais de uma linha já cifrados (AES-256-GCM, feito pela Application/Infrastructure): o domínio nunca vê o texto claro.
/// O objeto é imutável: guarda cópias dos buffers recebidos e devolve cópias nas propriedades, para ninguém alterar os bytes por fora.
/// </summary>
public sealed class ProtectedPersonalData
{
    private readonly byte[] _name;
    private readonly byte[] _email;
    private readonly byte[] _cpf;
    private readonly byte[]? _phone;
    private readonly byte[] _emailIndex;

    /// <summary>Cria o conjunto de valores cifrados; os quatro primeiros e o índice são obrigatórios, o telefone é opcional.</summary>
    /// <param name="name">Nome cifrado.</param>
    /// <param name="email">E-mail cifrado.</param>
    /// <param name="cpf">CPF cifrado.</param>
    /// <param name="phone">Telefone cifrado; <c>null</c> quando não informado.</param>
    /// <param name="emailIndex">Índice cego (HMAC) do e-mail normalizado, que permite comparar e-mails sem decifrar.</param>
    /// <param name="keyVersion">Identificador da versão da chave que cifrou os valores.</param>
    /// <param name="emailIndexKeyVersion">Versão da chave que gerou o índice; por padrão, a mesma de <paramref name="keyVersion"/>.</param>
    public ProtectedPersonalData(
        byte[] name, byte[] email, byte[] cpf, byte[]? phone, byte[] emailIndex, string keyVersion, string? emailIndexKeyVersion = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(cpf);
        ArgumentNullException.ThrowIfNull(emailIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyVersion);
        _name = (byte[])name.Clone();
        _email = (byte[])email.Clone();
        _cpf = (byte[])cpf.Clone();
        _phone = (byte[]?)phone?.Clone();
        _emailIndex = (byte[])emailIndex.Clone();
        KeyVersion = keyVersion;
        EmailIndexKeyVersion = string.IsNullOrWhiteSpace(emailIndexKeyVersion) ? keyVersion : emailIndexKeyVersion;
    }

    /// <summary>Nome cifrado (cópia).</summary>
    public byte[] Name => (byte[])_name.Clone();

    /// <summary>E-mail cifrado (cópia).</summary>
    public byte[] Email => (byte[])_email.Clone();

    /// <summary>CPF cifrado (cópia).</summary>
    public byte[] Cpf => (byte[])_cpf.Clone();

    /// <summary>Telefone cifrado (cópia); <c>null</c> quando não informado.</summary>
    public byte[]? Phone => (byte[]?)_phone?.Clone();

    /// <summary>Índice cego (HMAC) do e-mail normalizado (cópia).</summary>
    public byte[] EmailIndex => (byte[])_emailIndex.Clone();

    /// <summary>Identificador da versão da chave que cifrou os valores.</summary>
    public string KeyVersion { get; }

    /// <summary>Versão da chave que gerou o índice cego; uma busca por e-mail teria de recalcular o índice com esta versão.</summary>
    public string EmailIndexKeyVersion { get; }
}
