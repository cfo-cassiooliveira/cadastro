using Microsoft.AspNetCore.Antiforgery;

namespace Cadastro.Api.Auth;

/// <summary>
/// Exige o token anti-CSRF (header <c>X-XSRF-TOKEN</c> + cookie HttpOnly) em chamadas que mudam estado. O cookie de sessão
/// <c>SameSite=Strict</c> já barra a maior parte do CSRF; o token é a segunda camada (security.md).
/// </summary>
internal sealed partial class AntiforgeryValidationFilter(IAntiforgery antiforgery, ILogger<AntiforgeryValidationFilter> logger)
    : IEndpointFilter
{
    /// <summary>Recusa com 400 (código <c>antiforgery.invalid</c>) quando o token falta ou não confere.</summary>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (!await antiforgery.IsRequestValidAsync(context.HttpContext))
        {
            LogRejected(context.HttpContext.Request.Path);
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The anti-forgery token is missing or invalid.",
                extensions: new Dictionary<string, object?> { ["code"] = "antiforgery.invalid" });
        }

        return await next(context);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Access denied: invalid anti-forgery token on {Path}")]
    private partial void LogRejected(PathString path);
}
