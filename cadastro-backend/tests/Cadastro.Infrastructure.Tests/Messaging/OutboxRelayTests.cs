using System.Text.Json;
using Cadastro.Application.UserImports;
using Cadastro.Infrastructure.Messaging;
using Cadastro.Infrastructure.Persistence;
using Cadastro.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Cadastro.Infrastructure.Tests.Messaging;

/// <summary>Relay da outbox contra SQL Server e RabbitMQ reais: publica com confirms, não perde nem duplica mensagens.</summary>
[Collection(ImportDatabaseSharing.Name)]
public sealed class OutboxRelayTests(ImportDatabaseFixture database, RabbitMqFixture broker)
{
    private const string TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    private async Task ResetAsync()
    {
        await database.MigrateAsync();
        await using var db = ImportDatabaseFixture.CreateContext(database.AppConnectionString);
        await db.OutboxMessages.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await broker.PurgeAsync();
    }

    private async Task<List<Guid>> SeedAsync(int count, string type = RabbitMqFixture.RoutingKey, TimeProvider? clock = null)
    {
        await using var db = ImportDatabaseFixture.CreateContext(database.AppConnectionString);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.CreateVersion7();
            ids.Add(id);
            var payload = EventEnvelope.CreateLineAccepted(id, Guid.NewGuid(), Guid.NewGuid(), "corr-1", "cause-1", now);
            db.OutboxMessages.Add(OutboxMessage.Create(id, type, payload, TraceParent, now));
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return ids;
    }

    private (OutboxRelay Relay, RabbitMqOutboxPublisher Publisher, CadastroDbContext Db) NewRelay(TimeProvider? clock = null, int batchSize = 10)
    {
        var options = broker.Options();
        options.RelayBatchSize = batchSize;
        var wrapped = Options.Create(options);
        var publisher = new RabbitMqOutboxPublisher(wrapped, NullLogger<RabbitMqOutboxPublisher>.Instance);
        var db = ImportDatabaseFixture.CreateContext(database.AppConnectionString);
        return (new OutboxRelay(db, publisher, clock ?? TimeProvider.System, wrapped, NullLogger<OutboxRelay>.Instance), publisher, db);
    }

    private static async Task<int> DrainRelayAsync(OutboxRelay relay)
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

    [Fact]
    public async Task Relay_PublishesEveryPendingMessageWithConfirmsAndMarksThemPublished()
    {
        await ResetAsync();
        var ids = await SeedAsync(25);
        var (relay, publisher, db) = NewRelay();
        await using var _ = publisher;
        await using var __ = db;

        var published = await DrainRelayAsync(relay);

        published.ShouldBe(25);
        var received = await broker.DrainAsync();
        received.Select(m => Guid.Parse(m.MessageId!)).Order().ShouldBe(ids.Order());
        await using var check = ImportDatabaseFixture.CreateContext(database.AppConnectionString);
        (await check.OutboxMessages.CountAsync(m => m.PublishedAtUtc == null, TestContext.Current.CancellationToken)).ShouldBe(0);
        (await check.OutboxMessages.AllAsync(m => m.Attempts == 1 && m.LastErrorCode == null, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task Relay_PublishesPersistentEnvelopeWithTraceParentAndOnlyIds()
    {
        await ResetAsync();
        var ids = await SeedAsync(1);
        var (relay, publisher, db) = NewRelay();
        await using var _ = publisher;
        await using var __ = db;

        await DrainRelayAsync(relay);

        var message = (await broker.DrainAsync()).Single();
        message.Persistent.ShouldBeTrue();
        message.TraceParent.ShouldBe(TraceParent);
        message.RoutingKey.ShouldBe(RabbitMqFixture.RoutingKey);
        message.MessageId.ShouldBe(ids[0].ToString("D"));
        var root = JsonDocument.Parse(message.Body).RootElement;
        root.GetProperty("type").GetString().ShouldBe(RabbitMqFixture.RoutingKey);
        root.GetProperty("source").GetString().ShouldBe("cadastro-api");
        root.GetProperty("correlationId").GetString().ShouldBe("corr-1");
        root.GetProperty("data").EnumerateObject().Select(p => p.Name).ShouldBe(["batchId", "lineId"]);
    }

    [Fact]
    public async Task Relay_UnroutableMessage_StaysPendingWithBackoffAndErrorCode()
    {
        await ResetAsync();
        // Relógio parado: o backoff da 1ª falha é de 0,5 a 1,5 s e, no relógio real, vencia antes da conferência sob carga.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await SeedAsync(1, type: "cadastro.nobody-listens.v1", clock: clock);
        var (relay, publisher, db) = NewRelay(clock);
        await using var _ = publisher;
        await using var __ = db;

        var cycle = await relay.RunOnceAsync(TestContext.Current.CancellationToken);

        cycle.Published.ShouldBe(0);
        cycle.BrokerUnavailable.ShouldBeFalse();
        await using var check = ImportDatabaseFixture.CreateContext(database.AppConnectionString);
        var message = await check.OutboxMessages.SingleAsync(TestContext.Current.CancellationToken);
        message.PublishedAtUtc.ShouldBeNull();
        message.Attempts.ShouldBe(1);
        message.LastErrorCode.ShouldBe("broker.unroutable");
        var baseDelay = TimeSpan.FromMilliseconds(broker.Options().BackoffBaseMs);
        message.NextAttemptAtUtc.ShouldBeInRange(clock.GetUtcNow() + (baseDelay * 0.5), clock.GetUtcNow() + (baseDelay * 1.5));
        (await broker.DrainAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Relay_WhenBrokerIsDown_KeepsLinesPendingAndPublishesThemAfterItComesBack()
    {
        await ResetAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var ids = await SeedAsync(5, clock: clock);
        var (relay, publisher, db) = NewRelay(clock);
        await using var _ = publisher;
        await using var __ = db;
        await broker.StopAsync();
        try
        {
            var down = await relay.RunOnceAsync(TestContext.Current.CancellationToken);

            down.Published.ShouldBe(0);
            down.BrokerUnavailable.ShouldBeTrue();
            publisher.IsDegraded.ShouldBeTrue();
            await using (var check = ImportDatabaseFixture.CreateContext(database.AppConnectionString))
            {
                var pending = await check.OutboxMessages.ToListAsync(TestContext.Current.CancellationToken);
                pending.ShouldAllBe(m => m.PublishedAtUtc == null && m.LastErrorCode == "broker.unavailable" && m.Attempts == 1);
                pending.ShouldAllBe(m => m.NextAttemptAtUtc > clock.GetUtcNow());
            }

            // Ainda dentro do backoff: o relay não pega as mensagens de novo.
            (await relay.RunOnceAsync(TestContext.Current.CancellationToken)).Selected.ShouldBe(0);
        }
        finally
        {
            await broker.StartAsync();
        }

        clock.Advance(TimeSpan.FromMinutes(10));
        var published = await DrainRelayAsync(relay);

        published.ShouldBe(5);
        publisher.IsDegraded.ShouldBeFalse();
        (await broker.DrainAsync()).Select(m => Guid.Parse(m.MessageId!)).Order().ShouldBe(ids.Order());
    }

    [Fact]
    public async Task Relay_TwoInstancesInParallel_NeverPublishTheSameMessageTwice()
    {
        await ResetAsync();
        var ids = await SeedAsync(120);
        var first = NewRelay(batchSize: 15);
        var second = NewRelay(batchSize: 15);
        await using var _1 = first.Publisher;
        await using var _2 = second.Publisher;
        await using var _3 = first.Db;
        await using var _4 = second.Db;

        var results = await Task.WhenAll(Task.Run(() => DrainRelayAsync(first.Relay)), Task.Run(() => DrainRelayAsync(second.Relay)));

        results.Sum().ShouldBe(120);
        var received = (await broker.DrainAsync()).Select(m => Guid.Parse(m.MessageId!)).ToList();
        received.Count.ShouldBe(120);
        received.Distinct().Count().ShouldBe(120);
        received.Order().ShouldBe(ids.Order());
    }

    [Fact]
    public async Task RestrictedUser_HasTheSamePermissionsAsCadastroApp_NoConfigureAndWriteOnlyToItsExchanges()
    {
        await using var connection = await broker.ConnectAsRestrictedAppAsync();

        // configure vazio: declarar exchange ou fila é recusado pelo broker (o canal fecha com ACCESS_REFUSED).
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            await Should.ThrowAsync<RabbitMQ.Client.Exceptions.OperationInterruptedException>(
                () => channel.QueueDeclareAsync("cadastro.rogue", durable: true, exclusive: false, autoDelete: false, cancellationToken: TestContext.Current.CancellationToken));
        }

        await using (var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            await Should.ThrowAsync<RabbitMQ.Client.Exceptions.OperationInterruptedException>(
                () => channel.ExchangeDeclareAsync("cadastro.rogue", "topic", durable: true, autoDelete: false, cancellationToken: TestContext.Current.CancellationToken));
        }

        // write fora de cadastro.events|cadastro.retry também é recusado.
        await using (var channel = await connection.CreateChannelAsync(new RabbitMQ.Client.CreateChannelOptions(true, true), TestContext.Current.CancellationToken))
        {
            await Should.ThrowAsync<Exception>(
                () => channel.BasicPublishAsync("amq.topic", "x", mandatory: false, new RabbitMQ.Client.BasicProperties(), new byte[] { 1 }, TestContext.Current.CancellationToken).AsTask());
        }
    }

    [Fact]
    public async Task Publisher_AfterTheBrokerRestarts_RecreatesTheConnectionAndPublishesAgainWithoutLeakingTheOldOne()
    {
        await ResetAsync();
        var options = broker.Options();
        options.ClientProvidedName = "cadastro-api-recreate-" + Guid.NewGuid().ToString("N");
        // Recuperação automática praticamente desligada: só a recriação pelo publicador consegue voltar a publicar.
        options.NetworkRecoveryIntervalSeconds = 3600;
        options.ConnectionRecreateAfterSeconds = 1;
        await using var publisher = new RabbitMqOutboxPublisher(Options.Create(options), NullLogger<RabbitMqOutboxPublisher>.Instance);
        OutboundMessage NewMessage() => new(Guid.CreateVersion7(), RabbitMqFixture.RoutingKey, "{}", null, DateTimeOffset.UtcNow);

        (await publisher.PublishAsync([NewMessage()], TestContext.Current.CancellationToken)).Single().Confirmed.ShouldBeTrue();

        await broker.StopAsync();
        try
        {
            var down = (await publisher.PublishAsync([NewMessage()], TestContext.Current.CancellationToken)).Single();
            down.Confirmed.ShouldBeFalse();
            down.ErrorCode.ShouldBe("broker.unavailable");
            publisher.IsDegraded.ShouldBeTrue();
        }
        finally
        {
            await broker.StartAsync();
        }

        // Passado o prazo de recriação, a próxima publicação abre uma conexão nova sem reiniciar nada.
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
        var outcome = (await publisher.PublishAsync([NewMessage()], TestContext.Current.CancellationToken)).Single();

        outcome.Confirmed.ShouldBeTrue();
        publisher.IsDegraded.ShouldBeFalse();
        (await broker.CountConnectionsAsync(options.ClientProvidedName)).ShouldBe(1);
    }

    [Fact]
    public async Task Publisher_WithoutBrokerCredentials_ReportsBrokerUnavailableInsteadOfThrowing()
    {
        var options = broker.Options();
        options.Password = "wrong-password";
        await using var publisher = new RabbitMqOutboxPublisher(Options.Create(options), NullLogger<RabbitMqOutboxPublisher>.Instance);

        var outcomes = await publisher.PublishAsync(
            [new OutboundMessage(Guid.CreateVersion7(), RabbitMqFixture.RoutingKey, "{}", null, DateTimeOffset.UtcNow)], TestContext.Current.CancellationToken);

        outcomes.Single().Confirmed.ShouldBeFalse();
        outcomes.Single().ErrorCode.ShouldBe("broker.unavailable");
        publisher.IsDegraded.ShouldBeTrue();
    }
}
