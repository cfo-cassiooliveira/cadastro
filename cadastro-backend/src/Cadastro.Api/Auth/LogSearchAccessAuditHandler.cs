using Cadastro.Api.LogSearch;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Cadastro.Api.Auth;

/// <summary>
/// Registra na trilha de auditoria cada acesso negado (403) à pesquisa em logs e responde 403; qualquer outro
/// resultado segue o tratamento padrão. Só o <c>sub</c> vai ao log: sem tokens, tipo de dado nem motivo.
/// </summary>
internal sealed class LogSearchAccessAuditHandler(ILogger<LogSearchAccessAuditHandler> logger) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    /// <inheritdoc />
    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        var isLogSearch = context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(data => data.Policy == BffAuthenticationSetup.LogsInvestigatePolicy) == true;
        if (authorizeResult.Forbidden && isLogSearch)
        {
            LogSearchAudit.AccessDenied(logger, context.User.FindFirst(SessionPrincipal.SubjectClaim)?.Value ?? "unknown");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
