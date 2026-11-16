namespace Cadastro.Api.LogSearch;

/// <summary>
/// Trilha de auditoria da pesquisa em logs. Os nomes dos campos (snake_case) são o contrato com o stream
/// <c>Cadastro - Pesquisa em logs (LGPD)</c> do graylog e ficam fora de <c>PersonalDataFields</c>: não podem ser
/// pseudonimizados. Nunca vão ao log o valor pesquisado nem a máscara.
/// </summary>
internal static partial class LogSearchAudit
{
    /// <summary>Valor de <c>audit_event</c> de uma pesquisa atendida.</summary>
    public const string QueryIssued = "log_search_query_issued";

    /// <summary>Valor de <c>audit_event</c> de um acesso negado.</summary>
    public const string AccessDenied403 = "log_search_access_denied";

    /// <summary>Nomes dos campos de auditoria (contrato com o graylog).</summary>
    public static readonly IReadOnlyList<string> FieldNames =
        ["audit_event", "investigator_id", "data_type", "case_reference", "trace_id", "tokens"];

    /// <summary>Registra a pesquisa atendida; <c>tokens</c> é um texto único (lista viraria <c>tokens_0</c>... no sink GELF).</summary>
    public static void Issued(ILogger logger, string investigatorId, string dataType, string caseReference, string tokens) =>
        LogIssued(logger, QueryIssued, investigatorId, dataType, caseReference, tokens);

    /// <summary>Registra o acesso negado, só com o investigador.</summary>
    public static void AccessDenied(ILogger logger, string investigatorId) =>
        LogDenied(logger, AccessDenied403, investigatorId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Log search query issued: {audit_event} by {investigator_id}, data type {data_type}, case {case_reference}, tokens {tokens}")]
    private static partial void LogIssued(ILogger logger, string audit_event, string investigator_id, string data_type, string case_reference, string tokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Log search access denied: {audit_event} for {investigator_id}")]
    private static partial void LogDenied(ILogger logger, string audit_event, string investigator_id);
}
