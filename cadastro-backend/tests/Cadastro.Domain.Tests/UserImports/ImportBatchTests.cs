using Cadastro.Domain.UserImports;

namespace Cadastro.Domain.Tests.UserImports;

public sealed class ImportBatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(-3));
    private static readonly string ValidHash = new('a', 64);

    private static ImportBatch NewBatch(int valid = 2) =>
        ImportBatch.Create(Guid.CreateVersion7(), ValidHash, "user-sub", valid, 1, 3, Now);

    [Fact]
    public void Create_StartsAwaitingConfirmationWithUtcDate()
    {
        var batch = NewBatch();

        batch.Status.ShouldBe(ImportBatchStatus.AwaitingConfirmation);
        batch.CreatedAtUtc.Offset.ShouldBe(TimeSpan.Zero);
        batch.CreatedAtUtc.ShouldBe(Now);
        batch.ValidLines.ShouldBe(2);
        batch.InvalidLines.ShouldBe(1);
        batch.DuplicateLines.ShouldBe(3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Create_WithInvalidHash_Throws(string hash) =>
        Should.Throw<ImportDomainException>(() => ImportBatch.Create(Guid.NewGuid(), hash, "sub", 1, 0, 0, Now));

    [Fact]
    public void Create_WithNegativeCounter_Throws() =>
        Should.Throw<ImportDomainException>(() => ImportBatch.Create(Guid.NewGuid(), ValidHash, "sub", -1, 0, 0, Now));

    [Fact]
    public void Create_WithoutCreator_Throws() =>
        Should.Throw<ArgumentException>(() => ImportBatch.Create(Guid.NewGuid(), ValidHash, " ", 1, 0, 0, Now));

    [Fact]
    public void Confirm_MovesToProcessing()
    {
        var batch = NewBatch();

        batch.Confirm(Now);

        batch.Status.ShouldBe(ImportBatchStatus.Processing);
        batch.ConfirmedAtUtc.ShouldBe(Now);
    }

    [Fact]
    public void Confirm_WithoutValidLines_Throws() =>
        Should.Throw<ImportDomainException>(() => NewBatch(valid: 0).Confirm(Now));

    [Fact]
    public void Confirm_Twice_Throws()
    {
        var batch = NewBatch();
        batch.Confirm(Now);

        Should.Throw<ImportDomainException>(() => batch.Confirm(Now));
    }

    [Fact]
    public void Cancel_BeforeConfirmation_FinishesAndSetsReportRetention()
    {
        var batch = NewBatch();

        batch.Cancel([], Now);

        batch.Status.ShouldBe(ImportBatchStatus.Cancelled);
        batch.ReportExpiresAtUtc.ShouldBe(Now.AddDays(90));
    }

    [Fact]
    public void Cancel_AfterConfirmation_Throws()
    {
        var batch = NewBatch();
        batch.Confirm(Now);

        Should.Throw<ImportDomainException>(() => batch.Cancel([], Now));
    }

    [Fact]
    public void RecordLineOutcome_LastLineWithoutErrors_Completes()
    {
        var batch = NewBatch();
        batch.Confirm(Now);

        batch.RecordLineOutcome(ImportLineStatus.Created, Now);
        batch.Status.ShouldBe(ImportBatchStatus.Processing);
        batch.RecordLineOutcome(ImportLineStatus.AlreadyExists, Now);

        batch.Status.ShouldBe(ImportBatchStatus.Completed);
        batch.CreatedCount.ShouldBe(1);
        batch.AlreadyExistsCount.ShouldBe(1);
        batch.FinishedAtUtc.ShouldBe(Now);
        batch.ReportExpiresAtUtc.ShouldBe(Now.AddDays(90));
    }

    [Fact]
    public void RecordLineOutcome_WithError_CompletesWithErrors()
    {
        var batch = NewBatch();
        batch.Confirm(Now);

        batch.RecordLineOutcome(ImportLineStatus.Created, Now);
        batch.RecordLineOutcome(ImportLineStatus.Error, Now);

        batch.Status.ShouldBe(ImportBatchStatus.CompletedWithErrors);
        batch.ErrorCount.ShouldBe(1);
    }

    [Fact]
    public void RecordLineOutcome_BeyondValidLines_Throws()
    {
        var batch = NewBatch(valid: 1);
        batch.Confirm(Now);
        batch.RecordLineOutcome(ImportLineStatus.Created, Now);

        Should.Throw<ImportDomainException>(() => batch.RecordLineOutcome(ImportLineStatus.Created, Now));
    }

    [Fact]
    public void RecordLineOutcome_BeforeConfirmation_Throws() =>
        Should.Throw<ImportDomainException>(() => NewBatch().RecordLineOutcome(ImportLineStatus.Created, Now));

    [Fact]
    public void RecordLineOutcome_WithPendingOutcome_Throws()
    {
        var batch = NewBatch();
        batch.Confirm(Now);

        Should.Throw<ImportDomainException>(() => batch.RecordLineOutcome(ImportLineStatus.Pending, Now));
    }
}
