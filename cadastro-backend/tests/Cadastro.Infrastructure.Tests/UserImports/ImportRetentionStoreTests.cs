using Cadastro.Application.UserImports;
using Cadastro.Domain.UserImports;
using Cadastro.Infrastructure.Persistence;
using Cadastro.Infrastructure.Tests.Persistence;
using Cadastro.Infrastructure.UserImports;
using Microsoft.EntityFrameworkCore;

namespace Cadastro.Infrastructure.Tests.UserImports;

/// <summary>Job de retenção (J1) contra SQL Server real, com o login de runtime <c>cadastro_app</c> (DML só no esquema import).</summary>
[Collection(ImportDatabaseSharing.Name)]
public sealed class ImportRetentionStoreTests(ImportDatabaseFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static string Hash() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());

    private static ProtectedPersonalData Data(byte seed) => new([seed, 1], [seed, 2], [seed, 3], null, Enumerable.Repeat(seed, 32).ToArray(), "k1");

    private async Task<CadastroDbContext> OpenAsync()
    {
        await fixture.MigrateAsync();
        return ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
    }

    /// <summary>Lote com duas linhas já terminadas: uma criada (dados apagados) e uma com erro (dados cifrados mantidos).</summary>
    private static async Task<ImportBatch> SaveFinishedBatchAsync(CadastroDbContext db, DateTimeOffset finishedAt)
    {
        var batch = ImportBatch.Create(Guid.CreateVersion7(), Hash(), "admin-1", 2, 0, 0, Start);
        var lines = new[]
        {
            ImportLine.Create(Guid.CreateVersion7(), batch.Id, 1, Data(1), Start),
            ImportLine.Create(Guid.CreateVersion7(), batch.Id, 2, Data(2), Start),
        };
        batch.Confirm(Start);
        lines[0].MarkCreated(finishedAt);
        batch.RecordLineOutcome(ImportLineStatus.Created, finishedAt);
        lines[1].MarkFailed("keycloak.rejected", finishedAt);
        batch.RecordLineOutcome(ImportLineStatus.Error, finishedAt);
        db.ImportBatches.Add(batch);
        db.ImportLines.AddRange(lines);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return batch;
    }

    [Fact]
    public async Task DeleteExpiredBatches_RemovesBatchAndItsLinesIncludingTheErrorLineCipherData()
    {
        await using var db = await OpenAsync();
        var batch = await SaveFinishedBatchAsync(db, Start);
        var store = new ImportRetentionStore(db);
        var ct = TestContext.Current.CancellationToken;

        var afterRetention = batch.ReportExpiresAtUtc!.Value.AddSeconds(1);
        var purge = await store.DeleteExpiredBatchesAsync(afterRetention, 100, ct);

        purge.Batches.ShouldBeGreaterThanOrEqualTo(1);
        purge.Lines.ShouldBeGreaterThanOrEqualTo(2);
        await using var check = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        (await check.ImportBatches.AnyAsync(b => b.Id == batch.Id, ct)).ShouldBeFalse();
        (await check.ImportLines.AnyAsync(l => l.BatchId == batch.Id, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteExpiredBatches_BeforeTheReportExpires_KeepsEverything()
    {
        await using var db = await OpenAsync();
        var batch = await SaveFinishedBatchAsync(db, Start);
        var store = new ImportRetentionStore(db);
        var ct = TestContext.Current.CancellationToken;

        // 89 dias depois do fim do lote: o relatório ainda vale (retenção de 90 dias).
        await store.DeleteExpiredBatchesAsync(Start.AddDays(89), 100, ct);

        await using var check = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        (await check.ImportBatches.AnyAsync(b => b.Id == batch.Id, ct)).ShouldBeTrue();
        (await check.ImportLines.CountAsync(l => l.BatchId == batch.Id, ct)).ShouldBe(2);
    }

    [Fact]
    public async Task DeleteExpiredBatches_NeverRemovesABatchStillProcessing()
    {
        await using var db = await OpenAsync();
        var batch = ImportBatch.Create(Guid.CreateVersion7(), Hash(), "admin-1", 1, 0, 0, Start);
        var line = ImportLine.Create(Guid.CreateVersion7(), batch.Id, 1, Data(3), Start);
        batch.Confirm(Start);
        db.ImportBatches.Add(batch);
        db.ImportLines.Add(line);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        batch.ReportExpiresAtUtc.ShouldBeNull();
        await new ImportRetentionStore(db).DeleteExpiredBatchesAsync(Start.AddYears(5), 100, ct);

        await using var check = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        (await check.ImportBatches.AnyAsync(b => b.Id == batch.Id, ct)).ShouldBeTrue();
        (await check.ImportLines.AnyAsync(l => l.BatchId == batch.Id, ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteExpiredBatches_HonorsTheBatchSize()
    {
        await using var db = await OpenAsync();
        await SaveFinishedBatchAsync(db, Start.AddDays(-2));
        await SaveFinishedBatchAsync(db, Start.AddDays(-1));
        var store = new ImportRetentionStore(db);
        var ct = TestContext.Current.CancellationToken;
        var now = Start.AddYears(1);
        var expiredBefore = await db.ImportBatches.CountAsync(x => x.ReportExpiresAtUtc <= now, ct);

        var first = await store.DeleteExpiredBatchesAsync(now, 1, ct);

        first.Batches.ShouldBe(1);
        expiredBefore.ShouldBeGreaterThanOrEqualTo(2);
        (await db.ImportBatches.CountAsync(x => x.ReportExpiresAtUtc <= now, ct)).ShouldBe(expiredBefore - 1);
    }

    [Fact]
    public async Task DeleteExpiredAuditEntries_RemovesOnlyTheExpiredOnes()
    {
        await using var db = await OpenAsync();
        var expired = ImportAuditEntry.Create(Guid.CreateVersion7(), "admin-1", "batch.uploaded", null, "c1", Start.AddMonths(-13));
        var valid = ImportAuditEntry.Create(Guid.CreateVersion7(), "admin-1", "batch.uploaded", null, "c2", Start);
        db.AuditEntries.AddRange(expired, valid);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        var removed = await new ImportRetentionStore(db).DeleteExpiredAuditEntriesAsync(Start, 100, ct);

        removed.ShouldBeGreaterThanOrEqualTo(1);
        await using var check = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        (await check.AuditEntries.AnyAsync(a => a.Id == expired.Id, ct)).ShouldBeFalse();
        (await check.AuditEntries.AnyAsync(a => a.Id == valid.Id, ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Handler_WithTheRealStore_PurgesEverythingExpiredAndIsIdempotent()
    {
        await using var db = await OpenAsync();
        var batch = await SaveFinishedBatchAsync(db, Start);
        var expiredAudit = ImportAuditEntry.Create(Guid.CreateVersion7(), "admin-1", "batch.uploaded", batch.Id, "c3", Start.AddMonths(-13));
        db.AuditEntries.Add(expiredAudit);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(batch.ReportExpiresAtUtc!.Value.AddDays(1));
        var handler = new PurgeExpiredImportDataHandler(new ImportRetentionStore(db), clock);
        var ct = TestContext.Current.CancellationToken;

        var first = await handler.HandleAsync(100, ct);
        var second = await handler.HandleAsync(100, ct);

        first.Batches.ShouldBeGreaterThanOrEqualTo(1);
        first.AuditEntries.ShouldBeGreaterThanOrEqualTo(1);
        second.RemovedAnything.ShouldBeFalse();
        await using var check = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        (await check.ImportBatches.AnyAsync(b => b.Id == batch.Id, ct)).ShouldBeFalse();
        (await check.AuditEntries.AnyAsync(a => a.Id == expiredAudit.Id, ct)).ShouldBeFalse();
    }
}
