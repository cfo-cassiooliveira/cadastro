using System.Text;
using Cadastro.Application.UserImports;
using Cadastro.Infrastructure.Keycloak;
using Cadastro.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RabbitMQ.Client;

namespace Cadastro.Infrastructure.Tests.UserImports;

/// <summary>Regras do consumidor sem broker nem banco: rate limiter, decisão por entrega e leitura do contador de ciclos.</summary>
public sealed class ConsumerUnitTests
{
    private static readonly Guid BatchId = Guid.NewGuid();
    private static readonly Guid LineId = Guid.NewGuid();
    private static readonly Guid MessageId = Guid.NewGuid();

    private readonly IProcessUserImportLineHandler _process = Substitute.For<IProcessUserImportLineHandler>();
    private readonly IFailUserImportLineHandler _fail = Substitute.For<IFailUserImportLineHandler>();

    private LineDeliveryProcessor Processor()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_process);
        services.AddSingleton(_fail);
        return new LineDeliveryProcessor(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<LineDeliveryProcessor>.Instance);
    }

    private static byte[] Body() =>
        Encoding.UTF8.GetBytes(EventEnvelope.CreateLineAccepted(MessageId, BatchId, LineId, "corr-12345678", null, DateTimeOffset.UtcNow));

    private Task<DeliveryDecision> RunAsync(int attempt = 0, byte[]? body = null) =>
        Processor().ProcessAsync(body ?? Body(), attempt, "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", TestContext.Current.CancellationToken);

    [Fact]
    public async Task RateLimiter_SpacesCreationsAtTenPerSecond()
    {
        var clock = new FakeTimeProvider();
        var limiter = new CreationRateLimiter(10, clock);

        var first = limiter.WaitAsync(TestContext.Current.CancellationToken);
        var second = limiter.WaitAsync(TestContext.Current.CancellationToken);
        var third = limiter.WaitAsync(TestContext.Current.CancellationToken);

        first.IsCompletedSuccessfully.ShouldBeTrue();
        second.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(99));
        second.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await second;
        third.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await third;
    }

    [Fact]
    public async Task RateLimiter_TwentyFivePermitsNeverExceedTenPerSecond()
    {
        var clock = new FakeTimeProvider();
        var limiter = new CreationRateLimiter(10, clock);
        var start = clock.GetUtcNow();
        var waits = Enumerable.Range(0, 25).Select(_ => limiter.WaitAsync(TestContext.Current.CancellationToken)).ToList();

        while (waits.Any(w => !w.IsCompleted))
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            await Task.Yield();
        }

        // 25 permissões a 10/s levam pelo menos 2,4 s (a primeira sai na hora).
        (clock.GetUtcNow() - start).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(2400));
    }

    [Fact]
    public async Task Delivery_InvalidBody_IsRejectedWithoutCallingTheUseCase()
    {
        var decision = await RunAsync(body: Encoding.UTF8.GetBytes("garbage"));

        decision.ShouldBe(new DeliveryDecision(DeliveryAction.Reject, Code: ImportFailureCodes.MessageInvalid));
        await _process.DidNotReceiveWithAnyArgs().HandleAsync(default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(LineProcessingDisposition.Completed)]
    [InlineData(LineProcessingDisposition.Duplicate)]
    public async Task Delivery_CompletedOrDuplicate_IsAcknowledged(LineProcessingDisposition disposition)
    {
        _process.HandleAsync(Arg.Any<ProcessUserImportLineCommand>(), Arg.Any<CancellationToken>()).Returns(new LineProcessingResult(disposition));

        (await RunAsync()).Action.ShouldBe(DeliveryAction.Ack);
    }

    [Fact]
    public async Task Delivery_PassesTheEnvelopeIdsAndTheCycleCounter()
    {
        _process.HandleAsync(Arg.Any<ProcessUserImportLineCommand>(), Arg.Any<CancellationToken>()).Returns(new LineProcessingResult(LineProcessingDisposition.Completed));

        await RunAsync(attempt: 3);

        await _process.Received(1).HandleAsync(new ProcessUserImportLineCommand(MessageId, BatchId, LineId, 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delivery_RetryLater_PublishesTheNextCycle()
    {
        _process.HandleAsync(Arg.Any<ProcessUserImportLineCommand>(), Arg.Any<CancellationToken>())
            .Returns(new LineProcessingResult(LineProcessingDisposition.RetryLater, "identity.unavailable"));

        (await RunAsync(attempt: 2)).ShouldBe(new DeliveryDecision(DeliveryAction.Retry, 3, "identity.unavailable"));
    }

    [Fact]
    public async Task Delivery_Failed_IsRejectedToTheDeadLetterQueue()
    {
        _process.HandleAsync(Arg.Any<ProcessUserImportLineCommand>(), Arg.Any<CancellationToken>())
            .Returns(new LineProcessingResult(LineProcessingDisposition.Failed, "identity.rejected"));

        (await RunAsync()).ShouldBe(new DeliveryDecision(DeliveryAction.Reject, Code: "identity.rejected"));
    }

    [Fact]
    public async Task Delivery_UnexpectedException_RetriesWhileCyclesRemain()
    {
        _process.HandleAsync(Arg.Any<ProcessUserImportLineCommand>(), Arg.Any<CancellationToken>()).Returns<LineProcessingResult>(_ => throw new InvalidOperationException("boom"));

        var decision = await RunAsync(attempt: 1);

        decision.ShouldBe(new DeliveryDecision(DeliveryAction.Retry, 2, ImportFailureCodes.ProcessingFailed));
        await _fail.DidNotReceiveWithAnyArgs().HandleAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Delivery_UnexpectedException_AfterFiveCycles_FailsTheLineAndRejects()
    {
        _process.HandleAsync(Arg.Any<ProcessUserImportLineCommand>(), Arg.Any<CancellationToken>()).Returns<LineProcessingResult>(_ => throw new InvalidOperationException("boom"));

        var decision = await RunAsync(attempt: ImportRetryPolicy.MaxCycles);

        decision.ShouldBe(new DeliveryDecision(DeliveryAction.Reject, Code: ImportFailureCodes.RetriesExhausted));
        await _fail.Received(1).HandleAsync(new FailUserImportLineCommand(MessageId, BatchId, LineId, ImportFailureCodes.RetriesExhausted), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delivery_WhenMarkingTheLineAlsoFails_StillRejects()
    {
        _process.HandleAsync(Arg.Any<ProcessUserImportLineCommand>(), Arg.Any<CancellationToken>()).Returns<LineProcessingResult>(_ => throw new InvalidOperationException("boom"));
        _fail.HandleAsync(Arg.Any<FailUserImportLineCommand>(), Arg.Any<CancellationToken>()).Returns<LineProcessingResult>(_ => throw new InvalidOperationException("db down"));

        (await RunAsync(attempt: ImportRetryPolicy.MaxCycles)).Action.ShouldBe(DeliveryAction.Reject);
    }

    [Fact]
    public async Task Delivery_Cancellation_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _process.HandleAsync(Arg.Any<ProcessUserImportLineCommand>(), Arg.Any<CancellationToken>()).Returns<LineProcessingResult>(_ => throw new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => Processor().ProcessAsync(Body(), 0, null, cts.Token));
    }

    [Fact]
    public void ReadAttempt_ParsesTheRetryHeaderInAnyBrokerRepresentation()
    {
        int Read(object? value)
        {
            var properties = Substitute.For<IReadOnlyBasicProperties>();
            properties.Headers.Returns(value is null ? null : new Dictionary<string, object?> { [UserImportLineConsumerService.RetryCountHeader] = value });
            return UserImportLineConsumerService.ReadAttempt(properties);
        }

        Read(null).ShouldBe(0);
        Read(3).ShouldBe(3);
        Read(4L).ShouldBe(4);
        Read(Encoding.UTF8.GetBytes("2")).ShouldBe(2);
        Read("junk").ShouldBe(0);
    }
}
