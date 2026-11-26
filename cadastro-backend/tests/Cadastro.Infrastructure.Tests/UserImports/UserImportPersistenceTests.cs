using Cadastro.Application.UserImports;
using Cadastro.Domain.UserImports;
using Cadastro.Infrastructure.Persistence;
using Cadastro.Infrastructure.Tests.Persistence;
using Cadastro.Infrastructure.UserImports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cadastro.Infrastructure.Tests.UserImports;

/// <summary>Escrita e leitura da importação contra SQL Server real: índice único, transação com a outbox e consultas sem dado pessoal.</summary>
[Collection(ImportDatabaseSharing.Name)]
public sealed class UserImportPersistenceTests(ImportDatabaseFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 13, 0, 0, TimeSpan.Zero);

    private static string Hash() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());

    private static ProtectedPersonalData Data(byte seed) => new([seed, 1], [seed, 2], [seed, 3], null, Enumerable.Repeat(seed, 32).ToArray(), "k1");

    private async Task<(CadastroDbContext Db, UserImportWriter Writer)> OpenAsync()
    {
        await fixture.MigrateAsync();
        var db = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        return (db, new UserImportWriter(db, NullLogger<UserImportWriter>.Instance));
    }

    private static (ImportBatch Batch, List<ImportLine> Lines) NewBatch(string fileHash, int lines = 3)
    {
        var batch = ImportBatch.Create(Guid.CreateVersion7(), fileHash, "admin-1", lines, 1, 0, Now);
        var list = Enumerable.Range(1, lines).Select(i => ImportLine.Create(Guid.CreateVersion7(), batch.Id, i, Data((byte)i), Now)).ToList();
        return (batch, list);
    }

    private static async Task<ImportSaveResult> SaveNewAsync(UserImportWriter writer, ImportBatch batch, IEnumerable<ImportLine> lines)
    {
        writer.AddBatch(batch, lines);
        writer.AddAudit(ImportAuditEntry.Create(Guid.CreateVersion7(), "admin-1", "batch.uploaded", batch.Id, "corr-1", Now));
        return await writer.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AddBatch_PersistsBatchLinesAndAuditEncryptedAndFindsByHash()
    {
        var (db, writer) = await OpenAsync();
        await using var _ = db;
        var (batch, lines) = NewBatch(Hash());

        (await SaveNewAsync(writer, batch, lines)).ShouldBe(ImportSaveResult.Saved);

        (await writer.FindActiveBatchIdByHashAsync(batch.FileHash, TestContext.Current.CancellationToken)).ShouldBe(batch.Id);
        await using var check = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        (await check.ImportLines.CountAsync(l => l.BatchId == batch.Id, TestContext.Current.CancellationToken)).ShouldBe(3);
        (await check.AuditEntries.CountAsync(a => a.BatchId == batch.Id, TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task AddBatch_SameHashTwice_ReturnsDuplicateAndStoresNothingFromTheSecond()
    {
        var (db1, writer1) = await OpenAsync();
        var (db2, writer2) = await OpenAsync();
        await using var _1 = db1;
        await using var _2 = db2;
        var hash = Hash();
        var first = NewBatch(hash);
        var second = NewBatch(hash);
        await SaveNewAsync(writer1, first.Batch, first.Lines);

        var result = await SaveNewAsync(writer2, second.Batch, second.Lines);

        result.ShouldBe(ImportSaveResult.DuplicateFileHash);
        await using var check = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        (await check.ImportBatches.AnyAsync(b => b.Id == second.Batch.Id, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await check.ImportLines.AnyAsync(l => l.BatchId == second.Batch.Id, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task CancelledBatch_ReleasesTheHashForANewUpload()
    {
        var (db, writer) = await OpenAsync();
        await using var _ = db;
        var hash = Hash();
        var (batch, lines) = NewBatch(hash);
        await SaveNewAsync(writer, batch, lines);
        batch.Cancel(lines, Now);
        await writer.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await writer.FindActiveBatchIdByHashAsync(hash, TestContext.Current.CancellationToken)).ShouldBeNull();
        var again = NewBatch(hash);
        (await SaveNewAsync(writer, again.Batch, again.Lines)).ShouldBe(ImportSaveResult.Saved);
    }

    [Fact]
    public async Task Confirm_WritesOneOutboxRowPerPersonInTheSameTransactionAsTheBatchChange()
    {
        var (db, writer) = await OpenAsync();
        await using var _ = db;
        var (batch, lines) = NewBatch(Hash(), lines: 4);
        await SaveNewAsync(writer, batch, lines);
        var (db2, writer2) = await OpenAsync();
        await using var _2 = db2;

        var loaded = (await writer2.FindBatchAsync(batch.Id, TestContext.Current.CancellationToken))!;
        var loadedLines = await writer2.GetLinesAsync(batch.Id, TestContext.Current.CancellationToken);
        loaded.Confirm(Now);
        writer2.EnqueueEvents(loadedLines.Select(l => new IntegrationEvent(
            Guid.CreateVersion7(), ImportLimits.LineAcceptedEventType, EventEnvelope.CreateLineAccepted(Guid.NewGuid(), batch.Id, l.Id, "c", "d", Now), "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", Now)));
        (await writer2.SaveChangesAsync(TestContext.Current.CancellationToken)).ShouldBe(ImportSaveResult.Saved);

        await using var check = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        (await check.ImportBatches.SingleAsync(b => b.Id == batch.Id, TestContext.Current.CancellationToken)).Status.ShouldBe(ImportBatchStatus.Processing);
        var rows = await check.OutboxMessages.Where(m => m.Payload.Contains(batch.Id.ToString())).ToListAsync(TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(4);
        rows.ShouldAllBe(m => m.PublishedAtUtc == null && m.Type == "cadastro.user-import-line.accepted.v1");
    }

    [Fact]
    public async Task Confirm_LosingTheConcurrentRace_RollsBackItsOutboxRowsToo()
    {
        var (db, writer) = await OpenAsync();
        await using var _ = db;
        var (batch, lines) = NewBatch(Hash(), lines: 2);
        await SaveNewAsync(writer, batch, lines);
        var (dbA, writerA) = await OpenAsync();
        var (dbB, writerB) = await OpenAsync();
        await using var _a = dbA;
        await using var _b = dbB;
        var batchA = (await writerA.FindBatchAsync(batch.Id, TestContext.Current.CancellationToken))!;
        var batchB = (await writerB.FindBatchAsync(batch.Id, TestContext.Current.CancellationToken))!;
        batchA.Confirm(Now);
        batchB.Confirm(Now);
        writerA.EnqueueEvents([NewEvent(batch.Id, "A")]);
        writerB.EnqueueEvents([NewEvent(batch.Id, "B"), NewEvent(batch.Id, "B")]);

        (await writerA.SaveChangesAsync(TestContext.Current.CancellationToken)).ShouldBe(ImportSaveResult.Saved);
        (await writerB.SaveChangesAsync(TestContext.Current.CancellationToken)).ShouldBe(ImportSaveResult.ConcurrencyConflict);

        await using var check = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        var rows = await check.OutboxMessages.Where(m => m.Payload.Contains(batch.Id.ToString())).ToListAsync(TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(1);
        rows.Single().Payload.ShouldContain("\"A\"");
    }

    [Fact]
    public async Task ReadService_ListsDetailsLinesAndReportWithProgressAndNoPersonalColumns()
    {
        var (db, writer) = await OpenAsync();
        await using var _ = db;
        var (batch, lines) = NewBatch(Hash(), lines: 3);
        await SaveNewAsync(writer, batch, lines);
        var loaded = (await writer.FindBatchAsync(batch.Id, TestContext.Current.CancellationToken))!;
        loaded.Confirm(Now);
        var tracked = await writer.GetLinesAsync(batch.Id, TestContext.Current.CancellationToken);
        tracked[0].MarkCreated(Now);
        loaded.RecordLineOutcome(ImportLineStatus.Created, Now);
        tracked[1].MarkFailed("keycloak.unavailable", Now);
        loaded.RecordLineOutcome(ImportLineStatus.Error, Now);
        await writer.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using var readDb = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        var read = new UserImportReadService(readDb);
        var ct = TestContext.Current.CancellationToken;

        var detail = (await read.GetBatchAsync(batch.Id, ct))!;
        var page = await read.ListBatchesAsync(1, 100, ct);
        var errors = (await read.ListLinesAsync(batch.Id, "Error", 1, 10, ct))!;
        var paged = (await read.ListLinesAsync(batch.Id, null, 2, 2, ct))!;
        var report = (await read.GetReportLinesAsync(batch.Id, ct))!;

        detail.Status.ShouldBe("Processing");
        detail.Progress.ShouldBe(new ImportLineCounts(1, 1, 0, 1, 0));
        page.Items.ShouldContain(b => b.Id == batch.Id);
        errors.Items.Single().FailureReason.ShouldBe("keycloak.unavailable");
        paged.Items.Count.ShouldBe(1);
        paged.TotalCount.ShouldBe(3);
        report.Select(r => r.LineNumber).ShouldBe([1, 2, 3]);
        (await read.ListLinesAsync(batch.Id, "Nonsense", 1, 10, ct))!.TotalCount.ShouldBe(0);
        (await read.GetBatchAsync(Guid.NewGuid(), ct)).ShouldBeNull();
        (await read.ListLinesAsync(Guid.NewGuid(), null, 1, 10, ct)).ShouldBeNull();
        (await read.GetReportLinesAsync(Guid.NewGuid(), ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ReadService_AwaitingBatch_NeverExposesTheSummaryToken()
    {
        var (db, writer) = await OpenAsync();
        await using var _ = db;
        var (batch, lines) = NewBatch(Hash());
        await SaveNewAsync(writer, batch, lines);
        await using var readDb = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);

        var detail = (await new UserImportReadService(readDb).GetBatchAsync(batch.Id, TestContext.Current.CancellationToken))!;

        detail.Status.ShouldBe("AwaitingConfirmation");
        System.Text.Json.JsonSerializer.Serialize(detail).ToLowerInvariant().ShouldNotContain("token");
    }

    private static IntegrationEvent NewEvent(Guid batchId, string tag) =>
        new(Guid.CreateVersion7(), ImportLimits.LineAcceptedEventType, $"{{\"batchId\":\"{batchId}\",\"tag\":\"{tag}\"}}", null, Now);
}
