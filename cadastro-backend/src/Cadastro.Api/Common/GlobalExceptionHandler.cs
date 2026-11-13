using Cadastro.Application.Users;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Cadastro.Api.Common;

/// <summary>Ponto único de tratamento e log de exceções não esperadas (Art. VI.3), sem stack trace na resposta.</summary>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Converte a exceção em Problem Details: 400 (requisição malformada) ou 500.</summary>
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem;
        if (exception is BadHttpRequestException badRequest)
        {
            // Requisição malformada: responde 400, mas é exceção capturada → Error (regra de log).
            LogBadRequest(badRequest, badRequest.StatusCode);
            problem = new ProblemDetails { Status = badRequest.StatusCode, Title = "The request is malformed." };
        }
        else if (exception is IdentityProviderUnavailableException)
        {
            LogIdentityProviderUnavailable(exception);
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "The identity provider is temporarily unavailable.",
                Extensions = { ["code"] = "identity.unavailable" },
            };
        }
        else
        {
            LogUnhandled(exception);
            problem = new ProblemDetails { Status = StatusCodes.Status500InternalServerError, Title = "An unexpected error occurred." };
        }

        httpContext.Response.StatusCode = problem.Status.Value;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Malformed request rejected with status {StatusCode}")]
    private partial void LogBadRequest(Exception exception, int statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Identity provider unavailable")]
    private partial void LogIdentityProviderUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception while processing the request")]
    private partial void LogUnhandled(Exception exception);
}
