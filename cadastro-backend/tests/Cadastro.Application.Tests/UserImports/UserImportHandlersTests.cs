using System.Security.Cryptography;
using System.Text;
using Cadastro.Application.UserImports;
using Cadastro.Domain.UserImports;
using Cadastro.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Cadastro.Application.Tests.UserImports;

/// <summary>Casos de uso da importação: upload, confirmação, cancelamento, reprocesso e consultas, sobre o repositório em memória.</summary>
public sealed class UserImportHandlersTests
{
    private const string Header = "nome,email,cpf,telefone\n";
    private const string ValidCsv = Header + "Ana,ana@example.com,52998224725,\nBeto,beto@example.com,11144477735,11999990000\n";
    private static readonly ImportRequestContext Admin = new("admin-1", "corr-12345678", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

    private readonly InMemoryUserImportStore _store = new();
    private readonly IVirusScanner _scanner = Substitute.For<IVirusScanner>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 25, 13, 0, 0, TimeSpan.Zero));
    private readonly IPersonalDataProtector _protector = Substitute.For<IPersonalDataProtector>();
    private readonly HmacImportSummarySigner _signer = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly ImportRequestContext Other = new("admin-2", "corr-other-1", null);

    public UserImportHandlersTests()
    {
        _scanner.ScanAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(VirusScanResult.Clean);
        _protector.CurrentKeyVersion.Returns("k1");
        _protector.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns(call => Encoding.UTF8.GetBytes("enc:" + call.ArgAt<string>(0)));
        _protector.ComputeEmailIndex(Arg.Any<string>()).Returns(call => SHA256.HashData(Encoding.UTF8.GetBytes(call.ArgAt<string>(0))));
    }

    private UploadUserImportHandler Upload() =>
        new(_scanner, new CsvUserExtractor(), _protector, _signer, _store, _clock, NullLogger<UploadUserImportHandler>.Instance);

    private ConfirmUserImportHandler Confirm() => new(_store, _signer, _clock, NullLogger<ConfirmUserImportHandler>.Instance);

    private Task<UploadUserImportResult> UploadAsync(string csv) =>
        Upload().HandleAsync(new UploadUserImportCommand(new MemoryStream(Encoding.UTF8.GetBytes(csv)), Admin), TestContext.Current.CancellationToken);

    private async Task<UploadSummary> UploadValidAsync()
    {
        var result = await UploadAsync(ValidCsv);
        return result.Summary!;
    }

    [Fact]
    public async Task Upload_ValidFile_CreatesBatchAwaitingConfirmationWithEncryptedLines()
    {
        var result = await UploadAsync(ValidCsv);

        result.Outcome.ShouldBe(UploadOutcome.Created);
        result.Summary!.ValidLines.ShouldBe(2);
        var batch = _store.Batches.Single();
        batch.Status.ShouldBe(ImportBatchStatus.AwaitingConfirmation);
        batch.CreatedBy.ShouldBe("admin-1");
        batch.FileHash.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ValidCsv))));
        result.Summary.SummaryToken.ShouldBe(_signer.Sign(batch.Id, "admin-1", batch.FileHash, 2, 0, 0));
        _store.Lines.Count.ShouldBe(2);
        _store.Lines.ShouldAllBe(line => line.Status == ImportLineStatus.Pending && line.HasPersonalData);
        _store.Events.ShouldBeEmpty();
        _store.Audits.Single().Action.ShouldBe("batch.uploaded");
    }

    [Fact]
    public async Task Upload_BindsEachCipherToItsLineAndField()
    {
        await UploadAsync(ValidCsv);

        var cpfs = new Dictionary<int, string> { [1] = "52998224725", [2] = "11144477735" };
        foreach (var line in _store.Lines)
        {
            _protector.Received().Protect(cpfs[line.LineNumber], ImportLineProtection.Context(line.Id, ImportLineProtection.CpfField));
        }
    }

    [Fact]
    public async Task Upload_MixedFile_CountsValidInvalidAndDuplicateWithoutPersonalDataInTheSummary()
    {
        var csv = Header + "Ana,ana@example.com,52998224725,\nBad,not-an-email,52998224725,\nAna Dup,ana@example.com,11144477735,\nOk,ok@example.com,11144477735,\n";

        var summary = (await UploadAsync(csv)).Summary!;

        (summary.ValidLines, summary.InvalidLines, summary.DuplicateLines).ShouldBe((2, 1, 1));
        summary.Issues.ShouldBe([new ImportRowIssue(2, "row.email_invalid"), new ImportRowIssue(3, "row.duplicate_email")]);
        _store.Lines.Select(l => l.LineNumber).ShouldBe([1, 4]);
    }

    [Fact]
    public async Task Upload_SameFileTwice_ReturnsDuplicateWithTheExistingBatchAndCreatesNothing()
    {
        var first = await UploadValidAsync();

        var second = await UploadAsync(ValidCsv);

        second.Outcome.ShouldBe(UploadOutcome.Duplicate);
        second.ExistingBatchId.ShouldBe(first.BatchId);
        _store.Batches.Count.ShouldBe(1);
        _store.Lines.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Upload_SameFileAfterCancellation_IsAccepted()
    {
        var first = await UploadValidAsync();
        await new CancelUserImportHandler(_store, _clock, NullLogger<CancelUserImportHandler>.Instance)
            .HandleAsync(new CancelUserImportCommand(first.BatchId, Admin), TestContext.Current.CancellationToken);

        (await UploadAsync(ValidCsv)).Outcome.ShouldBe(UploadOutcome.Created);
    }

    [Fact]
    public async Task Upload_RaceOnTheUniqueIndex_ReturnsDuplicateOfTheWinner()
    {
        var winner = Guid.CreateVersion7();
        var writer = Substitute.For<IUserImportWriter>();
        writer.FindActiveBatchIdByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Guid?)null, (Guid?)winner);
        writer.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(ImportSaveResult.DuplicateFileHash);
        var handler = new UploadUserImportHandler(_scanner, new CsvUserExtractor(), _protector, _signer, writer, _clock, NullLogger<UploadUserImportHandler>.Instance);

        var result = await handler.HandleAsync(
            new UploadUserImportCommand(new MemoryStream(Encoding.UTF8.GetBytes(ValidCsv)), Admin), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(UploadOutcome.Duplicate);
        result.ExistingBatchId.ShouldBe(winner);
    }

    [Fact]
    public async Task Upload_ScannerUnavailable_RejectsAndCreatesNothing()
    {
        _scanner.ScanAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns<VirusScanResult>(_ => throw new VirusScannerUnavailableException("down"));

        var result = await UploadAsync(ValidCsv);

        result.Outcome.ShouldBe(UploadOutcome.Rejected);
        result.Code.ShouldBe("scanner.unavailable");
        _store.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Upload_ScannerThrowsUnexpectedError_StillRejects()
    {
        _scanner.ScanAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns<VirusScanResult>(_ => throw new InvalidOperationException("boom"));

        (await UploadAsync(ValidCsv)).Code.ShouldBe("scanner.unavailable");
    }

    [Fact]
    public async Task Upload_InfectedFile_IsRejected()
    {
        _scanner.ScanAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(VirusScanResult.Infected);

        (await UploadAsync(ValidCsv)).Code.ShouldBe("file.infected");
        _store.Batches.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("nome,email\nAna,a@example.com\n", "file.header_invalid")]
    [InlineData("", "file.header_invalid")]
    public async Task Upload_FileLevelError_IsRejectedWithItsCode(string csv, string code)
    {
        var result = await UploadAsync(csv);

        result.Outcome.ShouldBe(UploadOutcome.Rejected);
        result.Code.ShouldBe(code);
        _store.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Upload_WithoutValidLines_IsRejectedWithTheIssues()
    {
        var result = await UploadAsync(Header + "Ana,invalid,123,\n");

        result.Code.ShouldBe("import.no_valid_lines");
        result.Issues.Select(i => i.Code).ShouldBe(["row.email_invalid", "row.cpf_invalid"]);
        _store.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Upload_FileOverFiveMegabytes_IsRejectedBeforeScanning()
    {
        using var big = new MemoryStream(new byte[ImportLimits.MaxFileBytes + 1]);

        var result = await Upload().HandleAsync(new UploadUserImportCommand(big, Admin), TestContext.Current.CancellationToken);

        result.Code.ShouldBe("file.too_large");
        await _scanner.DidNotReceiveWithAnyArgs().ScanAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Upload_NonSeekableStream_Throws() =>
        await Should.ThrowAsync<ArgumentException>(() => Upload().HandleAsync(
            new UploadUserImportCommand(new ForwardOnlyStream(), Admin), TestContext.Current.CancellationToken));

    [Fact]
    public async Task Confirm_WithTheRightToken_MovesToProcessingAndQueuesOneMessagePerPersonWithoutPersonalData()
    {
        var summary = await UploadValidAsync();

        var result = await Confirm().HandleAsync(new ConfirmUserImportCommand(summary.BatchId, summary.SummaryToken, Admin), TestContext.Current.CancellationToken);

        result.ShouldBe(new ImportCommandResult(ImportCommandOutcome.Done, 2));
        _store.Batches.Single().Status.ShouldBe(ImportBatchStatus.Processing);
        _store.Events.Count.ShouldBe(2);
        _store.Events.Select(e => e.Type).Distinct().ShouldBe(["cadastro.user-import-line.accepted.v1"]);
        _store.Events.Select(e => e.MessageId).Distinct().Count().ShouldBe(2);
        _store.Events.ShouldAllBe(e => e.TraceParent == Admin.TraceParent);
        foreach (var message in _store.Events)
        {
            message.Payload.ShouldNotContain("ana@example.com");
            message.Payload.ShouldNotContain("52998224725");
            message.Payload.ShouldContain(summary.BatchId.ToString());
        }

        _store.Audits.Select(a => a.Action).ShouldBe(["batch.uploaded", "batch.confirmed"]);
    }

    [Fact]
    public async Task Confirm_InvalidLinesAreNeverQueued()
    {
        var csv = Header + "Ana,ana@example.com,52998224725,\nBad,x,1,\n";
        var summary = (await UploadAsync(csv)).Summary!;

        await Confirm().HandleAsync(new ConfirmUserImportCommand(summary.BatchId, summary.SummaryToken, Admin), TestContext.Current.CancellationToken);

        _store.Events.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Confirm_WithWrongToken_ChangesNothing(string? token)
    {
        var summary = await UploadValidAsync();

        var result = await Confirm().HandleAsync(new ConfirmUserImportCommand(summary.BatchId, token, Admin), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ImportCommandOutcome.InvalidToken);
        _store.Batches.Single().Status.ShouldBe(ImportBatchStatus.AwaitingConfirmation);
        _store.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Confirm_UnknownBatch_ReturnsNotFound() =>
        (await Confirm().HandleAsync(new ConfirmUserImportCommand(Guid.NewGuid(), "x", Admin), TestContext.Current.CancellationToken))
            .Outcome.ShouldBe(ImportCommandOutcome.NotFound);

    [Fact]
    public async Task Confirm_Twice_SecondIsInvalidStateAndQueuesNothingMore()
    {
        var summary = await UploadValidAsync();
        var command = new ConfirmUserImportCommand(summary.BatchId, summary.SummaryToken, Admin);
        await Confirm().HandleAsync(command, TestContext.Current.CancellationToken);

        var second = await Confirm().HandleAsync(command, TestContext.Current.CancellationToken);

        second.Outcome.ShouldBe(ImportCommandOutcome.InvalidState);
        _store.Events.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Confirm_ConcurrentUpdate_ReturnsConflict()
    {
        var summary = await UploadValidAsync();
        _store.ForcedSaveResult = ImportSaveResult.ConcurrencyConflict;

        var result = await Confirm().HandleAsync(new ConfirmUserImportCommand(summary.BatchId, summary.SummaryToken, Admin), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ImportCommandOutcome.Conflict);
        _store.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Confirm_ByAnotherImporter_IsRefusedEvenWithTheRightTokenAndChangesNothing()
    {
        var summary = await UploadValidAsync();

        var result = await Confirm().HandleAsync(new ConfirmUserImportCommand(summary.BatchId, summary.SummaryToken, Other), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ImportCommandOutcome.NotOwner);
        _store.Batches.Single().Status.ShouldBe(ImportBatchStatus.AwaitingConfirmation);
        _store.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Confirm_WithTheTokenOfAnotherBatch_IsRefused()
    {
        var first = await UploadValidAsync();
        var second = (await UploadAsync(Header + "Carla,carla@example.com,52998224725,\n")).Summary!;

        var result = await Confirm().HandleAsync(new ConfirmUserImportCommand(second.BatchId, first.SummaryToken, Admin), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ImportCommandOutcome.InvalidToken);
    }

    [Fact]
    public async Task Cancel_ByAnotherImporter_IsRefusedAndKeepsTheData()
    {
        var summary = await UploadValidAsync();

        var result = await new CancelUserImportHandler(_store, _clock, NullLogger<CancelUserImportHandler>.Instance)
            .HandleAsync(new CancelUserImportCommand(summary.BatchId, Other), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ImportCommandOutcome.NotOwner);
        _store.Batches.Single().Status.ShouldBe(ImportBatchStatus.AwaitingConfirmation);
        _store.Lines.ShouldAllBe(line => line.HasPersonalData);
    }

    [Fact]
    public async Task Reprocess_ByAnotherImporter_IsRefusedForLineAndForAllErrors()
    {
        var (batchId, failed) = await ConfirmAndFailOneLineAsync();
        var before = _store.Events.Count;

        var line = await new ReprocessUserImportLineHandler(_store, _clock, NullLogger<ReprocessUserImportLineHandler>.Instance)
            .HandleAsync(new ReprocessUserImportLineCommand(batchId, failed.Id, Other), TestContext.Current.CancellationToken);
        var errors = await new ReprocessUserImportErrorsHandler(_store, _clock, NullLogger<ReprocessUserImportErrorsHandler>.Instance)
            .HandleAsync(new ReprocessUserImportErrorsCommand(batchId, Other), TestContext.Current.CancellationToken);

        line.Outcome.ShouldBe(ImportCommandOutcome.NotOwner);
        errors.Outcome.ShouldBe(ImportCommandOutcome.NotOwner);
        failed.Status.ShouldBe(ImportLineStatus.Error);
        _store.Events.Count.ShouldBe(before);
    }

    [Fact]
    public async Task Queries_AreOpenToAnyImporter()
    {
        var summary = await UploadValidAsync();

        (await new GetUserImportHandler(_store).HandleAsync(summary.BatchId, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Cancel_AwaitingBatch_CancelsAndErasesPersonalData()
    {
        var summary = await UploadValidAsync();

        var result = await new CancelUserImportHandler(_store, _clock, NullLogger<CancelUserImportHandler>.Instance)
            .HandleAsync(new CancelUserImportCommand(summary.BatchId, Admin), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ImportCommandOutcome.Done);
        _store.Batches.Single().Status.ShouldBe(ImportBatchStatus.Cancelled);
        _store.Lines.ShouldAllBe(line => line.Status == ImportLineStatus.Cancelled && !line.HasPersonalData);
        _store.Audits.Last().Action.ShouldBe("batch.cancelled");
    }

    [Fact]
    public async Task Cancel_ConfirmedBatchOrUnknown_IsRejected()
    {
        var summary = await UploadValidAsync();
        await Confirm().HandleAsync(new ConfirmUserImportCommand(summary.BatchId, summary.SummaryToken, Admin), TestContext.Current.CancellationToken);
        var handler = new CancelUserImportHandler(_store, _clock, NullLogger<CancelUserImportHandler>.Instance);

        (await handler.HandleAsync(new CancelUserImportCommand(summary.BatchId, Admin), TestContext.Current.CancellationToken))
            .Outcome.ShouldBe(ImportCommandOutcome.InvalidState);
        (await handler.HandleAsync(new CancelUserImportCommand(Guid.NewGuid(), Admin), TestContext.Current.CancellationToken))
            .Outcome.ShouldBe(ImportCommandOutcome.NotFound);
    }

    private async Task<(Guid BatchId, ImportLine Failed)> ConfirmAndFailOneLineAsync()
    {
        var summary = await UploadValidAsync();
        await Confirm().HandleAsync(new ConfirmUserImportCommand(summary.BatchId, summary.SummaryToken, Admin), TestContext.Current.CancellationToken);
        var batch = _store.Batches.Single();
        var failed = _store.Lines[0];
        failed.MarkFailed("keycloak.unavailable", _clock.GetUtcNow());
        batch.RecordLineOutcome(ImportLineStatus.Error, _clock.GetUtcNow());
        return (summary.BatchId, failed);
    }

    [Fact]
    public async Task ReprocessLine_WithError_ReopensAuditsAndQueuesANewMessage()
    {
        var (batchId, failed) = await ConfirmAndFailOneLineAsync();
        var before = _store.Events.Count;

        var result = await new ReprocessUserImportLineHandler(_store, _clock, NullLogger<ReprocessUserImportLineHandler>.Instance)
            .HandleAsync(new ReprocessUserImportLineCommand(batchId, failed.Id, Admin), TestContext.Current.CancellationToken);

        result.ShouldBe(new ImportCommandResult(ImportCommandOutcome.Done, 1));
        failed.Status.ShouldBe(ImportLineStatus.Pending);
        failed.ReprocessCount.ShouldBe(1);
        _store.Events.Count.ShouldBe(before + 1);
        _store.Audits.Last().Action.ShouldBe("line.reprocess-requested");
    }

    [Fact]
    public async Task ReprocessLine_NotInError_ReturnsInvalidState()
    {
        var (batchId, _) = await ConfirmAndFailOneLineAsync();
        var healthy = _store.Lines[1];

        var result = await new ReprocessUserImportLineHandler(_store, _clock, NullLogger<ReprocessUserImportLineHandler>.Instance)
            .HandleAsync(new ReprocessUserImportLineCommand(batchId, healthy.Id, Admin), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ImportCommandOutcome.InvalidState);
    }

    [Fact]
    public async Task ReprocessLine_UnknownLineOrBatch_ReturnsNotFound()
    {
        var (batchId, _) = await ConfirmAndFailOneLineAsync();
        var handler = new ReprocessUserImportLineHandler(_store, _clock, NullLogger<ReprocessUserImportLineHandler>.Instance);

        (await handler.HandleAsync(new ReprocessUserImportLineCommand(batchId, Guid.NewGuid(), Admin), TestContext.Current.CancellationToken))
            .Outcome.ShouldBe(ImportCommandOutcome.NotFound);
        (await handler.HandleAsync(new ReprocessUserImportLineCommand(Guid.NewGuid(), Guid.NewGuid(), Admin), TestContext.Current.CancellationToken))
            .Outcome.ShouldBe(ImportCommandOutcome.NotFound);
    }

    [Fact]
    public async Task ReprocessErrors_ReopensEveryErrorLineAndQueuesThem()
    {
        var (batchId, _) = await ConfirmAndFailOneLineAsync();
        var second = _store.Lines[1];
        second.MarkFailed("keycloak.rejected", _clock.GetUtcNow());
        _store.Batches.Single().RecordLineOutcome(ImportLineStatus.Error, _clock.GetUtcNow());
        var before = _store.Events.Count;

        var result = await new ReprocessUserImportErrorsHandler(_store, _clock, NullLogger<ReprocessUserImportErrorsHandler>.Instance)
            .HandleAsync(new ReprocessUserImportErrorsCommand(batchId, Admin), TestContext.Current.CancellationToken);

        result.ShouldBe(new ImportCommandResult(ImportCommandOutcome.Done, 2));
        _store.Lines.ShouldAllBe(line => line.Status == ImportLineStatus.Pending);
        _store.Batches.Single().Status.ShouldBe(ImportBatchStatus.Processing);
        _store.Events.Count.ShouldBe(before + 2);
        _store.Audits.Count(a => a.Action == "line.reprocess-requested").ShouldBe(2);
    }

    [Fact]
    public async Task ReprocessErrors_WithoutErrors_DoesNothing()
    {
        var summary = await UploadValidAsync();
        await Confirm().HandleAsync(new ConfirmUserImportCommand(summary.BatchId, summary.SummaryToken, Admin), TestContext.Current.CancellationToken);
        var before = _store.Events.Count;

        var result = await new ReprocessUserImportErrorsHandler(_store, _clock, NullLogger<ReprocessUserImportErrorsHandler>.Instance)
            .HandleAsync(new ReprocessUserImportErrorsCommand(summary.BatchId, Admin), TestContext.Current.CancellationToken);

        result.ShouldBe(new ImportCommandResult(ImportCommandOutcome.Done, 0));
        _store.Events.Count.ShouldBe(before);
    }

    [Fact]
    public async Task ReprocessErrors_UnknownBatch_ReturnsNotFound() =>
        (await new ReprocessUserImportErrorsHandler(_store, _clock, NullLogger<ReprocessUserImportErrorsHandler>.Instance)
            .HandleAsync(new ReprocessUserImportErrorsCommand(Guid.NewGuid(), Admin), TestContext.Current.CancellationToken))
            .Outcome.ShouldBe(ImportCommandOutcome.NotFound);

    [Fact]
    public async Task Queries_ListGetLinesAndReport_ExposeNoPersonalData()
    {
        var summary = await UploadValidAsync();
        var ct = TestContext.Current.CancellationToken;

        var page = await new ListUserImportsHandler(_store).HandleAsync(new ListUserImportsQuery(1, 10), ct);
        var detail = await new GetUserImportHandler(_store).HandleAsync(summary.BatchId, ct);
        var lines = await new ListUserImportLinesHandler(_store).HandleAsync(new ListUserImportLinesQuery(summary.BatchId, null, 1, 1), ct);
        var report = await new GetUserImportReportHandler(_store).HandleAsync(summary.BatchId, ct);

        page.TotalCount.ShouldBe(1);
        detail!.Progress.Pending.ShouldBe(2);
        lines!.Items.Count.ShouldBe(1);
        lines.TotalCount.ShouldBe(2);
        report!.Count.ShouldBe(2);
        System.Text.Json.JsonSerializer.Serialize(new object?[] { page, detail, lines, report }).ShouldNotContain("example.com");
    }

    [Fact]
    public async Task Queries_UnknownBatch_ReturnNull()
    {
        var ct = TestContext.Current.CancellationToken;

        (await new GetUserImportHandler(_store).HandleAsync(Guid.NewGuid(), ct)).ShouldBeNull();
        (await new ListUserImportLinesHandler(_store).HandleAsync(new ListUserImportLinesQuery(Guid.NewGuid(), null, 1, 10), ct)).ShouldBeNull();
        (await new GetUserImportReportHandler(_store).HandleAsync(Guid.NewGuid(), ct)).ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task Queries_InvalidPaging_Throws(int page, int pageSize)
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => new ListUserImportsHandler(_store).HandleAsync(new ListUserImportsQuery(page, pageSize), TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => new ListUserImportLinesHandler(_store).HandleAsync(new ListUserImportLinesQuery(Guid.NewGuid(), null, page, pageSize), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1, 20, true)]
    [InlineData(0, 20, false)]
    [InlineData(1, 101, false)]
    public void Paging_IsValid(int page, int size, bool expected) => ImportPaging.IsValid(page, size).ShouldBe(expected);

    /// <summary>Fluxo sem posicionamento, para provar que o upload exige um arquivo temporário e não um fluxo de rede.</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
