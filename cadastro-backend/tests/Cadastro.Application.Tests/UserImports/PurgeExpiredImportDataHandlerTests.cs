using Cadastro.Application.UserImports;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Cadastro.Application.Tests.UserImports;

/// <summary>Job de retenção (J1): drena os itens vencidos em lotes pequenos, com o relógio injetado.</summary>
public sealed class PurgeExpiredImportDataHandlerTests
{
    private readonly IImportRetentionStore _store = Substitute.For<IImportRetentionStore>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 12, 1, 3, 0, 0, TimeSpan.Zero));
    private readonly PurgeExpiredImportDataHandler _handler;

    public PurgeExpiredImportDataHandlerTests() => _handler = new PurgeExpiredImportDataHandler(_store, _clock);

    [Fact]
    public async Task Handle_WhenNothingExpired_ReturnsZeroAndCallsEachStoreOnce()
    {
        _store.DeleteExpiredAuditEntriesAsync(Arg.Any<DateTimeOffset>(), 10, Arg.Any<CancellationToken>()).Returns(0);
        _store.DeleteExpiredBatchesAsync(Arg.Any<DateTimeOffset>(), 10, Arg.Any<CancellationToken>()).Returns(new ExpiredBatchPurge(0, 0));

        var result = await _handler.HandleAsync(10, TestContext.Current.CancellationToken);

        result.ShouldBe(new ImportRetentionResult(0, 0, 0));
        result.RemovedAnything.ShouldBeFalse();
        await _store.Received(1).DeleteExpiredAuditEntriesAsync(Arg.Any<DateTimeOffset>(), 10, Arg.Any<CancellationToken>());
        await _store.Received(1).DeleteExpiredBatchesAsync(Arg.Any<DateTimeOffset>(), 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenFullBatchesKeepComing_DrainsUntilAPartialOneAndSumsTheTotals()
    {
        _store.DeleteExpiredAuditEntriesAsync(Arg.Any<DateTimeOffset>(), 5, Arg.Any<CancellationToken>()).Returns(5, 5, 2);
        _store.DeleteExpiredBatchesAsync(Arg.Any<DateTimeOffset>(), 5, Arg.Any<CancellationToken>())
            .Returns(new ExpiredBatchPurge(5, 40), new ExpiredBatchPurge(1, 3));

        var result = await _handler.HandleAsync(5, TestContext.Current.CancellationToken);

        result.ShouldBe(new ImportRetentionResult(12, 6, 43));
        result.RemovedAnything.ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_UsesTheInjectedClockForEveryCall()
    {
        _store.DeleteExpiredAuditEntriesAsync(Arg.Any<DateTimeOffset>(), 5, Arg.Any<CancellationToken>()).Returns(5, 0);
        _store.DeleteExpiredBatchesAsync(Arg.Any<DateTimeOffset>(), 5, Arg.Any<CancellationToken>()).Returns(new ExpiredBatchPurge(0, 0));

        await _handler.HandleAsync(5, TestContext.Current.CancellationToken);

        await _store.Received(2).DeleteExpiredAuditEntriesAsync(_clock.GetUtcNow(), 5, Arg.Any<CancellationToken>());
        await _store.Received(1).DeleteExpiredBatchesAsync(_clock.GetUtcNow(), 5, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Handle_WhenBatchSizeIsNotPositive_Throws(int batchSize)
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => _handler.HandleAsync(batchSize, TestContext.Current.CancellationToken));
    }
}
