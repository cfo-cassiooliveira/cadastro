using Cadastro.Api.Auth;
using Cadastro.Application.Users;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Cadastro.Api.Users;

/// <summary>Endpoints de usuários (<c>/api/v1/users</c>).</summary>
internal static class UserEndpoints
{
    /// <summary>Mapeia <c>GET /api/v1/users/me</c>.</summary>
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/users").WithTags("Users");
        group.MapGet("/me", GetMeAsync).RequireAuthorization(BffAuthenticationSetup.UsersSelfPolicy);
        return app;
    }

    // O id vem só da sessão: não existe rota para ler o perfil de outra pessoa (IDOR).
    private static async Task<Results<Ok<MyProfile>, ProblemHttpResult>> GetMeAsync(
        HttpContext context,
        IGetMyProfileHandler handler,
        CancellationToken cancellationToken)
    {
        var userId = context.User.FindFirst(SessionPrincipal.SubjectClaim)?.Value ?? string.Empty;
        var profile = await handler.HandleAsync(userId, cancellationToken);
        return profile is null
            ? TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "The user no longer exists.",
                extensions: new Dictionary<string, object?> { ["code"] = "user.not_found" })
            : TypedResults.Ok(profile);
    }
}
