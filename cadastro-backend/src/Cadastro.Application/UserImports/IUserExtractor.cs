namespace Cadastro.Application.UserImports;

/// <summary>Uma linha lida do arquivo, ainda sem validação de negócio.</summary>
/// <param name="LineNumber">Número da linha de dados (base 1, sem contar o cabeçalho).</param>
/// <param name="Name">Nome informado.</param>
/// <param name="Email">E-mail informado.</param>
/// <param name="Cpf">CPF informado (com ou sem formatação).</param>
/// <param name="Phone">Telefone informado (opcional).</param>
/// <param name="StructuralIssue">Código de problema de estrutura da linha (ex.: colunas a mais); <c>null</c> quando bem formada.</param>
public sealed record ExtractedRow(int LineNumber, string? Name, string? Email, string? Cpf, string? Phone, string? StructuralIssue = null);

/// <summary>Erro do arquivo inteiro (não de uma linha): o upload é recusado com este código.</summary>
public sealed class ImportFileException : Exception
{
    /// <summary>Cria a exceção com o código do erro (<c>file.not_utf8</c>, <c>file.header_invalid</c>, <c>file.too_many_lines</c>).</summary>
    public ImportFileException(string code) : base($"Import file rejected: {code}")
    {
        Code = code;
    }

    /// <summary>Código estável do erro, traduzido pela interface.</summary>
    public string Code { get; }
}

/// <summary>Extrai as pessoas de um arquivo em streaming; CSV no MVP, XLSX e PDF depois (novas implementações).</summary>
public interface IUserExtractor
{
    /// <summary>Lê o arquivo linha a linha. Erros do arquivo inteiro lançam <see cref="ImportFileException"/>.</summary>
    IAsyncEnumerable<ExtractedRow> ExtractAsync(Stream content, CancellationToken cancellationToken);
}
