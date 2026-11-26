using Cadastro.Domain.UserImports;

namespace Cadastro.Domain.Tests.UserImports;

/// <summary>Reprocessamento de linhas com erro (Q8) e cancelamento do lote apagando os dados das linhas pendentes.</summary>
public sealed class ImportReprocessAndCancelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private static ImportBatch NewBatch(int valid) =>
        ImportBatch.Create(Guid.CreateVersion7(), new string('b', 64), "sub", valid, 0, 0, Now);

    private static ImportLine NewLine(ImportBatch batch, int number = 1) =>
        ImportLine.Create(Guid.CreateVersion7(), batch.Id, number, new ProtectedPersonalData([1], [2], [3], [4], [5], "k1"), Now);

    private static (ImportBatch Batch, ImportLine Failed) BatchWithOneFailure()
    {
        var batch = NewBatch(2);
        batch.Confirm(Now);
        var ok = NewLine(batch, 1);
        var failed = NewLine(batch, 2);
        ok.MarkCreated(Now);
        batch.RecordLineOutcome(ImportLineStatus.Created, Now);
        failed.MarkFailed("keycloak.unavailable", Now);
        batch.RecordLineOutcome(ImportLineStatus.Error, Now);
        return (batch, failed);
    }

    [Fact]
    public void LineReprocess_FromError_ReturnsToPendingKeepingDataAndCountsAttempt()
    {
        var (_, failed) = BatchWithOneFailure();

        failed.Reprocess();

        failed.Status.ShouldBe(ImportLineStatus.Pending);
        failed.FailureReason.ShouldBeNull();
        failed.FinishedAtUtc.ShouldBeNull();
        failed.ReprocessCount.ShouldBe(1);
        failed.HasPersonalData.ShouldBeTrue();
    }

    [Theory]
    [InlineData(ImportLineStatus.Pending)]
    [InlineData(ImportLineStatus.Created)]
    [InlineData(ImportLineStatus.AlreadyExists)]
    public void LineReprocess_FromNonErrorStatus_Throws(ImportLineStatus status)
    {
        var line = NewLine(NewBatch(1));
        if (status == ImportLineStatus.Created)
        {
            line.MarkCreated(Now);
        }
        else if (status == ImportLineStatus.AlreadyExists)
        {
            line.MarkAlreadyExists(Now);
        }

        Should.Throw<ImportDomainException>(line.Reprocess);
    }

    [Fact]
    public void LineReprocess_AfterPersonalDataErased_Throws()
    {
        var (_, failed) = BatchWithOneFailure();
        failed.ErasePersonalData(Now.AddDays(90));

        Should.Throw<ImportDomainException>(failed.Reprocess);
        failed.Status.ShouldBe(ImportLineStatus.Error);
    }

    [Fact]
    public void BatchReprocess_FromCompletedWithErrors_ReopensBatchAdjustsCountersAndAudits()
    {
        var (batch, failed) = BatchWithOneFailure();
        batch.Status.ShouldBe(ImportBatchStatus.CompletedWithErrors);
        batch.ReportExpiresAtUtc.ShouldNotBeNull();

        var audit = batch.Reprocess(failed, "admin-sub", "corr-1", Now.AddHours(1));

        batch.Status.ShouldBe(ImportBatchStatus.Processing);
        batch.ErrorCount.ShouldBe(0);
        batch.FinishedLines.ShouldBe(1);
        batch.FinishedAtUtc.ShouldBeNull();
        batch.ReportExpiresAtUtc.ShouldBeNull();
        failed.Status.ShouldBe(ImportLineStatus.Pending);
        audit.Action.ShouldBe("line.reprocess-requested");
        audit.Actor.ShouldBe("admin-sub");
        audit.BatchId.ShouldBe(batch.Id);
        audit.CorrelationId.ShouldBe("corr-1");
    }

    [Fact]
    public void BatchReprocess_ThenLineSucceeds_ClosesBatchAsCompleted()
    {
        var (batch, failed) = BatchWithOneFailure();
        batch.Reprocess(failed, "admin-sub", null, Now.AddHours(1));

        failed.MarkCreated(Now.AddHours(2));
        batch.RecordLineOutcome(ImportLineStatus.Created, Now.AddHours(2));

        batch.Status.ShouldBe(ImportBatchStatus.Completed);
        batch.CreatedCount.ShouldBe(2);
        batch.ErrorCount.ShouldBe(0);
    }

    [Fact]
    public void BatchReprocess_WhileStillProcessing_OnlyDecrementsErrorCount()
    {
        var batch = NewBatch(3);
        batch.Confirm(Now);
        var failed = NewLine(batch);
        failed.MarkFailed("keycloak.unavailable", Now);
        batch.RecordLineOutcome(ImportLineStatus.Error, Now);

        batch.Reprocess(failed, "admin-sub", null, Now);

        batch.Status.ShouldBe(ImportBatchStatus.Processing);
        batch.ErrorCount.ShouldBe(0);
    }

    [Fact]
    public void BatchReprocess_WithLineOfAnotherBatch_Throws()
    {
        var (batch, _) = BatchWithOneFailure();
        var (_, foreign) = BatchWithOneFailure();

        Should.Throw<ImportDomainException>(() => batch.Reprocess(foreign, "admin-sub", null, Now));
        batch.ErrorCount.ShouldBe(1);
    }

    [Fact]
    public void BatchReprocess_FromCancelledBatch_Throws()
    {
        var batch = NewBatch(1);
        batch.Cancel([], Now);
        var line = NewLine(batch);

        Should.Throw<ImportDomainException>(() => batch.Reprocess(line, "admin-sub", null, Now));
    }

    [Fact]
    public void BatchCancel_ErasesPersonalDataOfPendingLinesAndMarksThemCancelled()
    {
        var batch = NewBatch(2);
        var lines = new[] { NewLine(batch, 1), NewLine(batch, 2) };

        batch.Cancel(lines, Now.AddMinutes(5));

        batch.Status.ShouldBe(ImportBatchStatus.Cancelled);
        foreach (var line in lines)
        {
            line.Status.ShouldBe(ImportLineStatus.Cancelled);
            line.HasPersonalData.ShouldBeFalse();
            line.EmailIndex.ShouldBeNull();
            line.EmailIndexKeyVersion.ShouldBeNull();
            line.PersonalDataErasedAtUtc.ShouldBe(Now.AddMinutes(5));
        }
    }

    [Fact]
    public void BatchCancel_WithLineOfAnotherBatch_ThrowsAndKeepsBatchOpen()
    {
        var batch = NewBatch(1);
        var foreign = NewLine(NewBatch(1));

        Should.Throw<ImportDomainException>(() => batch.Cancel([foreign], Now));
        batch.Status.ShouldBe(ImportBatchStatus.AwaitingConfirmation);
    }

    [Fact]
    public void LegacyMark_OnCancelledLine_Throws()
    {
        var line = NewLine(NewBatch(1));
        line.Cancel(Now);

        Should.Throw<ImportDomainException>(() => line.RecordLegacyMark(LegacyMarkStatus.Marked));
    }

    [Fact]
    public void ProtectedPersonalData_DoesNotExposeMutableBuffers()
    {
        var name = new byte[] { 1, 2, 3 };
        var data = new ProtectedPersonalData(name, [2], [3], [4], [5], "k1");

        name[0] = 99;
        data.Name[0] = 77;

        data.Name[0].ShouldBe((byte)1);
        data.EmailIndexKeyVersion.ShouldBe("k1");
    }

    [Fact]
    public void ProtectedPersonalData_KeepsExplicitEmailIndexKeyVersion() =>
        new ProtectedPersonalData([1], [2], [3], null, [5], "k2", "k1").EmailIndexKeyVersion.ShouldBe("k1");
}
