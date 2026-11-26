namespace Cadastro.Application.UserImports;

/// <summary>Limites e contratos fixos da importação (spec 002, decisões Q4 e Q6).</summary>
public static class ImportLimits
{
    /// <summary>Tamanho máximo do arquivo: 5 MB.</summary>
    public const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>Máximo de linhas de dados (sem o cabeçalho).</summary>
    public const int MaxLines = 5000;

    /// <summary>Cabeçalho exato esperado no CSV.</summary>
    public const string CsvHeader = "nome,email,cpf,telefone";

    /// <summary>Máximo de problemas por linha devolvidos na resposta do upload (o restante só entra na contagem).</summary>
    public const int MaxReportedIssues = 200;

    /// <summary>Routing key (e <c>type</c> do envelope) do evento publicado por linha aceita; contrato versionado (Art. IX).</summary>
    public const string LineAcceptedEventType = "cadastro.user-import-line.accepted.v1";

    /// <summary>Origem (<c>source</c>) dos envelopes publicados pelo Cadastro.</summary>
    public const string EventSource = "cadastro-api";
}
