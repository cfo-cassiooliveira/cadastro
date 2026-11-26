using Cadastro.Application.UserImports;
using Cadastro.Domain.UserImports;
using Cadastro.Infrastructure.Persistence;
using Cadastro.Infrastructure.Tests.Persistence;
using Cadastro.Infrastructure.UserImports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cadastro.Infrastructure.Tests.UserImports;

/// <summary>
/// Término de linha com <c>UPDATE</c> atômico no lote contra SQL Server real: consumidores em paralelo não perdem a corrida pela
/// versão da linha do lote (antes esgotavam as tentativas, voltavam à retry e reenviavam o e-mail de ativação).
/// </summary>
[Collection(ImportDatabaseSharing.Name)]
public sealed class LineOutcomeAtomicityTests(ImportDatabaseFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 14, 0, 0, TimeSpan.Zero);

    private static string Hash() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());

    private static ProtectedPersonalData Data(byte seed) => new([seed, 1], [seed, 2], [seed, 3], null, Enumerable.Repeat(seed, 32).ToArray(), "k1");

    private UserImportWriter NewWriter(out CadastroDbContext db)
    {
        db = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        return new UserImportWriter(db, NullLogger<UserImportWriter>.Instance);
    }

    /// <summary>Lote com <paramref name="lines"/> linhas pendentes, gravado no banco; em processamento quando <paramref name="confirm"/>.</summary>
    private async Task<(Guid BatchId, IReadOnlyList<Guid> LineIds)> SeedBatchAsync(int lines, bool confirm = true)
    {
        await fixture.MigrateAsync();
        await using var db = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        var batch = ImportBatch.Create(Guid.CreateVersion7(), Hash(), "admin-1", lines, 0, 0, Now);
        var entities = Enumerable.Range(1, lines).Select(i => ImportLine.Create(Guid.CreateVersion7(), batch.Id, i, Data((byte)(i % 200)), Now)).ToList();
        if (confirm)
        {
            batch.Confirm(Now);
        }

        db.ImportBatches.Add(batch);
        db.ImportLines.AddRange(entities);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (batch.Id, entities.Select(l => l.Id).ToList());
    }

    private async Task<ImportSaveResult> FinishAsync(Guid batchId, Guid lineId, ImportLineStatus outcome, Guid? messageId = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var writer = NewWriter(out var db);
        await using var _ = db;
        var line = await writer.FindLineAsync(batchId, lineId, ct);
        switch (outcome)
        {
            case ImportLineStatus.Created:
                line!.MarkCreated(Now);
                break;
            case ImportLineStatus.AlreadyExists:
                line!.MarkAlreadyExists(Now);
                break;
            default:
                line!.MarkFailed("identity.rejected", Now);
                break;
        }

        writer.AddInboxMessage(messageId ?? Guid.CreateVersion7(), Now);
        return await writer.SaveLineOutcomeAsync(batchId, outcome, Now, ct);
    }

    private async Task<ImportBatch> LoadBatchAsync(Guid batchId)
    {
        await using var db = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        return await db.ImportBatches.AsNoTracking().SingleAsync(b => b.Id == batchId, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> AddPendingLineAsync(Guid batchId, int lineNumber)
    {
        await using var db = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        var line = ImportLine.Create(Guid.CreateVersion7(), batchId, lineNumber, Data(9), Now);
        db.ImportLines.Add(line);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return line.Id;
    }

    [Fact]
    public async Task SaveLineOutcome_WithManyConsumersInParallel_NeverConflictsAndCountsEveryLineOnce()
    {
        const int total = 60;
        var (batchId, lineIds) = await SeedBatchAsync(total);
        var outcomes = lineIds
            .Select((id, i) => (id, outcome: i % 5 == 0 ? ImportLineStatus.Error : i % 7 == 0 ? ImportLineStatus.AlreadyExists : ImportLineStatus.Created))
            .ToList();

        var results = await Task.WhenAll(outcomes.Select(async o =>
        {
            await Task.Yield();
            return await FinishAsync(batchId, o.id, o.outcome);
        }));

        results.ShouldAllBe(r => r == ImportSaveResult.Saved);
        var batch = await LoadBatchAsync(batchId);
        batch.ErrorCount.ShouldBe(outcomes.Count(o => o.outcome == ImportLineStatus.Error));
        batch.AlreadyExistsCount.ShouldBe(outcomes.Count(o => o.outcome == ImportLineStatus.AlreadyExists));
        batch.CreatedCount.ShouldBe(outcomes.Count(o => o.outcome == ImportLineStatus.Created));
        batch.FinishedLines.ShouldBe(total);
        batch.Status.ShouldBe(ImportBatchStatus.CompletedWithErrors);
        batch.FinishedAtUtc.ShouldBe(Now);
        batch.ReportExpiresAtUtc.ShouldBe(Now + ImportRetention.Report);
    }

    [Fact]
    public async Task SaveLineOutcome_ProducesTheSameBatchStateAsTheDomainMethod()
    {
        ImportLineStatus[] sequence = [ImportLineStatus.Created, ImportLineStatus.AlreadyExists, ImportLineStatus.Created];
        var (batchId, lineIds) = await SeedBatchAsync(sequence.Length);
        var expected = ImportBatch.Create(Guid.CreateVersion7(), Hash(), "admin-1", sequence.Length, 0, 0, Now);
        expected.Confirm(Now);

        for (var i = 0; i < sequence.Length; i++)
        {
            (await FinishAsync(batchId, lineIds[i], sequence[i])).ShouldBe(ImportSaveResult.Saved);
            expected.RecordLineOutcome(sequence[i], Now);
        }

        var actual = await LoadBatchAsync(batchId);
        (actual.Status, actual.CreatedCount, actual.AlreadyExistsCount, actual.ErrorCount, actual.FinishedAtUtc, actual.ReportExpiresAtUtc)
            .ShouldBe((expected.Status, expected.CreatedCount, expected.AlreadyExistsCount, expected.ErrorCount, expected.FinishedAtUtc, expected.ReportExpiresAtUtc));
        actual.Status.ShouldBe(ImportBatchStatus.Completed);
    }

    [Fact]
    public async Task SaveLineOutcome_WhileThereAreStillLinesToCount_KeepsTheBatchProcessingWithoutAFinishDate()
    {
        var (batchId, lineIds) = await SeedBatchAsync(3);

        (await FinishAsync(batchId, lineIds[0], ImportLineStatus.Created)).ShouldBe(ImportSaveResult.Saved);

        var batch = await LoadBatchAsync(batchId);
        batch.Status.ShouldBe(ImportBatchStatus.Processing);
        batch.CreatedCount.ShouldBe(1);
        batch.FinishedAtUtc.ShouldBeNull();
        batch.ReportExpiresAtUtc.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveLineOutcome_WhenTheBatchIsNotProcessing_ReturnsBatchNotProcessingAndRollsBackTheLine(bool alreadyCompleted)
    {
        // Falso: lote ainda aguardando confirmação. Verdadeiro: lote já concluído e uma linha extra tentando contar.
        var (batchId, lineIds) = await SeedBatchAsync(alreadyCompleted ? 1 : 2, confirm: alreadyCompleted);
        if (alreadyCompleted)
        {
            (await FinishAsync(batchId, lineIds[0], ImportLineStatus.Created)).ShouldBe(ImportSaveResult.Saved);
            (await LoadBatchAsync(batchId)).Status.ShouldBe(ImportBatchStatus.Completed);
        }

        var target = alreadyCompleted ? await AddPendingLineAsync(batchId, 2) : lineIds[0];
        var messageId = Guid.CreateVersion7();

        (await FinishAsync(batchId, target, ImportLineStatus.Created, messageId)).ShouldBe(ImportSaveResult.BatchNotProcessing);

        await using var check = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        var ct = TestContext.Current.CancellationToken;
        (await check.ImportLines.AsNoTracking().SingleAsync(l => l.Id == target, ct)).Status.ShouldBe(ImportLineStatus.Pending);
        (await check.InboxMessages.AnyAsync(m => m.MessageId == messageId, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task SaveLineOutcome_WithTheSameMessageTwice_ReturnsDuplicateMessageAndCountsOnce()
    {
        var (batchId, lineIds) = await SeedBatchAsync(2);
        var messageId = Guid.CreateVersion7();

        (await FinishAsync(batchId, lineIds[0], ImportLineStatus.Created, messageId)).ShouldBe(ImportSaveResult.Saved);
        (await FinishAsync(batchId, lineIds[1], ImportLineStatus.Created, messageId)).ShouldBe(ImportSaveResult.DuplicateMessage);

        var batch = await LoadBatchAsync(batchId);
        batch.CreatedCount.ShouldBe(1);
        batch.Status.ShouldBe(ImportBatchStatus.Processing);
    }
}
