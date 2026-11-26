using Cadastro.Domain.UserImports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Cadastro.Infrastructure.Tests.Persistence;

/// <summary>Regressões do esquema <c>import</c>: deriva do modelo, limites do runtime e remoção em cascata.</summary>
[Collection(ImportDatabaseSharing.Name)]
public sealed class ImportSchemaRegressionTests(ImportDatabaseFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migrations_MatchCurrentModel_WithoutPendingChanges()
    {
        await fixture.MigrateAsync();
        await using var context = ImportDatabaseFixture.CreateContext(fixture.MigratorConnectionString);

        // Falha quando alguém muda o modelo sem gerar a migration correspondente.
        context.Database.HasPendingModelChanges().ShouldBeFalse();
        (await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("DROP TABLE [import].import_lines")]
    [InlineData("ALTER TABLE [import].import_lines ADD rogue int")]
    [InlineData("TRUNCATE TABLE [import].inbox_messages")]
    public async Task App_CannotAlterOrDropTables(string sql)
    {
        await fixture.MigrateAsync();
        await using var connection = new SqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var error = await Should.ThrowAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        });

        error.Number.ShouldBeOneOf(262, 1088, 3701, 4902, 15151); // permissão negada (DROP devolve 3701, TRUNCATE 1088)
    }

    [Fact]
    public async Task App_CannotWriteToDbo()
    {
        await fixture.MigrateAsync();
        await using var connection = new SqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var error = await Should.ThrowAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand("DELETE FROM dbo.legacy_probe", connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        });

        error.Number.ShouldBe(229);
    }

    [Fact]
    public async Task DeletingBatch_CascadesToItsLinesOnly()
    {
        await fixture.MigrateAsync();
        await using var context = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        var data = new ProtectedPersonalData([1], [2], [3], null, [4], "k1");
        var doomed = ImportBatch.Create(Guid.CreateVersion7(), new string('d', 64), "sub", 1, 0, 0, Now);
        var kept = ImportBatch.Create(Guid.CreateVersion7(), new string('e', 64), "sub", 1, 0, 0, Now);
        context.AddRange(doomed, kept,
            ImportLine.Create(Guid.CreateVersion7(), doomed.Id, 1, data, Now),
            ImportLine.Create(Guid.CreateVersion7(), kept.Id, 1, data, Now));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Remove(doomed);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var check = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        (await check.ImportLines.CountAsync(l => l.BatchId == doomed.Id, TestContext.Current.CancellationToken)).ShouldBe(0);
        (await check.ImportLines.CountAsync(l => l.BatchId == kept.Id, TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task Line_WithUnknownBatch_IsRejectedByForeignKey()
    {
        await fixture.MigrateAsync();
        await using var context = ImportDatabaseFixture.CreateContext(fixture.AppConnectionString);
        context.Add(ImportLine.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, new ProtectedPersonalData([1], [2], [3], null, [4], "k1"), Now));

        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
    }
}
