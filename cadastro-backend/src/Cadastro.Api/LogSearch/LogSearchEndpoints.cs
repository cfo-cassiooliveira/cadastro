using Cadastro.Api.Auth;
using Cadastro.Api.Common;
using Cadastro.Application.LogSearch;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Cadastro.Api.LogSearch;

/// <summary>Corpo de <c>POST /api/v1/log-search/queries</c>. O valor só trafega no corpo, nunca em URL.</summary>
/// <param name="DataType"><c>cpf</c>, <c>cnpj</c>, <c>phone</c>, <c>email</c> ou <c>login</c>.</param>
/// <param name="Value">Valor a pesquisar.</param>
/// <param name="CaseReference">Chamado/incidente que motiva a pesquisa (3 a 40 caracteres <c>[A-Za-z0-9._/-]</c>).</param>
public sealed record LogSearchRequest(string? DataType, string? Value, string? CaseReference);

/// <summary>Pesquisa em logs por token (ADR-0003 do graylog, item 7): gera a consulta do Graylog sem expor o valor.</summary>
internal static class LogSearchEndpoints
{
    /// <summary>Mapeia <c>POST /api/v1/log-search/queries</c> e <c>GET /api/v1/log-search/access</c>.</summary>
    public static IEndpointRouteBuilder MapLogSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/log-search").WithTags("LogSearch");
        group.MapPost("/queries", CreateQuery)
            .WithSummary("Generates the Graylog query for the pseudonymized tokens of a personal data value.")
            .RequireAuthorization(BffAuthenticationSetup.LogsInvestigatePolicy)
            .RequireRateLimiting(RateLimitPolicies.LogSearch)
            .AddEndpointFilter<AntiforgeryValidationFilter>()
            .Produces<LogSearchQueryDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // Sem corpo nem antiforgery: não muda estado nem recebe dado digitado. Serve para a tela registrar o acesso negado
        // (o 403 passa pelo AccessDeniedAuditHandler, que loga log_search_access_denied). O rate limit evita inundar a trilha.
        group.MapGet("/access", CheckAccess)
            .WithSummary("Checks whether the session may use log search; a 403 is audited as an access denial.")
            .RequireAuthorization(BffAuthenticationSetup.LogsInvestigatePolicy)
            .RequireRateLimiting(RateLimitPolicies.LogSearch)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        return app;
    }

    // Chegar aqui significa que a policy já autorizou.
    private static NoContent CheckAccess() => TypedResults.NoContent();

    // O valor nunca é registrado nem devolvido; os erros trazem só códigos por campo.
    private static Results<Ok<LogSearchQueryDto>, ValidationProblem> CreateQuery(
        LogSearchRequest request,
        HttpContext context,
        IGetLogSearchQueryHandler handler,
        ILogger<LogSearchRequest> logger)
    {
        var result = handler.Handle(new GetLogSearchQuery(request.DataType, request.Value, request.CaseReference));
        if (result.Query is not { } query)
        {
            return TypedResults.ValidationProblem(result.Errors.ToDictionary(pair => pair.Key, pair => pair.Value));
        }

        var investigatorId = context.User.FindFirst(SessionPrincipal.SubjectClaim)?.Value ?? "unknown";
        LogSearchAudit.Issued(logger, investigatorId,
            query.DataType, request.CaseReference!, string.Join(',', query.Tokens.Select(token => token.Token)));
        return TypedResults.Ok(query);
    }
}
