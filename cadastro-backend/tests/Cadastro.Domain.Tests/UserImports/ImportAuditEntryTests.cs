using Cadastro.Domain.UserImports;

namespace Cadastro.Domain.Tests.UserImports;

public sealed class ImportAuditEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(-3));

    [Fact]
    public void Create_SetsUtcDateAndTwelveMonthRetention()
    {
        var entry = ImportAuditEntry.Create(Guid.NewGuid(), "sub", "batch.confirmed", Guid.NewGuid(), "corr", Now);

        entry.OccurredAtUtc.Offset.ShouldBe(TimeSpan.Zero);
        entry.ExpiresAtUtc.ShouldBe(Now.ToUniversalTime().AddMonths(12));
    }

    [Fact]
    public void Create_WithoutActor_Throws() =>
        Should.Throw<ArgumentException>(() => ImportAuditEntry.Create(Guid.NewGuid(), "", "a", null, null, Now));

    [Fact]
    public void Create_WithOversizedAction_Throws() =>
        Should.Throw<ImportDomainException>(() => ImportAuditEntry.Create(Guid.NewGuid(), "sub", new string('a', 101), null, null, Now));
}
