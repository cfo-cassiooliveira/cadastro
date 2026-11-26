using System.Diagnostics;
using Cadastro.Application.UserImports;
using Cadastro.Domain.UserImports;
using Cadastro.Infrastructure.Messaging;
using Cadastro.Infrastructure.Persistence;
using Cadastro.Infrastructure.Tests.Messaging;
using Cadastro.Infrastructure.Tests.Persistence;
using Cadastro.Infrastructure.UserImports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Tests.UserImports;

/// <summary>Volume e concorrência contra SQL Server e RabbitMQ reais: lote de 5.000 linhas na outbox, confirmação simultânea e relay duplo com 1.000 linhas (QA da onda 2).</summary>
[Collection(ImportDatabaseSharing.Name)]
public sealed class UserImportScaleTests(ImportDatabaseFixture database, RabbitMqFixture broker)
{
    private const string TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 13, 0, 0, TimeSpan.Zero);

    private static string Hash() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());

    private static ProtectedPersonalData Data(byte seed) => new([seed, 1], [seed, 2], [seed, 3], null, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), "k1");

    private async Task<(CadastroDbContext Db, UserImportWriter Writer)> OpenAsync()
    {
        await database.MigrateAsync();
        var db = ImportDatabaseFixture.CreateContext(database.AppConnectionString);
        return (db, new UserImportWriter(db, NullLogger<UserImportWriter>.Instance));
    }

    private async Task<(ImportBatch Batch, int Lines)> SeedBatchAsync(int lines)
    {
        var (db, writer) = await OpenAsync();
        await using var _ = db;
        var batch = ImportBatch.Create(Guid.CreateVersion7(), Hash(), "admin-1", lines, 0, 0, Now);
        writer.AddBatch(batch, Enumerable.Range(1, lines).Select(i => ImportLine.Create(Guid.CreateVersion7(), batch.Id, i, Data((byte)(i % 250)), Now)));
        (await writer.SaveChangesAsync(TestContext.Current.CancellationToken)).ShouldBe(ImportSaveResult.Saved);
        return (batch, lines);
    }

    private static IEnumerable<IntegrationEvent> EventsFor(Guid batchId, IEnumerable<ImportLine> lines) =>
        lines.Select(l => new IntegrationEvent(
            Guid.CreateVersion7(), ImportLimits.LineAcceptedEventType, EventEnvelope.CreateLineAccepted(Guid.NewGuid(), batchId, l.Id, "c", "d", Now), TraceParent, Now));

    [Fact]
    public async Task Confirm_FiveThousandLines_WritesFiveThousandOutboxRowsInOneTransaction()
    {
        var (batch, count) = await SeedBatchAsync(ImportLimits.MaxLines);
        var (db, writer) = await OpenAsync();
        await using var _ = db;
        var loaded = (await writer.FindBatchAsync(batch.Id, TestContext.Current.CancellationToken))!;
        var lines = await writer.GetLinesAsync(batch.Id, TestContext.Current.CancellationToken);
        lines.Count.ShouldBe(count);

        var watch = Stopwatch.StartNew();
        loaded.Confirm(Now);
        writer.EnqueueEvents(EventsFor(batch.Id, lines));
        (await writer.SaveChangesAsync(TestContext.Current.CancellationToken)).ShouldBe(ImportSaveResult.Saved);
        watch.Stop();

        await using var check = ImportDatabaseFixture.CreateContext(database.AppConnectionString);
        (await check.ImportBatches.SingleAsync(b => b.Id == batch.Id, TestContext.Current.CancellationToken)).Status.ShouldBe(ImportBatchStatus.Processing);
        (await check.OutboxMessages.CountAsync(m => m.Payload.Contains(batch.Id.ToString()), TestContext.Current.CancellationToken)).ShouldBe(count);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Confirm_TwoSimultaneousRequests_OnlyOneWinsAndOutboxHasExactlyOneSetOfRows()
    {
        var (batch, _) = await SeedBatchAsync(50);

        async Task<ImportSaveResult> TryConfirmAsync(string tag)
        {
            var (db, writer) = await OpenAsync();
            await using var _ = db;
            var loaded = (await writer.FindBatchAsync(batch.Id, TestContext.Current.CancellationToken))!;
            var lines = await writer.GetLinesAsync(batch.Id, TestContext.Current.CancellationToken);
            loaded.Confirm(Now);
            writer.EnqueueEvents(EventsFor(batch.Id, lines).Select(e => e with { Payload = e.Payload + tag }));
            return await writer.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var results = await Task.WhenAll(Task.Run(() => TryConfirmAsync("A")), Task.Run(() => TryConfirmAsync("B")));

        results.Count(r => r == ImportSaveResult.Saved).ShouldBe(1);
        results.Count(r => r == ImportSaveResult.ConcurrencyConflict).ShouldBe(1);
        await using var check = ImportDatabaseFixture.CreateContext(database.AppConnectionString);
        (await check.OutboxMessages.CountAsync(m => m.Payload.Contains(batch.Id.ToString()), TestContext.Current.CancellationToken)).ShouldBe(50);
    }

    [Fact]
    public async Task Relay_TwoInstancesWithAThousandRows_PublishEachExactlyOnce()
    {
        await database.MigrateAsync();
        await using (var cleanup = ImportDatabaseFixture.CreateContext(database.AppConnectionString))
        {
            await cleanup.OutboxMessages.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        await broker.PurgeAsync();
        var ids = new List<Guid>();
        await using (var seed = ImportDatabaseFixture.CreateContext(database.AppConnectionString))
        {
            for (var i = 0; i < 1000; i++)
            {
                var id = Guid.CreateVersion7();
                ids.Add(id);
                seed.OutboxMessages.Add(OutboxMessage.Create(id, RabbitMqFixture.RoutingKey, EventEnvelope.CreateLineAccepted(id, Guid.NewGuid(), Guid.NewGuid(), "c", "d", Now), TraceParent, Now));
            }

            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (OutboxRelay Relay, RabbitMqOutboxPublisher Publisher, CadastroDbContext Db) NewRelay()
        {
            var options = broker.Options();
            options.RelayBatchSize = 50;
            var wrapped = Options.Create(options);
            var publisher = new RabbitMqOutboxPublisher(wrapped, NullLogger<RabbitMqOutboxPublisher>.Instance);
            var db = ImportDatabaseFixture.CreateContext(database.AppConnectionString);
            return (new OutboxRelay(db, publisher, TimeProvider.System, wrapped, NullLogger<OutboxRelay>.Instance), publisher, db);
        }

        async Task<int> DrainAsync(OutboxRelay relay)
        {
            var published = 0;
            OutboxRelayCycle cycle;
            do
            {
                cycle = await relay.RunOnceAsync(TestContext.Current.CancellationToken);
                published += cycle.Published;
            }
            while (cycle.Selected > 0);

            return published;
        }

        var first = NewRelay();
        var second = NewRelay();
        await using var _1 = first.Publisher;
        await using var _2 = second.Publisher;
        await using var _3 = first.Db;
        await using var _4 = second.Db;

        var results = await Task.WhenAll(Task.Run(() => DrainAsync(first.Relay)), Task.Run(() => DrainAsync(second.Relay)));

        results.Sum().ShouldBe(1000);
        var received = (await broker.DrainAsync()).Select(m => Guid.Parse(m.MessageId!)).ToList();
        received.Count.ShouldBe(1000);
        received.Distinct().Count().ShouldBe(1000);
        received.Order().ShouldBe(ids.Order());
        await using var check = ImportDatabaseFixture.CreateContext(database.AppConnectionString);
        (await check.OutboxMessages.CountAsync(m => m.PublishedAtUtc == null, TestContext.Current.CancellationToken)).ShouldBe(0);
    }
}
