namespace Cadastro.Api.Common;

/// <summary>
/// Cabeçalhos de proteção nas respostas da API: <c>Cache-Control: no-store</c> para que dados pessoais e tokens
/// não fiquem em cache de navegador/proxy, e <c>nosniff</c> contra interpretação indevida de conteúdo.
/// </summary>
internal sealed class ApiResponseHeadersMiddleware(RequestDelegate next)
{
    private const string ApiPathPrefix = "/api";

    /// <summary>Registra os cabeçalhos antes de a resposta começar a ser enviada.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.Path.StartsWithSegments(ApiPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.CacheControl = "no-store";
                headers.Pragma = "no-cache";
                headers.XContentTypeOptions = "nosniff";
                return Task.CompletedTask;
            });
        }

        return next(context);
    }
}
