using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cadastro.Api.Auth;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Cadastro.Api.UserImports;
using Cadastro.Application.LogSearch;
using Cadastro.Application.UserImports;
using Cadastro.Domain.UserImports;
using Cadastro.Tests.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog.Core;
using Serilog.Events;

namespace Cadastro.Api.IntegrationTests.UserImports;

/// <summary>
/// <c>/api/v1/user-imports</c> com autenticação fake, repositório em memória e antivírus fake: 2xx, 400, 401, 403 auditado,
/// 404, 409, recusas do arquivo, ClamAV indisponível (recusa), confirmação com token errado, reenvio e cancelamento.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class UserImportEndpointTests(CadastroApiFactory factory) : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "cadastro-import-test-" + Guid.NewGuid().ToString("N"));
    private const string Header = "nome,email,cpf,telefone\n";
    private const string ValidCsv = Header + "Ana Souza,ana.souza@example.com,529.982.247-25,11999990000\nBeto Lima,beto.lima@example.com,111.444.777-35,\n";
    private static readonly Uri Route = new("/api/v1/user-imports", UriKind.Relative);
    private readonly string _admin = Guid.NewGuid().ToString();
    private readonly string _otherImporter = Guid.NewGuid().ToString();

    [Fact]
    public async Task Upload_ValidCsv_Returns201WithSummaryAndNoPersonalData()
    {
        await using var host = Host(out var store, out _);

        using var response = await UploadAsync(host, ValidCsv);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldNotContain("example.com");
        text.ShouldNotContain("52998224725");
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("validLines").GetInt32().ShouldBe(2);
        body.GetProperty("summaryToken").GetString().ShouldNotBeNullOrEmpty();
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/user-imports/{body.GetProperty("batchId").GetGuid()}");
        store.Batches.Single().Status.ShouldBe(ImportBatchStatus.AwaitingConfirmation);
        store.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Upload_WithoutSession_Returns401()
    {
        await using var host = Host(out _, out _);
        using var client = Client(host, null);
        using var content = Multipart(ValidCsv);

        using var response = await client.PostAsync(Route, content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("user", "otp", 10)]
    [InlineData("user-importer", "password", 10)]
    [InlineData("user-importer", null, 10)]
    [InlineData("user-importer", "otp", 360)]
    public async Task Endpoints_WithoutRoleOrFreshOtp_Return403AndWarnTheDenial(string role, string? acr, int ageSeconds)
    {
        await using var host = Host(out var store, out _);

        using var upload = await UploadAsync(host, ValidCsv, new TestIdentity(role, acr, ageSeconds));
        using var list = await SendAsync(host, HttpMethod.Get, Route, identity: new TestIdentity(role, acr, ageSeconds));

        upload.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        list.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        store.Batches.ShouldBeEmpty();
        var denied = AuditEvents(host).ToList();
        denied.Count.ShouldBe(2);
        denied.ShouldAllBe(e => e.Level == LogEventLevel.Warning);
        CollectingLogSink.Scalar(denied[0].Properties["audit_event"]).ShouldBe("user_import_access_denied");
        denied[0].RenderMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldNotContain("example.com");
    }

    [Fact]
    public async Task Upload_WithoutAntiforgeryToken_Returns400()
    {
        await using var host = Host(out var store, out _);
        using var client = Client(host, new TestIdentity());
        using var content = Multipart(ValidCsv);

        using var response = await client.PostAsync(Route, content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("antiforgery.invalid");
        store.Batches.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("nome,email\nAna,a@example.com\n", "file.header_invalid")]
    [InlineData("", "file.header_invalid")]
    public async Task Upload_InvalidFile_Returns422WithCodeAndCreatesNoBatch(string csv, string code)
    {
        await using var host = Host(out var store, out _);

        using var response = await UploadAsync(host, csv);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ProblemAsync(response)).GetProperty("code").GetString().ShouldBe(code);
        store.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Upload_InvalidUtf8_Returns422()
    {
        await using var host = Host(out _, out _);
        var bytes = Encoding.UTF8.GetBytes(ValidCsv).Concat(new byte[] { 0xFF, 0xFE }).ToArray();

        using var response = await UploadBytesAsync(host, bytes);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ProblemAsync(response)).GetProperty("code").GetString().ShouldBe("file.not_utf8");
    }

    [Fact]
    public async Task Upload_NameWithKeycloakProhibitedCharacter_MarksTheLineInvalidWithoutEchoingTheName()
    {
        await using var host = Host(out var store, out _);

        using var response = await UploadAsync(host, Header + "<Rejeitado>,rej@example.com,529.982.247-25,\nAna Souza,ana@example.com,111.444.777-35,\n");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldNotContain("Rejeitado");
        text.ShouldNotContain("rej@example.com");
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("validLines").GetInt32().ShouldBe(1);
        body.GetProperty("invalidLines").GetInt32().ShouldBe(1);
        var issue = body.GetProperty("issues").EnumerateArray().Single();
        (issue.GetProperty("line").GetInt32(), issue.GetProperty("code").GetString()).ShouldBe((1, "row.name_invalid"));
    }

    [Fact]
    public async Task Upload_FileWithoutValidLines_Returns422WithIssuesButNeverTheValues()
    {
        await using var host = Host(out var store, out _);

        using var response = await UploadAsync(host, Header + "Ana,not-an-email,123.456.789-00,\n");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldNotContain("not-an-email");
        text.ShouldNotContain("123.456.789-00");
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("code").GetString().ShouldBe("import.no_valid_lines");
        body.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("code").GetString()).ShouldBe(["row.email_invalid", "row.cpf_invalid"]);
        store.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Upload_OverFiveMegabytes_Returns413()
    {
        await using var host = Host(out var store, out _);
        var bytes = new byte[(int)ImportLimits.MaxFileBytes + 10];

        using var response = await UploadBytesAsync(host, bytes);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await ProblemAsync(response)).GetProperty("code").GetString().ShouldBe("file.too_large");
        store.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Upload_NotMultipart_Returns415()
    {
        await using var host = Host(out _, out _);

        using var response = await SendAsync(host, HttpMethod.Post, Route, JsonContent.Create(new { file = "x" }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Upload_MultipartWithoutFilePart_Returns400()
    {
        await using var host = Host(out _, out _);
        using var content = new MultipartFormDataContent { { new StringContent("x"), "other" } };

        using var response = await SendAsync(host, HttpMethod.Post, Route, content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemAsync(response)).GetProperty("code").GetString().ShouldBe("import.file_required");
    }

    [Fact]
    public async Task Upload_VirusScannerUnavailable_RefusesWith503AndCreatesNothing()
    {
        await using var host = Host(out var store, out var scanner);
        scanner.Mode = FakeVirusScanner.Behavior.Unavailable;

        using var response = await UploadAsync(host, ValidCsv);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ProblemAsync(response)).GetProperty("code").GetString().ShouldBe("scanner.unavailable");
        store.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Upload_InfectedFile_Returns422()
    {
        await using var host = Host(out var store, out var scanner);
        scanner.Mode = FakeVirusScanner.Behavior.Infected;

        using var response = await UploadAsync(host, ValidCsv);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ProblemAsync(response)).GetProperty("code").GetString().ShouldBe("file.infected");
        store.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Upload_SameFileAgain_Returns409WithTheExistingBatchAndCreatesNothing()
    {
        await using var host = Host(out var store, out _);
        var first = await CreateBatchAsync(host);

        using var response = await UploadAsync(host, ValidCsv);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ProblemAsync(response);
        problem.GetProperty("code").GetString().ShouldBe("import.duplicate_file");
        problem.GetProperty("batchId").GetGuid().ShouldBe(first.BatchId);
        problem.GetProperty("batch").GetProperty("status").GetString().ShouldBe("AwaitingConfirmation");
        store.Batches.Count.ShouldBe(1);
        store.Lines.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Upload_BeyondTheRateLimit_Returns429()
    {
        await using var host = Host(out _, out _, builder => builder.UseSetting("RateLimiting:UserImportsPermitsPerHour", "2"));

        (await UploadAsync(host, Header + "A,a1@example.com,52998224725,\n")).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await UploadAsync(host, Header + "B,b2@example.com,52998224725,\n")).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await UploadAsync(host, Header + "C,c3@example.com,52998224725,\n")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Confirm_WithRightToken_Returns202QueuesOneMessagePerPersonAndMovesToProcessing()
    {
        await using var host = Host(out var store, out _);
        var batch = await CreateBatchAsync(host);

        using var response = await ConfirmAsync(host, batch.BatchId, batch.SummaryToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("messages").GetInt32().ShouldBe(2);
        store.Batches.Single().Status.ShouldBe(ImportBatchStatus.Processing);
        store.Events.Count.ShouldBe(2);
        store.Events.ShouldAllBe(e => e.Payload.Contains(batch.BatchId.ToString(), StringComparison.Ordinal) && !e.Payload.Contains("example.com", StringComparison.Ordinal));
        store.Audits.Select(a => a.Action).ShouldBe(["batch.uploaded", "batch.confirmed"]);
        store.Audits.ShouldAllBe(a => a.Actor == _admin);
    }

    [Theory]
    [InlineData("wrong-token")]
    [InlineData(null)]
    public async Task Confirm_WithWrongOrMissingToken_Returns400AndChangesNothing(string? token)
    {
        await using var host = Host(out var store, out _);
        var batch = await CreateBatchAsync(host);

        using var response = await ConfirmAsync(host, batch.BatchId, token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemAsync(response)).GetProperty("code").GetString().ShouldBe("import.confirmation_token_invalid");
        store.Batches.Single().Status.ShouldBe(ImportBatchStatus.AwaitingConfirmation);
        store.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Confirm_TwiceOrUnknown_Returns409Or404()
    {
        await using var host = Host(out _, out _);
        var batch = await CreateBatchAsync(host);
        (await ConfirmAsync(host, batch.BatchId, batch.SummaryToken)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        using var again = await ConfirmAsync(host, batch.BatchId, batch.SummaryToken);
        using var unknown = await ConfirmAsync(host, Guid.NewGuid(), "x");

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemAsync(again)).GetProperty("code").GetString().ShouldBe("import.invalid_state");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancel_AwaitingBatch_Returns204ErasesDataAndAllowsResendingTheSameFile()
    {
        await using var host = Host(out var store, out _);
        var batch = await CreateBatchAsync(host);

        using var response = await SendAsync(host, HttpMethod.Post, new Uri($"/api/v1/user-imports/{batch.BatchId}/cancel", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        store.Batches.Single().Status.ShouldBe(ImportBatchStatus.Cancelled);
        store.Lines.ShouldAllBe(l => !l.HasPersonalData);
        (await UploadAsync(host, ValidCsv)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Cancel_ConfirmedOrUnknown_Returns409Or404()
    {
        await using var host = Host(out _, out _);
        var batch = await CreateBatchAsync(host);
        await ConfirmAsync(host, batch.BatchId, batch.SummaryToken);

        using var confirmed = await SendAsync(host, HttpMethod.Post, new Uri($"/api/v1/user-imports/{batch.BatchId}/cancel", UriKind.Relative));
        using var unknown = await SendAsync(host, HttpMethod.Post, new Uri($"/api/v1/user-imports/{Guid.NewGuid()}/cancel", UriKind.Relative));

        confirmed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Queries_ListDetailLinesAndReport_ReturnProgressWithoutPersonalData()
    {
        await using var host = Host(out _, out _);
        var batch = await CreateBatchAsync(host);
        var ct = TestContext.Current.CancellationToken;

        using var list = await SendAsync(host, HttpMethod.Get, new Uri("/api/v1/user-imports?page=1&pageSize=5", UriKind.Relative));
        using var detail = await SendAsync(host, HttpMethod.Get, new Uri($"/api/v1/user-imports/{batch.BatchId}", UriKind.Relative));
        using var lines = await SendAsync(host, HttpMethod.Get, new Uri($"/api/v1/user-imports/{batch.BatchId}/lines?status=Pending&page=1&pageSize=1", UriKind.Relative));
        using var report = await SendAsync(host, HttpMethod.Get, new Uri($"/api/v1/user-imports/{batch.BatchId}/report", UriKind.Relative));

        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await list.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("totalCount").GetInt32().ShouldBe(1);
        var detailBody = await detail.Content.ReadAsStringAsync(ct);
        JsonDocument.Parse(detailBody).RootElement.GetProperty("progress").GetProperty("pending").GetInt32().ShouldBe(2);
        detailBody.ShouldNotContain("example.com");
        var linesBody = JsonDocument.Parse(await lines.Content.ReadAsStringAsync(ct)).RootElement;
        linesBody.GetProperty("items").GetArrayLength().ShouldBe(1);
        linesBody.GetProperty("totalCount").GetInt32().ShouldBe(2);
        report.StatusCode.ShouldBe(HttpStatusCode.OK);
        report.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        report.Content.Headers.ContentDisposition!.FileNameStar.ShouldBe($"user-import-{batch.BatchId:D}.csv");
        var csv = await report.Content.ReadAsStringAsync(ct);
        csv.ShouldStartWith("line,status,failureReason,legacyMarkStatus,reprocessCount");
        csv.ShouldNotContain("example.com");
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(3);
    }

    [Theory]
    [InlineData("/api/v1/user-imports?page=0")]
    [InlineData("/api/v1/user-imports?pageSize=101")]
    [InlineData("/api/v1/user-imports/00000000-0000-0000-0000-000000000001/lines?pageSize=0")]
    public async Task Queries_InvalidPaging_Returns400(string url)
    {
        await using var host = Host(out _, out _);

        using var response = await SendAsync(host, HttpMethod.Get, new Uri(url, UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/lines")]
    [InlineData("/report")]
    public async Task Queries_UnknownBatch_Returns404(string suffix)
    {
        await using var host = Host(out _, out _);

        using var response = await SendAsync(host, HttpMethod.Get, new Uri($"/api/v1/user-imports/{Guid.NewGuid()}{suffix}", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reprocess_LineWithError_Returns202QueuesAndAudits_ThenRejectsNonErrorLines()
    {
        await using var host = Host(out var store, out _);
        var batch = await CreateBatchAsync(host);
        await ConfirmAsync(host, batch.BatchId, batch.SummaryToken);
        var failed = store.Lines[0];
        failed.MarkFailed("keycloak.unavailable", DateTimeOffset.UtcNow);
        store.Batches.Single().RecordLineOutcome(ImportLineStatus.Error, DateTimeOffset.UtcNow);
        var before = store.Events.Count;

        using var ok = await SendAsync(host, HttpMethod.Post, new Uri($"/api/v1/user-imports/{batch.BatchId}/lines/{failed.Id}/reprocess", UriKind.Relative));
        using var notError = await SendAsync(host, HttpMethod.Post, new Uri($"/api/v1/user-imports/{batch.BatchId}/lines/{store.Lines[1].Id}/reprocess", UriKind.Relative));
        using var unknown = await SendAsync(host, HttpMethod.Post, new Uri($"/api/v1/user-imports/{batch.BatchId}/lines/{Guid.NewGuid()}/reprocess", UriKind.Relative));

        ok.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        failed.Status.ShouldBe(ImportLineStatus.Pending);
        store.Events.Count.ShouldBe(before + 1);
        store.Audits.Last().Action.ShouldBe("line.reprocess-requested");
        notError.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReprocessErrors_ReopensEveryErrorLine()
    {
        await using var host = Host(out var store, out _);
        var batch = await CreateBatchAsync(host);
        await ConfirmAsync(host, batch.BatchId, batch.SummaryToken);
        foreach (var line in store.Lines)
        {
            line.MarkFailed("keycloak.rejected", DateTimeOffset.UtcNow);
            store.Batches.Single().RecordLineOutcome(ImportLineStatus.Error, DateTimeOffset.UtcNow);
        }

        using var response = await SendAsync(host, HttpMethod.Post, new Uri($"/api/v1/user-imports/{batch.BatchId}/reprocess-errors", UriKind.Relative));
        using var unknown = await SendAsync(host, HttpMethod.Post, new Uri($"/api/v1/user-imports/{Guid.NewGuid()}/reprocess-errors", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("messages").GetInt32().ShouldBe(2);
        store.Lines.ShouldAllBe(l => l.Status == ImportLineStatus.Pending);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private TestIdentity Other => new(Sub: _otherImporter);

    [Fact]
    public async Task OtherImporter_CannotConfirmCancelOrReprocessABatchItDidNotCreate_AndTheDenialIsWarned()
    {
        await using var host = Host(out var store, out _);
        var batch = await CreateBatchAsync(host);
        var line = store.Lines[0];

        using var confirm = await ConfirmAsync(host, batch.BatchId, batch.SummaryToken, Other);
        using var cancel = await SendAsync(host, HttpMethod.Post, new Uri($"/api/v1/user-imports/{batch.BatchId}/cancel", UriKind.Relative), identity: Other);
        using var reprocess = await SendAsync(host, HttpMethod.Post, new Uri($"/api/v1/user-imports/{batch.BatchId}/lines/{line.Id}/reprocess", UriKind.Relative), identity: Other);
        using var reprocessAll = await SendAsync(host, HttpMethod.Post, new Uri($"/api/v1/user-imports/{batch.BatchId}/reprocess-errors", UriKind.Relative), identity: Other);

        foreach (var response in new[] { confirm, cancel, reprocess, reprocessAll })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ProblemAsync(response)).GetProperty("code").GetString().ShouldBe("import.not_owner");
        }

        store.Batches.Single().Status.ShouldBe(ImportBatchStatus.AwaitingConfirmation);
        store.Events.ShouldBeEmpty();
        var warnings = host.Services.GetServices<ILogEventSink>().OfType<CollectingLogSink>().Single().Events
            .Where(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("not the batch owner", StringComparison.Ordinal)).ToList();
        warnings.Count.ShouldBeGreaterThanOrEqualTo(4);
        warnings.ShouldAllBe(e => !e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtherImporter_CanReadButTheOwnerStillConfirms()
    {
        await using var host = Host(out var store, out _);
        var batch = await CreateBatchAsync(host);

        using var detail = await SendAsync(host, HttpMethod.Get, new Uri($"/api/v1/user-imports/{batch.BatchId}", UriKind.Relative), identity: Other);
        using var list = await SendAsync(host, HttpMethod.Get, Route, identity: Other);
        using var lines = await SendAsync(host, HttpMethod.Get, new Uri($"/api/v1/user-imports/{batch.BatchId}/lines", UriKind.Relative), identity: Other);
        using var report = await SendAsync(host, HttpMethod.Get, new Uri($"/api/v1/user-imports/{batch.BatchId}/report", UriKind.Relative), identity: Other);
        using var owner = await ConfirmAsync(host, batch.BatchId, batch.SummaryToken);

        new[] { detail, list, lines, report }.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        owner.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        store.Batches.Single().Status.ShouldBe(ImportBatchStatus.Processing);
    }

    [Fact]
    public async Task SummaryToken_IsOnlyInTheUploadResponse_NeverInListOrDetail()
    {
        await using var host = Host(out _, out _);
        var batch = await CreateBatchAsync(host);

        var detail = await (await SendAsync(host, HttpMethod.Get, new Uri($"/api/v1/user-imports/{batch.BatchId}", UriKind.Relative))).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var list = await (await SendAsync(host, HttpMethod.Get, Route)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        detail.ToLowerInvariant().ShouldNotContain("token");
        list.ToLowerInvariant().ShouldNotContain("token");
        detail.ShouldNotContain(batch.SummaryToken);
    }

    [Fact]
    public async Task Confirm_WithTheTokenOfAnotherAuthorsBatch_Returns400()
    {
        await using var host = Host(out var store, out _);
        var mine = await CreateBatchAsync(host, Header + "Eu,eu@example.com,52998224725,\n");
        var theirs = await CreateBatchAsync(host, Header + "Elu,elu@example.com,11144477735,\n", Other);

        using var response = await ConfirmAsync(host, mine.BatchId, theirs.SummaryToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemAsync(response)).GetProperty("code").GetString().ShouldBe("import.confirmation_token_invalid");
        store.Batches.ShouldAllBe(b => b.Status == ImportBatchStatus.AwaitingConfirmation);
    }

    [Fact]
    public async Task TempFile_IsDeletedAfterSuccessAndEveryFailureKind()
    {
        // Sucesso, 422 do extrator, 400 sem a parte do arquivo, 413 por tamanho, 503 do antivírus e 500 por exceção.
        await using (var host = Host(out _, out _))
        {
            (await UploadAsync(host, ValidCsv)).StatusCode.ShouldBe(HttpStatusCode.Created);
            (await UploadAsync(host, "nome,email\n")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            using var noFile = new MultipartFormDataContent { { new StringContent("x"), "other" } };
            (await SendAsync(host, HttpMethod.Post, Route, noFile)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await UploadBytesAsync(host, new byte[(int)ImportLimits.MaxFileBytes + 10])).StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        }

        await using (var host = Host(out _, out var scanner))
        {
            scanner.Mode = FakeVirusScanner.Behavior.Unavailable;
            (await UploadAsync(host, ValidCsv)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        }

        await using (var host = Host(out _, out _, configure: services =>
        {
            services.RemoveAll<IUserExtractor>();
            services.AddSingleton<IUserExtractor, ThrowingExtractor>();
        }))
        {
            (await UploadAsync(host, ValidCsv)).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }

        await using (var host = Host(out _, out _, configure: services =>
        {
            services.RemoveAll<IUserImportWriter>();
            services.AddSingleton<IUserImportWriter, ThrowingWriter>();
        }))
        {
            (await UploadAsync(host, ValidCsv)).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }

        Directory.GetFiles(_tempDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Upload_ContentLengthFarOverTheLimit_Returns413WithoutReadingTheBody()
    {
        await using var host = Host(out var store, out _);

        using var response = await UploadBytesAsync(host, new byte[(int)ImportLimits.MaxFileBytes + 200 * 1024]);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await ProblemAsync(response)).GetProperty("code").GetString().ShouldBe("file.too_large");
        store.Batches.ShouldBeEmpty();
        Directory.GetFiles(_tempDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Upload_WhenTheServerBodyLimitIsExceededWhileReading_Returns413NotMalformed()
    {
        await using var host = Host(out _, out _, configure: services => services.AddSingleton<IStartupFilter, OversizedBodyStartupFilter>());

        using var response = await UploadAsync(host, ValidCsv);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await ProblemAsync(response)).GetProperty("code").GetString().ShouldBe("file.too_large");
        Directory.GetFiles(_tempDirectory).ShouldBeEmpty();
    }

    [Fact]
    public void CsvReport_NeutralizesFormulaInjectionAndQuotesSpecialCharacters()
    {
        UserImportCsvReport.Field("=1+1").ShouldBe("'=1+1");
        UserImportCsvReport.Field("+cmd").ShouldBe("'+cmd");
        UserImportCsvReport.Field("-2+3").ShouldBe("'-2+3");
        UserImportCsvReport.Field("@SUM(A1)").ShouldBe("'@SUM(A1)");
        UserImportCsvReport.Field("\tx").ShouldBe("'\tx");
        UserImportCsvReport.Field("keycloak.unavailable").ShouldBe("keycloak.unavailable");
        UserImportCsvReport.Field("a,b").ShouldBe("\"a,b\"");
        UserImportCsvReport.Field("say \"hi\"").ShouldBe("\"say \"\"hi\"\"\"");
        UserImportCsvReport.Field(null).ShouldBeEmpty();

        var csv = UserImportCsvReport.Write([new UserImportLineDto(Guid.NewGuid(), 7, "Error", "-2+3", "None", 1)]);

        csv.ShouldBe("line,status,failureReason,legacyMarkStatus,reprocessCount\n7,Error,'-2+3,None,1\n");
    }

    [Fact]
    public void AuditFieldNames_AreNotPseudonymizedFields()
    {
        PersonalDataFields.ByName.ShouldNotContainKey("audit_event");
        PersonalDataFields.ByName.ShouldNotContainKey("actor_id");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private WebApplicationFactory<Program> Host(
        out InMemoryUserImportStore store, out FakeVirusScanner scanner, Action<IWebHostBuilder>? extra = null, Action<IServiceCollection>? configure = null)
    {
        Directory.CreateDirectory(_tempDirectory);
        var memory = new InMemoryUserImportStore();
        var fakeScanner = new FakeVirusScanner();
        store = memory;
        scanner = fakeScanner;
        return factory.WithWebHostBuilder(builder =>
        {
            extra?.Invoke(builder);
            builder.UseSetting("UserImports:TempDirectory", _tempDirectory);
            builder.ConfigureTestServices(collection =>
            {
                var services = collection;
                services.RemoveAll<IUserImportWriter>();
                services.RemoveAll<IUserImportReadService>();
                services.RemoveAll<IVirusScanner>();
                services.AddSingleton<IUserImportWriter>(memory);
                services.AddSingleton<IUserImportReadService>(memory);
                services.AddSingleton<IVirusScanner>(fakeScanner);
                services.AddAuthentication(options =>
                    {
                        options.DefaultScheme = TestAuthHandler.SchemeName;
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                        options.DefaultForbidScheme = TestAuthHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                configure?.Invoke(services);
            });
        });
    }

    private IEnumerable<LogEvent> AuditEvents(WebApplicationFactory<Program> host) =>
        host.Services.GetServices<ILogEventSink>().OfType<CollectingLogSink>().Single().Events
            .Where(e => e.Properties.ContainsKey("audit_event") && e.Properties.ContainsKey("actor_id") && CollectingLogSink.Scalar(e.Properties["actor_id"]) == _admin);

    private HttpClient Client(WebApplicationFactory<Program> host, TestIdentity? identity)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });
        if (identity is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.SubHeader, identity.Sub ?? _admin);
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, identity.Role);
            if (identity.Acr is not null)
            {
                client.DefaultRequestHeaders.Add(TestAuthHandler.AcrHeader, identity.Acr);
            }

            var authTime = DateTimeOffset.UtcNow.AddSeconds(-identity.AuthAgeSeconds).ToUnixTimeSeconds();
            client.DefaultRequestHeaders.Add(TestAuthHandler.AuthTimeHeader, authTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return client;
    }

    // Envia a requisição já com o token anti-CSRF que o BFF entrega em /bff/user.
    private async Task<HttpResponseMessage> SendAsync(
        WebApplicationFactory<Program> host, HttpMethod method, Uri uri, HttpContent? content = null, TestIdentity? identity = null)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = Client(host, identity ?? new TestIdentity());
        var session = await client.GetFromJsonAsync<BffUserResponse>(new Uri("/bff/user", UriKind.Relative), cancellation);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Add(BffAuthenticationSetup.AntiforgeryHeaderName, session!.AntiforgeryToken);
        return await client.SendAsync(request, cancellation);
    }

    private static MultipartFormDataContent Multipart(string csv) => MultipartOf(Encoding.UTF8.GetBytes(csv));

    private static MultipartFormDataContent MultipartOf(byte[] bytes)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        return new MultipartFormDataContent { { file, "file", "users.csv" } };
    }

    private Task<HttpResponseMessage> UploadAsync(WebApplicationFactory<Program> host, string csv, TestIdentity? identity = null) =>
        UploadBytesAsync(host, Encoding.UTF8.GetBytes(csv), identity);

    private async Task<HttpResponseMessage> UploadBytesAsync(WebApplicationFactory<Program> host, byte[] bytes, TestIdentity? identity = null)
    {
        using var content = MultipartOf(bytes);
        return await SendAsync(host, HttpMethod.Post, Route, content, identity);
    }

    private async Task<UploadSummary> CreateBatchAsync(WebApplicationFactory<Program> host, string csv = ValidCsv, TestIdentity? identity = null)
    {
        using var response = await UploadAsync(host, csv, identity);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<UploadSummary>(TestContext.Current.CancellationToken))!;
    }

    private async Task<HttpResponseMessage> ConfirmAsync(WebApplicationFactory<Program> host, Guid batchId, string? token, TestIdentity? identity = null)
    {
        using var content = JsonContent.Create(new ConfirmUserImportRequest(token));
        return await SendAsync(host, HttpMethod.Post, new Uri($"/api/v1/user-imports/{batchId}/confirm", UriKind.Relative), content, identity);
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    /// <summary>Administrador fake: por padrão com a role <c>user-importer</c> e o OTP digitado agora.</summary>
    private sealed record TestIdentity(string Role = "user-importer", string? Acr = "otp", int AuthAgeSeconds = 10, string? Sub = null);

    /// <summary>Extrator que falha depois do spool, para provar que o arquivo temporário é apagado mesmo com exceção.</summary>
    private sealed class ThrowingExtractor : IUserExtractor
    {
        public async IAsyncEnumerable<ExtractedRow> ExtractAsync(Stream content, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("extractor down");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    /// <summary>Repositório que falha na primeira consulta, depois do spool e do antivírus.</summary>
    private sealed class ThrowingWriter : IUserImportWriter
    {
        public Task<Guid?> FindActiveBatchIdByHashAsync(string fileHash, CancellationToken cancellationToken) => throw new InvalidOperationException("writer down");

        public Task<ImportBatch?> FindBatchAsync(Guid batchId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ImportLine>> GetLinesAsync(Guid batchId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ImportLine?> FindLineAsync(Guid batchId, Guid lineId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ImportLine>> GetErrorLinesAsync(Guid batchId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddBatch(ImportBatch batch, IEnumerable<ImportLine> lines) => throw new NotSupportedException();

        public void AddAudit(ImportAuditEntry entry) => throw new NotSupportedException();

        public void EnqueueEvents(IEnumerable<IntegrationEvent> events) => throw new NotSupportedException();

        public void AddInboxMessage(Guid messageId, DateTimeOffset now) => throw new NotSupportedException();

        public Task<bool> HasProcessedMessageAsync(Guid messageId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ImportSaveResult> SaveChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>Troca o corpo das requisições de importação por um fluxo que estoura o limite do servidor ao ser lido (como o Kestrel faz).</summary>
    private sealed class OversizedBodyStartupFilter : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Method == HttpMethods.Post && context.Request.Path.StartsWithSegments("/api/v1/user-imports"))
                {
                    context.Request.Body = new OversizedBodyStream();
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class OversizedBodyStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Antivírus fake: limpo, infectado ou indisponível.</summary>
    private sealed class FakeVirusScanner : IVirusScanner
    {
        public enum Behavior
        {
            Clean,
            Infected,
            Unavailable,
        }

        public Behavior Mode { get; set; }

        public Task<VirusScanResult> ScanAsync(Stream content, CancellationToken cancellationToken) => Mode switch
        {
            Behavior.Infected => Task.FromResult(VirusScanResult.Infected),
            Behavior.Unavailable => throw new VirusScannerUnavailableException("The virus scanner is down."),
            _ => Task.FromResult(VirusScanResult.Clean),
        };
    }
}
