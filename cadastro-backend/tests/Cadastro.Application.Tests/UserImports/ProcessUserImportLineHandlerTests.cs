using System.Security.Cryptography;
using System.Text;
using Cadastro.Application.UserImports;
using Cadastro.Application.Users;
using Cadastro.Domain.UserImports;
using Cadastro.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Cadastro.Application.Tests.UserImports;

/// <summary>Processamento de uma linha aceita (C5a-C5c): criação, idempotência, falhas e marcação no Legado, sobre o repositório em memória.</summary>
public sealed class ProcessUserImportLineHandlerTests
{
    private const string Email = "ana@example.com";

    private readonly InMemoryUserImportStore _store = new();
    private readonly IPersonalDataProtector _protector = Substitute.For<IPersonalDataProtector>();
    private readonly IIdentityUserProvisioner _identity = Substitute.For<IIdentityUserProvisioner>();
    private readonly ILegacyUserDirectory _legacy = Substitute.For<ILegacyUserDirectory>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 25, 13, 0, 0, TimeSpan.Zero));
    private readonly Guid _batchId = Guid.NewGuid();
    private readonly Guid _lineId = Guid.NewGuid();
    private readonly Guid _messageId = Guid.NewGuid();

    public ProcessUserImportLineHandlerTests()
    {
        _protector.Unprotect(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => Encoding.UTF8.GetString(call.ArgAt<byte[]>(0)));
        _identity.ProvisionAsync(Arg.Any<ProvisionUserRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProvisionedUser(Guid.NewGuid().ToString("D"), ProvisionOutcome.Created));
        _legacy.FindByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    private async Task SeedAsync(int extraPendingLines = 0)
    {
        var lines = new List<ImportLine> { NewLine(_lineId, 1) };
        for (var i = 0; i < extraPendingLines; i++)
        {
            lines.Add(NewLine(Guid.NewGuid(), i + 2));
        }

        var batch = ImportBatch.Create(_batchId, new string('a', 64), "admin-1", lines.Count, 0, 0, _clock.GetUtcNow());
        batch.Confirm(_clock.GetUtcNow());
        _store.AddBatch(batch, lines);
        (await _store.SaveChangesAsync(TestContext.Current.CancellationToken)).ShouldBe(ImportSaveResult.Saved);
    }

    private ImportLine NewLine(Guid id, int number)
    {
        byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
        var data = new ProtectedPersonalData(Bytes("Ana Souza"), Bytes(Email), Bytes("52998224725"), Bytes("11999990000"), SHA256.HashData(Bytes(Email)), "k1");
        return ImportLine.Create(id, _batchId, number, data, _clock.GetUtcNow());
    }

    private ProcessUserImportLineHandler Handler() =>
        new(
            _store, _protector, _identity, _legacy,
            new UserImportLineCompletion(_store, _clock, NullLogger<UserImportLineCompletion>.Instance),
            NullLogger<ProcessUserImportLineHandler>.Instance);

    private Task<LineProcessingResult> RunAsync(int attempt = 0, Guid? messageId = null) =>
        Handler().HandleAsync(new ProcessUserImportLineCommand(messageId ?? _messageId, _batchId, _lineId, attempt), TestContext.Current.CancellationToken);

    private ImportLine Line => _store.Lines.Single(l => l.Id == _lineId);

    private ImportBatch Batch => _store.Batches.Single();

    [Fact]
    public async Task Created_WithSingleExactCandidate_MarksLegacyAndFinishesLineAndBatch()
    {
        await SeedAsync();
        var legacyId = Guid.NewGuid();
        _legacy.FindByEmailAsync(Email, Arg.Any<CancellationToken>()).Returns([new LegacyUserCandidate(legacyId, "ANA@example.com")]);

        var result = await RunAsync();

        result.Disposition.ShouldBe(LineProcessingDisposition.Completed);
        Line.Status.ShouldBe(ImportLineStatus.Created);
        Line.LegacyMarkStatus.ShouldBe(LegacyMarkStatus.Marked);
        Line.HasPersonalData.ShouldBeFalse();
        Batch.Status.ShouldBe(ImportBatchStatus.Completed);
        Batch.CreatedCount.ShouldBe(1);
        _store.InboxCount.ShouldBe(1);
        await _legacy.Received(1).MarkMigratedAsync(legacyId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _identity.Received(1).ProvisionAsync(
            Arg.Is<ProvisionUserRequest>(r => r.LineId == _lineId && r.Email == Email && r.Name == "Ana Souza"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Created_WithNoCandidate_RecordsNoCandidateWithoutError()
    {
        await SeedAsync();

        (await RunAsync()).Disposition.ShouldBe(LineProcessingDisposition.Completed);

        Line.Status.ShouldBe(ImportLineStatus.Created);
        Line.LegacyMarkStatus.ShouldBe(LegacyMarkStatus.NoCandidate);
        await _legacy.DidNotReceive().MarkMigratedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Created_WithCandidateThatDiffersByEmail_DoesNotMark()
    {
        await SeedAsync();
        _legacy.FindByEmailAsync(Email, Arg.Any<CancellationToken>()).Returns([new LegacyUserCandidate(Guid.NewGuid(), "other@example.com")]);

        await RunAsync();

        Line.LegacyMarkStatus.ShouldBe(LegacyMarkStatus.NoCandidate);
        await _legacy.DidNotReceive().MarkMigratedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Created_WithSeveralCandidates_RecordsAmbiguousAndDoesNotMark()
    {
        await SeedAsync();
        _legacy.FindByEmailAsync(Email, Arg.Any<CancellationToken>())
            .Returns([new LegacyUserCandidate(Guid.NewGuid(), Email), new LegacyUserCandidate(Guid.NewGuid(), Email)]);

        await RunAsync();

        Line.Status.ShouldBe(ImportLineStatus.Created);
        Line.LegacyMarkStatus.ShouldBe(LegacyMarkStatus.Ambiguous);
        await _legacy.DidNotReceive().MarkMigratedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Created_WhenLegacyLookupFails_KeepsTheKeycloakUserAndRecordsFailed()
    {
        await SeedAsync();
        _legacy.FindByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<LegacyUserCandidate>>(_ => throw new LegacyDirectoryUnavailableException("down"));

        var result = await RunAsync();

        result.Disposition.ShouldBe(LineProcessingDisposition.Completed);
        Line.Status.ShouldBe(ImportLineStatus.Created);
        Line.LegacyMarkStatus.ShouldBe(LegacyMarkStatus.Failed);
    }

    [Fact]
    public async Task Created_WhenLegacyMarkFails_KeepsTheKeycloakUserAndRecordsFailed()
    {
        await SeedAsync();
        _legacy.FindByEmailAsync(Email, Arg.Any<CancellationToken>()).Returns([new LegacyUserCandidate(Guid.NewGuid(), Email)]);
        _legacy.MarkMigratedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => throw new LegacyDirectoryUnavailableException("500"));

        await RunAsync();

        Line.Status.ShouldBe(ImportLineStatus.Created);
        Line.LegacyMarkStatus.ShouldBe(LegacyMarkStatus.Failed);
    }

    [Fact]
    public async Task AlreadyExists_FinishesLineWithoutTouchingTheLegacy()
    {
        await SeedAsync();
        _identity.ProvisionAsync(Arg.Any<ProvisionUserRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProvisionedUser(Guid.NewGuid().ToString("D"), ProvisionOutcome.AlreadyExists));

        var result = await RunAsync();

        result.Disposition.ShouldBe(LineProcessingDisposition.Completed);
        Line.Status.ShouldBe(ImportLineStatus.AlreadyExists);
        Line.HasPersonalData.ShouldBeFalse();
        Batch.AlreadyExistsCount.ShouldBe(1);
        await _legacy.DidNotReceiveWithAnyArgs().FindByEmailAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DuplicateMessage_IsAcknowledgedWithoutRepeatingTheEffect()
    {
        await SeedAsync(extraPendingLines: 1);

        await RunAsync();
        var second = await RunAsync();

        second.Disposition.ShouldBe(LineProcessingDisposition.Duplicate);
        await _identity.Received(1).ProvisionAsync(Arg.Any<ProvisionUserRequest>(), Arg.Any<CancellationToken>());
        Batch.CreatedCount.ShouldBe(1);
        _store.InboxCount.ShouldBe(1);
    }

    [Fact]
    public async Task NewMessageForFinishedLine_IsDuplicate()
    {
        await SeedAsync(extraPendingLines: 1);
        await RunAsync();

        var result = await RunAsync(messageId: Guid.NewGuid());

        result.Disposition.ShouldBe(LineProcessingDisposition.Duplicate);
        await _identity.Received(1).ProvisionAsync(Arg.Any<ProvisionUserRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransientIdentityFailure_AsksForRetryAndLeavesTheLinePending()
    {
        await SeedAsync();
        _identity.ProvisionAsync(Arg.Any<ProvisionUserRequest>(), Arg.Any<CancellationToken>()).Returns<ProvisionedUser>(_ => throw new IdentityProviderUnavailableException("503"));

        var result = await RunAsync(attempt: 2);

        result.ShouldBe(new LineProcessingResult(LineProcessingDisposition.RetryLater, ImportFailureCodes.IdentityUnavailable));
        Line.Status.ShouldBe(ImportLineStatus.Pending);
        _store.InboxCount.ShouldBe(0);
    }

    [Fact]
    public async Task AccessDenied_RetriesWithoutUsingACycleEvenAfterTheLimit()
    {
        await SeedAsync();
        _identity.ProvisionAsync(Arg.Any<ProvisionUserRequest>(), Arg.Any<CancellationToken>()).Returns<ProvisionedUser>(_ => throw new IdentityAccessDeniedException("403"));

        var result = await RunAsync(attempt: ImportRetryPolicy.MaxCycles);

        result.ShouldBe(new LineProcessingResult(LineProcessingDisposition.RetryLater, ImportFailureCodes.IdentityUnavailable, CountsAsCycle: false));
        Line.Status.ShouldBe(ImportLineStatus.Pending);
    }

    [Fact]
    public async Task TransientIdentityFailure_AfterFiveCycles_FailsTheLineKeepingTheData()
    {
        await SeedAsync();
        _identity.ProvisionAsync(Arg.Any<ProvisionUserRequest>(), Arg.Any<CancellationToken>()).Returns<ProvisionedUser>(_ => throw new IdentityProviderUnavailableException("503"));

        var result = await RunAsync(attempt: ImportRetryPolicy.MaxCycles);

        result.ShouldBe(new LineProcessingResult(LineProcessingDisposition.Failed, ImportFailureCodes.RetriesExhausted));
        Line.Status.ShouldBe(ImportLineStatus.Error);
        Line.FailureReason.ShouldBe(ImportFailureCodes.RetriesExhausted);
        Line.HasPersonalData.ShouldBeTrue();
        Batch.Status.ShouldBe(ImportBatchStatus.CompletedWithErrors);
        _store.InboxCount.ShouldBe(1);
    }

    [Fact]
    public async Task PermanentIdentityRejection_FailsTheLineWithItsCode()
    {
        await SeedAsync();
        _identity.ProvisionAsync(Arg.Any<ProvisionUserRequest>(), Arg.Any<CancellationToken>()).Returns<ProvisionedUser>(_ => throw new IdentityUserRejectedException("identity.rejected"));

        var result = await RunAsync();

        result.ShouldBe(new LineProcessingResult(LineProcessingDisposition.Failed, "identity.rejected"));
        Line.Status.ShouldBe(ImportLineStatus.Error);
        Batch.ErrorCount.ShouldBe(1);
    }

    [Fact]
    public async Task MissingLine_IsRejectedWithoutMarkingAnything()
    {
        await SeedAsync();

        var result = await Handler().HandleAsync(
            new ProcessUserImportLineCommand(_messageId, _batchId, Guid.NewGuid(), 0), TestContext.Current.CancellationToken);

        result.ShouldBe(new LineProcessingResult(LineProcessingDisposition.Failed, ImportFailureCodes.LineNotFound));
        Line.Status.ShouldBe(ImportLineStatus.Pending);
    }

    [Fact]
    public async Task UnreadablePersonalData_FailsTheLine()
    {
        await SeedAsync();
        _protector.Unprotect(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<string>()).Returns(_ => throw new CryptographicException("tampered"));

        var result = await RunAsync();

        result.ShouldBe(new LineProcessingResult(LineProcessingDisposition.Failed, ImportFailureCodes.PersonalDataUnavailable));
        Line.Status.ShouldBe(ImportLineStatus.Error);
        await _identity.DidNotReceiveWithAnyArgs().ProvisionAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FailHandler_MarksTheLineAsErrorAndCountsItInTheBatch()
    {
        await SeedAsync();
        var handler = new FailUserImportLineHandler(new UserImportLineCompletion(_store, _clock, NullLogger<UserImportLineCompletion>.Instance));

        var result = await handler.HandleAsync(new FailUserImportLineCommand(_messageId, _batchId, _lineId, ImportFailureCodes.RetriesExhausted), TestContext.Current.CancellationToken);

        result.Disposition.ShouldBe(LineProcessingDisposition.Failed);
        Line.Status.ShouldBe(ImportLineStatus.Error);
        Batch.Status.ShouldBe(ImportBatchStatus.CompletedWithErrors);
    }

    [Fact]
    public async Task LastLineOfTheBatch_ClosesItAsCompletedWithErrorsWhenAnyLineFailed()
    {
        await SeedAsync(extraPendingLines: 1);
        var other = _store.Lines.Single(l => l.Id != _lineId);
        _identity.ProvisionAsync(Arg.Is<ProvisionUserRequest>(r => r.LineId == _lineId), Arg.Any<CancellationToken>())
            .Returns<ProvisionedUser>(_ => throw new IdentityUserRejectedException("identity.rejected"));

        await RunAsync();
        Batch.Status.ShouldBe(ImportBatchStatus.Processing);
        await Handler().HandleAsync(new ProcessUserImportLineCommand(Guid.NewGuid(), _batchId, other.Id, 0), TestContext.Current.CancellationToken);

        Batch.Status.ShouldBe(ImportBatchStatus.CompletedWithErrors);
        Batch.FinishedLines.ShouldBe(2);
    }
}
