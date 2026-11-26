using Cadastro.Domain.UserImports;

namespace Cadastro.Domain.Tests.UserImports;

public sealed class ImportLineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private static ProtectedPersonalData Data(bool withPhone = true) =>
        new([1], [2], [3], withPhone ? [4] : null, [5], "k1");

    private static ImportLine NewLine(bool withPhone = true) =>
        ImportLine.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, Data(withPhone), Now);

    [Fact]
    public void Create_StartsPendingKeepingProtectedData()
    {
        var line = NewLine();

        line.Status.ShouldBe(ImportLineStatus.Pending);
        line.LegacyMarkStatus.ShouldBe(LegacyMarkStatus.NotAttempted);
        line.HasPersonalData.ShouldBeTrue();
        line.GetProtectedData()!.KeyVersion.ShouldBe("k1");
        line.GetProtectedData()!.Phone.ShouldBe([4]);
    }

    [Fact]
    public void Create_WithoutPhone_KeepsPhoneNull() =>
        NewLine(withPhone: false).PhoneCipher.ShouldBeNull();

    [Fact]
    public void Create_WithInvalidLineNumber_Throws() =>
        Should.Throw<ImportDomainException>(() => ImportLine.Create(Guid.NewGuid(), Guid.NewGuid(), 0, Data(), Now));

    [Fact]
    public void Create_WithoutKeyVersion_Throws() =>
        Should.Throw<ArgumentException>(() => new ProtectedPersonalData([1], [2], [3], null, [5], " "));

    [Fact]
    public void MarkCreated_ErasesPersonalData()
    {
        var line = NewLine();

        line.MarkCreated(Now);

        line.Status.ShouldBe(ImportLineStatus.Created);
        line.HasPersonalData.ShouldBeFalse();
        line.GetProtectedData().ShouldBeNull();
        line.EmailIndex.ShouldBeNull();
        line.KeyVersion.ShouldBeNull();
        line.PersonalDataErasedAtUtc.ShouldBe(Now);
        line.FinishedAtUtc.ShouldBe(Now);
    }

    [Fact]
    public void MarkAlreadyExists_ErasesPersonalData()
    {
        var line = NewLine();

        line.MarkAlreadyExists(Now);

        line.Status.ShouldBe(ImportLineStatus.AlreadyExists);
        line.HasPersonalData.ShouldBeFalse();
    }

    [Fact]
    public void MarkFailed_KeepsDataForManualReprocessAndStoresCode()
    {
        var line = NewLine();

        line.MarkFailed("keycloak.unavailable", Now);

        line.Status.ShouldBe(ImportLineStatus.Error);
        line.FailureReason.ShouldBe("keycloak.unavailable");
        line.HasPersonalData.ShouldBeTrue();
    }

    [Fact]
    public void MarkFailed_WithBlankReason_Throws() =>
        Should.Throw<ArgumentException>(() => NewLine().MarkFailed(" ", Now));

    [Theory]
    [InlineData("john@example.com")]
    [InlineData("free text with spaces")]
    public void MarkFailed_WithNonCodeReason_Throws(string reason) =>
        Should.Throw<ImportDomainException>(() => NewLine().MarkFailed(reason, Now));

    [Fact]
    public void MarkCreated_Twice_Throws()
    {
        var line = NewLine();
        line.MarkCreated(Now);

        Should.Throw<ImportDomainException>(() => line.MarkCreated(Now));
    }

    [Fact]
    public void ErasePersonalData_AfterFailure_ClearsAndIsIdempotent()
    {
        var line = NewLine();
        line.MarkFailed("x.y", Now);

        line.ErasePersonalData(Now);
        line.ErasePersonalData(Now.AddDays(1));

        line.HasPersonalData.ShouldBeFalse();
        line.PersonalDataErasedAtUtc.ShouldBe(Now);
    }

    [Fact]
    public void RecordLegacyMark_AfterCreated_Stores()
    {
        var line = NewLine();
        line.MarkCreated(Now);

        line.RecordLegacyMark(LegacyMarkStatus.Ambiguous);

        line.LegacyMarkStatus.ShouldBe(LegacyMarkStatus.Ambiguous);
    }

    [Fact]
    public void RecordLegacyMark_WhilePending_Throws() =>
        Should.Throw<ImportDomainException>(() => NewLine().RecordLegacyMark(LegacyMarkStatus.Marked));
}
