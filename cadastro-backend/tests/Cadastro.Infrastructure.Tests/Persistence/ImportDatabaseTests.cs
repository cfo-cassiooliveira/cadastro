using Cadastro.Domain.UserImports;
using Cadastro.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Cadastro.Infrastructure.Tests.Persistence;

/// <summary>Migrations e mapeamento do esquema <c>import</c> contra um SQL Server real, com os dois logins separados.</summary>
[Collection(ImportDatabaseSharing.Name)]
public sealed class ImportDatabaseTests(ImportDatabaseFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private static string Hash(char c) => new(c, 64);

    private static ProtectedPersonalData Data(byte seed) => new([seed, 1], [seed, 2], [seed, 3], null, Enumerable.Repeat(seed, 32).ToArray(), "k1");

    private async Task<CadastroDbContext> AppContextAsync()
    {
        await fixture.MigrateAsync();
        return ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
    }

    [Fact]
    public async Task Migrate_AsMigrator_CreatesTablesAndHistoryInImportSchemaAndIsIdempotent()
    {
        await fixture.MigrateAsync();
        await fixture.MigrateAsync();

        await using var connection = new SqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(
            "SELECT t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'import' ORDER BY t.name",
            connection);
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        tables.ShouldBe(["__EFMigrationsHistory", "audit_entries", "import_batches", "import_lines", "inbox_messages", "outbox_messages"]);
    }

    [Fact]
    public async Task App_CannotReadDboOrChangeSchema()
    {
        await fixture.MigrateAsync();
        await using var connection = new SqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var read = await Should.ThrowAsync<SqlException>(async () =>
        {
            await using var select = new SqlCommand("SELECT * FROM dbo.legacy_probe", connection);
            await select.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        });
        var ddl = await Should.ThrowAsync<SqlException>(async () =>
        {
            await using var create = new SqlCommand("CREATE TABLE [import].rogue (id int)", connection);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        });

        read.Number.ShouldBe(229); // permissão negada
        ddl.Number.ShouldBe(262); // CREATE TABLE negado
    }

    [Fact]
    public async Task App_PersistsBatchAndLines_AndErasedDataStaysNull()
    {
        await using var write = await AppContextAsync();
        var batch = ImportBatch.Create(Guid.CreateVersion7(), Hash('1'), "sub-1", 2, 0, 0, Now);
        var line = ImportLine.Create(Guid.CreateVersion7(), batch.Id, 1, Data(1), Now);
        var other = ImportLine.Create(Guid.CreateVersion7(), batch.Id, 2, Data(2), Now);
        write.AddRange(batch, line, other);
        await write.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using (var read = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString))
        {
            var stored = await read.ImportLines.SingleAsync(l => l.Id == line.Id, TestContext.Current.CancellationToken);
            stored.NameCipher.ShouldBe([1, 1]);
            stored.EmailIndex.ShouldBe(Enumerable.Repeat((byte)1, 32).ToArray());
            stored.Status.ShouldBe(ImportLineStatus.Pending);
            stored.CreatedAtUtc.ShouldBe(Now);
            (await read.ImportLines.Where(l => l.BatchId == batch.Id && l.EmailIndex == Enumerable.Repeat((byte)2, 32).ToArray()).CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        }

        line.MarkCreated(Now.AddMinutes(1));
        await write.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using var check = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        var erased = await check.ImportLines.SingleAsync(l => l.Id == line.Id, TestContext.Current.CancellationToken);
        erased.Status.ShouldBe(ImportLineStatus.Created);
        erased.NameCipher.ShouldBeNull();
        erased.CpfCipher.ShouldBeNull();
        erased.EmailIndex.ShouldBeNull();
        erased.KeyVersion.ShouldBeNull();
        erased.PersonalDataErasedAtUtc.ShouldBe(Now.AddMinutes(1));
    }

    [Fact]
    public async Task SameFileHash_IsRejectedUnlessPreviousBatchWasCancelled()
    {
        await using var context = await AppContextAsync();
        var first = ImportBatch.Create(Guid.CreateVersion7(), Hash('2'), "sub", 1, 0, 0, Now);
        context.Add(first);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using (var duplicate = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString))
        {
            duplicate.Add(ImportBatch.Create(Guid.CreateVersion7(), Hash('2'), "sub", 1, 0, 0, Now));
            await Should.ThrowAsync<DbUpdateException>(() => duplicate.SaveChangesAsync(TestContext.Current.CancellationToken));
        }

        first.Cancel([], Now);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using var resend = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        resend.Add(ImportBatch.Create(Guid.CreateVersion7(), Hash('2'), "sub", 1, 0, 0, Now));
        await resend.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LineNumber_IsUniquePerBatch()
    {
        await using var context = await AppContextAsync();
        var batch = ImportBatch.Create(Guid.CreateVersion7(), Hash('3'), "sub", 2, 0, 0, Now);
        context.AddRange(batch, ImportLine.Create(Guid.CreateVersion7(), batch.Id, 1, Data(1), Now));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Add(ImportLine.Create(Guid.CreateVersion7(), batch.Id, 1, Data(2), Now));

        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Batch_ConcurrentCounterUpdates_SecondWriterGetsConcurrencyException()
    {
        await using var seed = await AppContextAsync();
        var batch = ImportBatch.Create(Guid.CreateVersion7(), Hash('4'), "sub", 3, 0, 0, Now);
        batch.Confirm(Now);
        seed.Add(batch);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Dois consumidores leem o mesmo estado e cada um conta uma linha: sem token, o segundo sobrescreveria o primeiro.
        await using var first = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        await using var second = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        var batchOfFirst = await first.ImportBatches.SingleAsync(b => b.Id == batch.Id, TestContext.Current.CancellationToken);
        var batchOfSecond = await second.ImportBatches.SingleAsync(b => b.Id == batch.Id, TestContext.Current.CancellationToken);
        batchOfFirst.RecordLineOutcome(ImportLineStatus.Created, Now);
        batchOfSecond.RecordLineOutcome(ImportLineStatus.Created, Now);

        await first.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(TestContext.Current.CancellationToken));
        await using var read = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        (await read.ImportBatches.AsNoTracking().SingleAsync(b => b.Id == batch.Id, TestContext.Current.CancellationToken))
            .CreatedCount.ShouldBe(1);
    }

    [Fact]
    public async Task Cancel_PersistsErasedPendingLinesAndIndexKeyVersion()
    {
        await using var context = await AppContextAsync();
        var batch = ImportBatch.Create(Guid.CreateVersion7(), Hash('5'), "sub", 1, 0, 0, Now);
        var line = ImportLine.Create(Guid.CreateVersion7(), batch.Id, 1, Data(5), Now);
        context.AddRange(batch, line);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var read1 = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        (await read1.ImportLines.AsNoTracking().SingleAsync(l => l.Id == line.Id, TestContext.Current.CancellationToken))
            .EmailIndexKeyVersion.ShouldBe("k1");

        batch.Cancel([line], Now.AddMinutes(1));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var read2 = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        var stored = await read2.ImportLines.AsNoTracking().SingleAsync(l => l.Id == line.Id, TestContext.Current.CancellationToken);
        stored.Status.ShouldBe(ImportLineStatus.Cancelled);
        stored.NameCipher.ShouldBeNull();
        stored.EmailIndex.ShouldBeNull();
        stored.EmailIndexKeyVersion.ShouldBeNull();
    }

    [Fact]
    public async Task Reprocess_PersistsReopenedBatchAndLine()
    {
        await using var context = await AppContextAsync();
        var batch = ImportBatch.Create(Guid.CreateVersion7(), Hash('6'), "sub", 1, 0, 0, Now);
        batch.Confirm(Now);
        var line = ImportLine.Create(Guid.CreateVersion7(), batch.Id, 1, Data(6), Now);
        line.MarkFailed("keycloak.unavailable", Now);
        batch.RecordLineOutcome(ImportLineStatus.Error, Now);
        context.AddRange(batch, line);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var audit = batch.Reprocess(line, "admin-sub", "corr", Now.AddMinutes(1));
        context.Add(audit);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var read = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        var storedBatch = await read.ImportBatches.AsNoTracking().SingleAsync(b => b.Id == batch.Id, TestContext.Current.CancellationToken);
        storedBatch.Status.ShouldBe(ImportBatchStatus.Processing);
        storedBatch.ErrorCount.ShouldBe(0);
        storedBatch.ReportExpiresAtUtc.ShouldBeNull();
        var storedLine = await read.ImportLines.AsNoTracking().SingleAsync(l => l.Id == line.Id, TestContext.Current.CancellationToken);
        storedLine.Status.ShouldBe(ImportLineStatus.Pending);
        storedLine.ReprocessCount.ShouldBe(1);
        (await read.AuditEntries.AsNoTracking().AnyAsync(a => a.Id == audit.Id, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task Inbox_RejectsSameMessageIdTwice()
    {
        await using var context = await AppContextAsync();
        var messageId = Guid.CreateVersion7();
        context.Add(InboxMessage.Create(messageId, Now));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var second = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        second.Add(InboxMessage.Create(messageId, Now));

        await Should.ThrowAsync<DbUpdateException>(() => second.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Outbox_PersistsPendingThenPublished()
    {
        await using var context = await AppContextAsync();
        var message = OutboxMessage.Create(Guid.CreateVersion7(), "cadastro.user-import-line.accepted.v1", "{\"batchId\":\"b\",\"lineId\":\"l\"}", "00-abc-def-01", Now);
        context.Add(message);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        message.MarkAttemptFailed("broker.unavailable", Now);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        message.MarkPublished(Now.AddSeconds(5));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var read = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        var stored = await read.OutboxMessages.SingleAsync(m => m.Id == message.Id, TestContext.Current.CancellationToken);
        stored.PublishedAtUtc.ShouldBe(Now.AddSeconds(5));
        stored.Attempts.ShouldBe(2);
        stored.LastErrorCode.ShouldBeNull();
        stored.TraceParent.ShouldBe("00-abc-def-01");
    }

    [Fact]
    public async Task Audit_PersistsWithExpiry()
    {
        await using var context = await AppContextAsync();
        var entry = ImportAuditEntry.Create(Guid.CreateVersion7(), "sub", "access.denied", null, "corr-1", Now);
        context.Add(entry);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var read = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        var stored = await read.AuditEntries.SingleAsync(a => a.Id == entry.Id, TestContext.Current.CancellationToken);
        stored.ExpiresAtUtc.ShouldBe(Now.AddMonths(12));
        stored.BatchId.ShouldBeNull();
    }
}
