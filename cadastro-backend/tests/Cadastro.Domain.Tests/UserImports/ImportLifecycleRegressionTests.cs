using Cadastro.Domain.UserImports;

namespace Cadastro.Domain.Tests.UserImports;

/// <summary>Bordas do ciclo de vida do lote e da linha que não podem regredir (contadores, estados finais, dados pessoais).</summary>
public sealed class ImportLifecycleRegressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(-3));

    private static ImportBatch ProcessingBatch(int valid)
    {
        var batch = ImportBatch.Create(Guid.CreateVersion7(), new string('a', 64), "sub", valid, 0, 0, Now);
        batch.Confirm(Now);
        return batch;
    }

    private static ImportLine NewLine() =>
        ImportLine.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, new ProtectedPersonalData([1], [2], [3], [4], [5], "k1"), Now);

    [Fact]
    public void RecordLineOutcome_MixedOutcomes_CountsEachAndCompletesWithErrors()
    {
        var batch = ProcessingBatch(3);

        batch.RecordLineOutcome(ImportLineStatus.AlreadyExists, Now);
        batch.RecordLineOutcome(ImportLineStatus.Created, Now);
        batch.Status.ShouldBe(ImportBatchStatus.Processing);
        batch.FinishedAtUtc.ShouldBeNull();
        batch.RecordLineOutcome(ImportLineStatus.Error, Now.AddMinutes(1));

        batch.AlreadyExistsCount.ShouldBe(1);
        batch.CreatedCount.ShouldBe(1);
        batch.ErrorCount.ShouldBe(1);
        batch.Status.ShouldBe(ImportBatchStatus.CompletedWithErrors);
        batch.FinishedAtUtc.ShouldBe(Now.AddMinutes(1));
        batch.FinishedAtUtc!.Value.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void RecordLineOutcome_AfterCompletion_Throws()
    {
        var batch = ProcessingBatch(1);
        batch.RecordLineOutcome(ImportLineStatus.Created, Now);

        Should.Throw<ImportDomainException>(() => batch.RecordLineOutcome(ImportLineStatus.Created, Now));
        batch.CreatedCount.ShouldBe(1);
    }

    [Fact]
    public void Cancel_Twice_Throws_AndConfirmAfterCancel_Throws()
    {
        var batch = ImportBatch.Create(Guid.CreateVersion7(), new string('b', 64), "sub", 1, 0, 0, Now);
        batch.Cancel([], Now);

        Should.Throw<ImportDomainException>(() => batch.Cancel([], Now));
        Should.Throw<ImportDomainException>(() => batch.Confirm(Now));
        batch.Status.ShouldBe(ImportBatchStatus.Cancelled);
    }

    [Fact]
    public void ReportExpiry_IsNinetyDaysAfterFinish()
    {
        var batch = ProcessingBatch(1);

        batch.RecordLineOutcome(ImportLineStatus.Created, Now);

        batch.ReportExpiresAtUtc.ShouldBe(Now.AddDays(90));
    }

    [Fact]
    public void MarkFailed_ThenMarkCreated_Throws_KeepingErrorState()
    {
        var line = NewLine();
        line.MarkFailed("keycloak.unavailable", Now);

        Should.Throw<ImportDomainException>(() => line.MarkCreated(Now));
        Should.Throw<ImportDomainException>(() => line.MarkAlreadyExists(Now));
        line.Status.ShouldBe(ImportLineStatus.Error);
        line.HasPersonalData.ShouldBeTrue();
    }

    [Fact]
    public void RecordLegacyMark_AfterError_Throws()
    {
        var line = NewLine();
        line.MarkFailed("keycloak.unavailable", Now);

        Should.Throw<ImportDomainException>(() => line.RecordLegacyMark(LegacyMarkStatus.Marked));
    }

    [Fact]
    public void GetProtectedData_BeforeErase_ReturnsAllFields_AndNullAfter()
    {
        var line = NewLine();

        var data = line.GetProtectedData();
        data.ShouldNotBeNull();
        data.Phone.ShouldBe([4]);
        data.KeyVersion.ShouldBe("k1");

        line.ErasePersonalData(Now);
        line.GetProtectedData().ShouldBeNull();
        line.PhoneCipher.ShouldBeNull();
    }

    [Fact]
    public void ErasePersonalData_Twice_KeepsFirstTimestamp()
    {
        var line = NewLine();
        line.ErasePersonalData(Now);

        line.ErasePersonalData(Now.AddHours(1));

        line.PersonalDataErasedAtUtc.ShouldBe(Now);
    }

    [Fact]
    public void ProtectedPersonalData_WithoutRequiredValue_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new ProtectedPersonalData(null!, [2], [3], null, [5], "k1"));
        Should.Throw<ArgumentNullException>(() => new ProtectedPersonalData([1], [2], [3], null, null!, "k1"));
        Should.Throw<ArgumentException>(() => new ProtectedPersonalData([1], [2], [3], null, [5], " "));
    }

    [Fact]
    public void MarkFailed_WithOverlongCode_Throws() =>
        Should.Throw<ImportDomainException>(() => NewLine().MarkFailed(new string('x', 101), Now));
}
