namespace Cadastro.Api.UserImports;

/// <summary>
/// Trilha de auditoria de acesso à importação em lote. Só o identificador do ator (<c>sub</c>) vai ao log: nunca nome,
/// e-mail, CPF nem conteúdo do arquivo. O <c>audit_event</c> fica fora de <c>PersonalDataFields</c>.
/// </summary>
internal static partial class UserImportAudit
{
    /// <summary>Valor de <c>audit_event</c> de um acesso negado.</summary>
    public const string AccessDenied403 = "user_import_access_denied";

    /// <summary>Registra o acesso negado (role ausente ou OTP vencido).</summary>
    public static void AccessDenied(ILogger logger, string actorId) => LogDenied(logger, AccessDenied403, actorId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "User import access denied: {audit_event} for {actor_id}")]
    private static partial void LogDenied(ILogger logger, string audit_event, string actor_id);
}
