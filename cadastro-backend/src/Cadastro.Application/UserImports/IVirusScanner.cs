namespace Cadastro.Application.UserImports;

/// <summary>Resultado da verificação de um arquivo no antivírus.</summary>
public enum VirusScanResult
{
    /// <summary>Nenhuma ameaça encontrada.</summary>
    Clean = 0,

    /// <summary>Ameaça detectada; o arquivo deve ser recusado.</summary>
    Infected = 1,
}

/// <summary>O antivírus não pôde verificar o arquivo (indisponível, timeout ou resposta inesperada). O upload é recusado (fail-closed).</summary>
public sealed class VirusScannerUnavailableException : Exception
{
    /// <summary>Cria a exceção com mensagem técnica (inglês, sem dado do arquivo).</summary>
    public VirusScannerUnavailableException(string message) : base(message)
    {
    }

    /// <summary>Cria a exceção encapsulando a causa.</summary>
    public VirusScannerUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Porta do antivírus (ClamAV por INSTREAM na implementação).</summary>
public interface IVirusScanner
{
    /// <summary>
    /// Verifica o conteúdo, sem carregá-lo inteiro em memória. Lança <see cref="VirusScannerUnavailableException"/> quando não
    /// consegue verificar: nunca devolve <see cref="VirusScanResult.Clean"/> por omissão.
    /// </summary>
    Task<VirusScanResult> ScanAsync(Stream content, CancellationToken cancellationToken);
}
