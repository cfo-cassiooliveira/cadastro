using Cadastro.Application.UserImports;
using Cadastro.Domain.UserImports;
using Cadastro.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Cadastro.Application.Tests.UserImports;

/// <summary>Cancelamento automático de lotes esquecidos em aguardando confirmação (J1, decisão de 7 dias).</summary>
public sealed class CancelAbandonedImportsHandlerTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly InMemoryUserImportStore _writer = new();
    private readonly IImportRetentionStore _retention = Substitute.For<IImportRetentionStore>();
    private readonly FakeTimeProvider _clock = new(Created.AddDays(8));
    private readonly CancelAbandonedImportsHandler _handler;

    public CancelAbandonedImportsHandlerTests() =>
        _handler = new CancelAbandonedImportsHandler(_retention, _writer, _clock, NullLogger<CancelAbandonedImportsHandler>.Instance);

    private static ProtectedPersonalData Data(byte seed) => new([seed], [seed], [seed], null, new byte[32], "k1");

    private async Task<ImportBatch> SeedBatchAsync()
    {
        var batch = ImportBatch.Create(Guid.CreateVersion7(), Convert.ToHexStringLower(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray()), "admin-1", 2, 0, 0, Created);
        _writer.AddBatch(batch, [ImportLine.Create(Guid.CreateVersion7(), batch.Id, 1, Data(1), Created), ImportLine.Create(Guid.CreateVersion7(), batch.Id, 2, Data(2), Created)]);
        (await _writer.SaveChangesAsync(TestContext.Current.CancellationToken)).ShouldBe(ImportSaveResult.Saved);
        return batch;
    }

    [Fact]
    public async Task Handle_CancelsTheAbandonedBatchErasesLineDataAndAuditsAsTheSystem()
    {
        var batch = await SeedBatchAsync();
        _retention.FindAbandonedBatchIdsAsync(Arg.Any<DateTimeOffset>(), 10, Arg.Any<CancellationToken>()).Returns([batch.Id]);

        var cancelled = await _handler.HandleAsync(10, TestContext.Current.CancellationToken);

        cancelled.ShouldBe(1);
        batch.Status.ShouldBe(ImportBatchStatus.Cancelled);
        _writer.Lines.ShouldAllBe(l => l.Status == ImportLineStatus.Cancelled && l.NameCipher == null && l.EmailCipher == null && l.CpfCipher == null);
        var audit = _writer.Audits.ShouldHaveSingleItem();
        audit.Actor.ShouldBe(CancelAbandonedImportsHandler.SystemActor);
        audit.Action.ShouldBe("batch.abandoned-cancelled");
        audit.BatchId.ShouldBe(batch.Id);
    }

    [Fact]
    public async Task Handle_AsksForBatchesOlderThanSevenDaysBeforeNow()
    {
        _retention.FindAbandonedBatchIdsAsync(Arg.Any<DateTimeOffset>(), 10, Arg.Any<CancellationToken>()).Returns([]);

        var cancelled = await _handler.HandleAsync(10, TestContext.Current.CancellationToken);

        cancelled.ShouldBe(0);
        await _retention.Received(1).FindAbandonedBatchIdsAsync(_clock.GetUtcNow() - TimeSpan.FromDays(7), 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTheBatchWasConfirmedMeanwhile_SkipsItAndKeepsItsData()
    {
        var batch = await SeedBatchAsync();
        batch.Confirm(Created.AddDays(7));
        _retention.FindAbandonedBatchIdsAsync(Arg.Any<DateTimeOffset>(), 10, Arg.Any<CancellationToken>()).Returns([batch.Id]);

        var cancelled = await _handler.HandleAsync(10, TestContext.Current.CancellationToken);

        cancelled.ShouldBe(0);
        batch.Status.ShouldBe(ImportBatchStatus.Processing);
        _writer.Lines.ShouldAllBe(l => l.NameCipher != null);
        _writer.Audits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_WhenTheBatchNoLongerExists_ReturnsZero()
    {
        _retention.FindAbandonedBatchIdsAsync(Arg.Any<DateTimeOffset>(), 10, Arg.Any<CancellationToken>()).Returns([Guid.NewGuid()]);

        (await _handler.HandleAsync(10, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenSaveConflicts_DoesNotCountTheBatch()
    {
        var batch = await SeedBatchAsync();
        _retention.FindAbandonedBatchIdsAsync(Arg.Any<DateTimeOffset>(), 10, Arg.Any<CancellationToken>()).Returns([batch.Id]);
        _writer.ForcedSaveResult = ImportSaveResult.ConcurrencyConflict;

        (await _handler.HandleAsync(10, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenMaxBatchesIsNotPositive_Throws()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => _handler.HandleAsync(0, TestContext.Current.CancellationToken));
    }
}
