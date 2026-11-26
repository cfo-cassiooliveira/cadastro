using System.Diagnostics;
using System.Text;
using Cadastro.Api.Auth;
using Cadastro.Api.Common;
using Cadastro.Application.UserImports;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Cadastro.Api.UserImports;

/// <summary>Corpo da confirmação: o token do resumo devolvido no upload.</summary>
/// <param name="SummaryToken">Token do resumo.</param>
public sealed record ConfirmUserImportRequest(string? SummaryToken);

/// <summary>Resposta de comandos que enfileiram mensagens.</summary>
/// <param name="Messages">Mensagens gravadas na outbox (linhas confirmadas ou reabertas).</param>
public sealed record UserImportQueuedResponse(int Messages);

/// <summary>Endpoints da importação em lote (<c>/api/v1/user-imports</c>): role <c>user-importer</c> + OTP recente.</summary>
internal static class UserImportEndpoints
{
    private const string CsvContentType = "text/csv; charset=utf-8";
    private const string FilePart = "file";

    // Folga do multipart (boundary e cabeçalhos da parte) sobre o limite do arquivo.
    private const long MultipartOverheadBytes = 64 * 1024;

    /// <summary>Mapeia as rotas de upload, confirmação, cancelamento, consulta, relatório e reprocessamento.</summary>
    public static IEndpointRouteBuilder MapUserImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/user-imports").WithTags("UserImports")
            .RequireAuthorization(BffAuthenticationSetup.UserImportsPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/", UploadAsync)
            .WithSummary("Uploads a CSV (multipart field 'file'); creates a batch awaiting confirmation.")
            .RequireRateLimiting(RateLimitPolicies.UserImports)
            .AddEndpointFilter<AntiforgeryValidationFilter>()
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<UploadSummary>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/", ListAsync).WithSummary("Lists batches (paginated).");
        group.MapGet("/{id:guid}", GetAsync).WithSummary("Batch detail with progress by line status.");
        group.MapGet("/{id:guid}/lines", ListLinesAsync).WithSummary("Lines of a batch (paginated, no personal data).");
        group.MapGet("/{id:guid}/report", ReportAsync).WithSummary("Per-line report as CSV (no personal data).");

        group.MapPost("/{id:guid}/confirm", ConfirmAsync)
            .WithSummary("Confirms the batch with the summary token and queues one message per person.")
            .AddEndpointFilter<AntiforgeryValidationFilter>();
        group.MapPost("/{id:guid}/cancel", CancelAsync)
            .WithSummary("Cancels a batch awaiting confirmation.")
            .AddEndpointFilter<AntiforgeryValidationFilter>();
        group.MapPost("/{id:guid}/lines/{lineId:guid}/reprocess", ReprocessLineAsync)
            .WithSummary("Reprocesses one line with a definitive error.")
            .AddEndpointFilter<AntiforgeryValidationFilter>();
        group.MapPost("/{id:guid}/reprocess-errors", ReprocessErrorsAsync)
            .WithSummary("Reprocesses every line with a definitive error.")
            .AddEndpointFilter<AntiforgeryValidationFilter>();
        return app;
    }

    // O corpo é lido em streaming (MultipartReader) para um arquivo temporário apagado ao fechar: nunca inteiro em memória.
    private static async Task<IResult> UploadAsync(
        HttpContext context, IUploadUserImportHandler handler, IGetUserImportHandler getHandler, IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (!request.HasFormContentType || !MediaTypeHeaderValue.TryParse(request.ContentType, out var media)
            || !request.ContentType!.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status415UnsupportedMediaType, "import.multipart_required", "The upload must be multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(media.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return Problem(StatusCodes.Status400BadRequest, "import.multipart_invalid", "The multipart boundary is missing.");
        }

        // Recusa já pelo Content-Length declarado: nem começa a ler um corpo que passa do limite do arquivo mais a folga do multipart.
        if (request.ContentLength > ImportLimits.MaxFileBytes + MultipartOverheadBytes)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "file.too_large", "The file exceeds the size limit.");
        }

        var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false })
        {
            bodyLimit.MaxRequestBodySize = ImportLimits.MaxFileBytes + MultipartOverheadBytes;
        }

        await using var spool = new FileStream(
            Path.Combine(TempDirectory(configuration), $"cadastro-import-{Guid.NewGuid():N}.tmp"),
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
            });
        try
        {
            if (await SpoolFilePartAsync(request.Body, boundary, spool, cancellationToken) is { } failure)
            {
                return failure;
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // O Kestrel estourou o limite do corpo durante a leitura (corpo sem Content-Length ou maior que o declarado).
            return Problem(StatusCodes.Status413PayloadTooLarge, "file.too_large", "The file exceeds the size limit.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            return Problem(StatusCodes.Status400BadRequest, "import.multipart_invalid", "The multipart body is malformed.");
        }

        var import = ImportContext(context);
        var result = await handler.HandleAsync(new UploadUserImportCommand(spool, import), cancellationToken);
        return result.Outcome switch
        {
            UploadOutcome.Created => Results.Created($"/api/v1/user-imports/{result.Summary!.BatchId}", result.Summary),
            UploadOutcome.Duplicate => await DuplicateAsync(result.ExistingBatchId!.Value, getHandler, cancellationToken),
            _ => Rejected(result),
        };
    }

    // Copia a parte "file" para o arquivo temporário respeitando o limite; devolve a resposta de erro ou null.
    private static async Task<IResult?> SpoolFilePartAsync(Stream body, string boundary, FileStream spool, CancellationToken cancellationToken)
    {
        var reader = new MultipartReader(boundary, body);
        while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
        {
            var disposition = ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var parsed) ? parsed : null;
            if (disposition is null || !disposition.IsFileDisposition()
                || !string.Equals(HeaderUtilities.RemoveQuotes(disposition.Name).Value, FilePart, StringComparison.Ordinal))
            {
                continue;
            }

            var buffer = new byte[81920];
            int read;
            while ((read = await section.Body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (spool.Length + read > ImportLimits.MaxFileBytes)
                {
                    return Problem(StatusCodes.Status413PayloadTooLarge, "file.too_large", "The file exceeds the size limit.");
                }

                await spool.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            spool.Position = 0;
            return null;
        }

        return Problem(StatusCodes.Status400BadRequest, "import.file_required", "The multipart field 'file' is required.");
    }

    private static async Task<IResult> DuplicateAsync(Guid existingId, IGetUserImportHandler getHandler, CancellationToken cancellationToken)
    {
        var batch = await getHandler.HandleAsync(existingId, cancellationToken);
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "This file was already imported.",
            extensions: new Dictionary<string, object?> { ["code"] = "import.duplicate_file", ["batchId"] = existingId, ["batch"] = batch });
    }

    private static IResult Rejected(UploadUserImportResult result)
    {
        var status = result.Code switch
        {
            "file.too_large" => StatusCodes.Status413PayloadTooLarge,
            "scanner.unavailable" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status422UnprocessableEntity,
        };
        var extensions = new Dictionary<string, object?> { ["code"] = result.Code };
        if (result.Issues.Count > 0)
        {
            extensions["issues"] = result.Issues;
        }

        return Results.Problem(statusCode: status, title: "The file was rejected.", extensions: extensions);
    }

    private static async Task<IResult> ListAsync(
        IListUserImportsHandler handler, CancellationToken cancellationToken, int page = 1, int pageSize = ImportPaging.DefaultPageSize)
    {
        if (!ImportPaging.IsValid(page, pageSize))
        {
            return PagingProblem();
        }

        return Results.Ok(await handler.HandleAsync(new ListUserImportsQuery(page, pageSize), cancellationToken));
    }

    private static async Task<IResult> GetAsync(Guid id, IGetUserImportHandler handler, CancellationToken cancellationToken) =>
        await handler.HandleAsync(id, cancellationToken) is { } batch ? Results.Ok(batch) : NotFound();

    private static async Task<IResult> ListLinesAsync(
        Guid id, IListUserImportLinesHandler handler, CancellationToken cancellationToken,
        string? status = null, int page = 1, int pageSize = ImportPaging.DefaultPageSize)
    {
        if (!ImportPaging.IsValid(page, pageSize))
        {
            return PagingProblem();
        }

        return await handler.HandleAsync(new ListUserImportLinesQuery(id, status, page, pageSize), cancellationToken) is { } lines
            ? Results.Ok(lines)
            : NotFound();
    }

    private static async Task<IResult> ReportAsync(Guid id, IGetUserImportReportHandler handler, CancellationToken cancellationToken)
    {
        if (await handler.HandleAsync(id, cancellationToken) is not { } lines)
        {
            return NotFound();
        }

        return Results.File(Encoding.UTF8.GetBytes(UserImportCsvReport.Write(lines)), CsvContentType, $"user-import-{id:D}.csv");
    }

    private static async Task<IResult> ConfirmAsync(
        Guid id, ConfirmUserImportRequest? request, HttpContext context, IConfirmUserImportHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ConfirmUserImportCommand(id, request?.SummaryToken, ImportContext(context)), cancellationToken);
        return result.Outcome == ImportCommandOutcome.Done
            ? Results.Accepted(value: new UserImportQueuedResponse(result.Count))
            : CommandProblem(result.Outcome);
    }

    private static async Task<IResult> CancelAsync(Guid id, HttpContext context, ICancelUserImportHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CancelUserImportCommand(id, ImportContext(context)), cancellationToken);
        return result.Outcome == ImportCommandOutcome.Done ? Results.NoContent() : CommandProblem(result.Outcome);
    }

    private static async Task<IResult> ReprocessLineAsync(
        Guid id, Guid lineId, HttpContext context, IReprocessUserImportLineHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ReprocessUserImportLineCommand(id, lineId, ImportContext(context)), cancellationToken);
        return result.Outcome == ImportCommandOutcome.Done
            ? Results.Accepted(value: new UserImportQueuedResponse(result.Count))
            : CommandProblem(result.Outcome);
    }

    private static async Task<IResult> ReprocessErrorsAsync(
        Guid id, HttpContext context, IReprocessUserImportErrorsHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ReprocessUserImportErrorsCommand(id, ImportContext(context)), cancellationToken);
        return result.Outcome == ImportCommandOutcome.Done
            ? Results.Accepted(value: new UserImportQueuedResponse(result.Count))
            : CommandProblem(result.Outcome);
    }

    private static ImportRequestContext ImportContext(HttpContext context) => new(
        context.User.FindFirst(SessionPrincipal.SubjectClaim)?.Value ?? "unknown",
        context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString() is { Length: > 0 } correlation ? correlation : null,
        Activity.Current?.Id);

    private static IResult CommandProblem(ImportCommandOutcome outcome) => outcome switch
    {
        ImportCommandOutcome.NotFound => NotFound(),
        ImportCommandOutcome.InvalidToken => Problem(
            StatusCodes.Status400BadRequest, "import.confirmation_token_invalid", "The summary token does not match this batch."),
        ImportCommandOutcome.InvalidState => Problem(
            StatusCodes.Status409Conflict, "import.invalid_state", "The batch or line state does not allow this operation."),
        ImportCommandOutcome.NotOwner => Problem(
            StatusCodes.Status403Forbidden, "import.not_owner", "Only the administrator who created the batch can change it."),
        _ => Problem(StatusCodes.Status409Conflict, "import.concurrent_update", "The batch was changed by another request; try again."),
    };

    private static string TempDirectory(IConfiguration configuration) =>
        configuration["UserImports:TempDirectory"] is { Length: > 0 } configured ? configured : Path.GetTempPath();

    private static IResult NotFound() => Problem(StatusCodes.Status404NotFound, "import.not_found", "The batch or line was not found.");

    private static IResult PagingProblem() => Results.ValidationProblem(new Dictionary<string, string[]>
    {
        ["page"] = ["paging.invalid"],
        ["pageSize"] = ["paging.invalid"],
    });

    private static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
