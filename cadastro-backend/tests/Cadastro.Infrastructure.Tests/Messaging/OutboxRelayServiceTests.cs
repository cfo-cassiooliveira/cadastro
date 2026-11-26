using Cadastro.Application.UserImports;
using Cadastro.Infrastructure.Messaging;
using Cadastro.Infrastructure.Persistence;
using Cadastro.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Tests.Messaging;

/// <summary>Laço em segundo plano do relay: drena a outbox, pausa com o broker fora e sobrevive a falhas inesperadas (QA da onda 2).</summary>
[Collection(ImportDatabaseSharing.Name)]
public sealed class OutboxRelayServiceTests(ImportDatabaseFixture database)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 13, 0, 0, TimeSpan.Zero);

    private sealed class ScriptedPublisher(Func<IReadOnlyList<OutboundMessage>, IReadOnlyList<PublishOutcome>> script) : IOutboxPublisher
    {
        public int Calls;

        public Task<IReadOnlyList<PublishOutcome>> PublishAsync(IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(script(messages));
        }
    }

    private async Task SeedAsync(int count)
    {
        await database.MigrateAsync();
        await using var db = ImportDatabaseFixture.CreateContext(database.AppConnectionString);
        await db.OutboxMessages.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < count; i++)
        {
            var id = Guid.CreateVersion7();
            db.OutboxMessages.Add(OutboxMessage.Create(id, "cadastro.user-import-line.accepted.v1", EventEnvelope.CreateLineAccepted(id, Guid.NewGuid(), Guid.NewGuid(), "c", "d", Now), null, Now));
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private OutboxRelayService NewService(IOutboxPublisher publisher)
    {
        var options = Options.Create(new RabbitMqOptions { RelayIdleDelayMs = 20, RelayBatchSize = 10 });
        var services = new ServiceCollection();
        services.AddScoped(_ => ImportDatabaseFixture.CreateContext(database.AppConnectionString));
        services.AddSingleton(publisher);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddLogging();
        services.AddScoped<OutboxRelay>();
        var provider = services.BuildServiceProvider();
        return new OutboxRelayService(provider.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<OutboxRelayService>.Instance);
    }

    private async Task<int> PendingAsync()
    {
        await using var db = ImportDatabaseFixture.CreateContext(database.AppConnectionString);
        return await db.OutboxMessages.CountAsync(m => m.PublishedAtUtc == null, TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            (DateTime.UtcNow < deadline).ShouldBeTrue("condition not reached in time");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Service_DrainsEveryPendingMessageAcrossCyclesAndStopsOnCancellation()
    {
        await SeedAsync(25);
        var publisher = new ScriptedPublisher(messages => [.. messages.Select(m => new PublishOutcome(m.MessageId, true, null))]);
        using var service = NewService(publisher);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await PendingAsync() == 0);
        await service.StopAsync(TestContext.Current.CancellationToken);

        publisher.Calls.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Service_WhenBrokerIsDown_KeepsMessagesPendingAndPublishesAfterItRecovers()
    {
        await SeedAsync(3);
        var brokerUp = false;
        var publisher = new ScriptedPublisher(messages => [.. messages.Select(m => brokerUp
            ? new PublishOutcome(m.MessageId, true, null)
            : new PublishOutcome(m.MessageId, false, PublishErrorCodes.BrokerUnavailable))]);
        using var service = NewService(publisher);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Task.FromResult(publisher.Calls >= 1));
        (await PendingAsync()).ShouldBe(3);

        // Falha de broker adia as linhas com backoff curto; ao voltar, o relay as publica sem perda.
        brokerUp = true;
        await WaitUntilAsync(async () => await PendingAsync() == 0);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Service_SurvivesUnexpectedCycleFailureAndKeepsRunning()
    {
        await SeedAsync(2);
        var attempts = 0;
        var publisher = new ScriptedPublisher(messages =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("boom");
            }

            return [.. messages.Select(m => new PublishOutcome(m.MessageId, true, null))];
        });
        using var service = NewService(publisher);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await PendingAsync() == 0);
        await service.StopAsync(TestContext.Current.CancellationToken);

        attempts.ShouldBeGreaterThanOrEqualTo(2);
    }
}
