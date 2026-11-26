using Cadastro.Api.LogSearch;
using Cadastro.Api.UserImports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Cadastro.Api.Auth;

/// <summary>
/// Registra na trilha de auditoria cada acesso negado (403) às áreas sensíveis (pesquisa em logs e importação em lote) e
/// responde 403; qualquer outro resultado segue o tratamento padrão. Só o <c>sub</c> vai ao log: sem tokens nem motivo.
/// </summary>
internal sealed class AccessDeniedAuditHandler(ILogger<AccessDeniedAuditHandler> logger) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    /// <inheritdoc />
    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (authorizeResult.Forbidden)
        {
            var subject = context.User.FindFirst(SessionPrincipal.SubjectClaim)?.Value ?? "unknown";
            if (UsesPolicy(context, BffAuthenticationSetup.LogsInvestigatePolicy))
            {
                LogSearchAudit.AccessDenied(logger, subject);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            if (UsesPolicy(context, BffAuthenticationSetup.UserImportsPolicy))
            {
                UserImportAudit.AccessDenied(logger, subject);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }

    private static bool UsesPolicy(HttpContext context, string policyName) =>
        context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(data => data.Policy == policyName) == true;
}
