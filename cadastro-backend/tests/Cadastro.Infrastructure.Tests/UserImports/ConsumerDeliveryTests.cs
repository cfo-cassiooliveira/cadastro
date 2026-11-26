using System.Text;
using Cadastro.Application.UserImports;
using Cadastro.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

// Configuração de ValueTask em mocks do NSubstitute: o retorno é consumido pelo próprio substituto, não por este código.
#pragma warning disable CA2012

namespace Cadastro.Infrastructure.Tests.UserImports;

/// <summary>
/// Caminhos AMQP do consumidor com canais falsos: publicação da retry falhando, ack falhando e a garantia de que nenhuma mensagem
/// vai à DLQ com a linha ainda pendente.
/// </summary>
public sealed class ConsumerDeliveryTests
{
    private static readonly Guid BatchId = Guid.NewGuid();
    private static readonly Guid LineId = Guid.NewGuid();
    private static readonly Guid MessageId = Guid.NewGuid();

    private readonly IProcessUserImportLineHandler _process = Substitute.For<IProcessUserImportLineHandler>();
    private readonly IFailUserImportLineHandler _fail = Substitute.For<IFailUserImportLineHandler>();
    private readonly IChannel _consume = Substitute.For<IChannel>();
    private readonly IChannel _retry = Substitute.For<IChannel>();

    public ConsumerDeliveryTests()
    {
        _consume.IsOpen.Returns(true);
        _fail.HandleAsync(Arg.Any<FailUserImportLineCommand>(), Arg.Any<CancellationToken>())
            .Returns(new LineProcessingResult(LineProcessingDisposition.Failed, "x"));
    }

    private UserImportLineConsumerService Service(int maxRepublishFailures = 3)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_process);
        services.AddSingleton(_fail);
        var processor = new LineDeliveryProcessor(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<LineDeliveryProcessor>.Instance);
        return new UserImportLineConsumerService(
            Options.Create(new RabbitMqOptions()),
            Options.Create(new ImportConsumerOptions { MaxRepublishFailures = maxRepublishFailures, RepublishBackoffMs = 1 }),
            processor,
            NullLogger<UserImportLineConsumerService>.Instance);
    }

    private static BasicDeliverEventArgs Delivery(Dictionary<string, object?>? headers = null) =>
        new("tag", 7, false, "cadastro.events", "cadastro.user-import-line.accepted.v1",
            new BasicProperties { MessageId = MessageId.ToString("D"), Headers = headers },
            Encoding.UTF8.GetBytes(EventEnvelope.CreateLineAccepted(MessageId, BatchId, LineId, "corr-12345678", null, DateTimeOffset.UtcNow)));

    private void ProcessReturns(LineProcessingResult result) =>
        _process.HandleAsync(Arg.Any<ProcessUserImportLineCommand>(), Arg.Any<CancellationToken>()).Returns(result);

    private void PublishToRetryFails() =>
        _retry.BasicPublishAsync(default(string)!, default(string)!, default, default(BasicProperties)!, default, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(_ => throw new AlreadyClosedException(new ShutdownEventArgs(ShutdownInitiator.Library, 0, "down")));

    private static readonly LineProcessingResult RetryLater = new(LineProcessingDisposition.RetryLater, "identity.unavailable");

    [Fact]
    public async Task Completed_IsAcknowledgedOnly()
    {
        ProcessReturns(new LineProcessingResult(LineProcessingDisposition.Completed));

        await Service().HandleDeliveryAsync(_consume, _retry, Delivery(), TestContext.Current.CancellationToken);

        await _consume.Received(1).BasicAckAsync(7, false, Arg.Any<CancellationToken>());
        await _consume.DidNotReceiveWithAnyArgs().BasicNackAsync(default, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Failed_IsRejectedWithoutRequeue()
    {
        ProcessReturns(new LineProcessingResult(LineProcessingDisposition.Failed, "identity.rejected"));

        await Service().HandleDeliveryAsync(_consume, _retry, Delivery(), TestContext.Current.CancellationToken);

        await _consume.Received(1).BasicNackAsync(7, false, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryLater_PublishesToTheRetryExchangeWithTheNextCycleThenAcks()
    {
        ProcessReturns(RetryLater);

        await Service().HandleDeliveryAsync(_consume, _retry, Delivery(new Dictionary<string, object?> { [UserImportLineConsumerService.RetryCountHeader] = 2 }), TestContext.Current.CancellationToken);

        await _retry.Received(1).BasicPublishAsync(
            "cadastro.retry", ImportLimits.LineAcceptedEventType, true,
            Arg.Is<BasicProperties>(p => p.MessageId == MessageId.ToString("D") && (int)p.Headers![UserImportLineConsumerService.RetryCountHeader]! == 3),
            Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>());
        await _consume.Received(1).BasicAckAsync(7, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryPublishFailing_FirstTime_WaitsAndRequeuesWithoutMarkingTheLine()
    {
        ProcessReturns(RetryLater);
        PublishToRetryFails();

        await Service().HandleDeliveryAsync(_consume, _retry, Delivery(), TestContext.Current.CancellationToken);

        await _consume.Received(1).BasicNackAsync(7, false, true, Arg.Any<CancellationToken>());
        await _consume.DidNotReceive().BasicAckAsync(Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _fail.DidNotReceiveWithAnyArgs().HandleAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RetryPublishFailing_Repeatedly_MarksTheLineAsFailedBeforeDeadLettering()
    {
        ProcessReturns(RetryLater);
        PublishToRetryFails();
        var service = Service(maxRepublishFailures: 3);

        for (var i = 0; i < 3; i++)
        {
            await service.HandleDeliveryAsync(_consume, _retry, Delivery(), TestContext.Current.CancellationToken);
        }

        await _fail.Received(1).HandleAsync(
            new FailUserImportLineCommand(MessageId, BatchId, LineId, ImportFailureCodes.ProcessingFailed), Arg.Any<CancellationToken>());
        await _consume.Received(2).BasicNackAsync(7, false, true, Arg.Any<CancellationToken>());
        await _consume.Received(1).BasicNackAsync(7, false, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryPublishFailing_OnTheLastCycle_MarksTheLineAsRetriesExhausted()
    {
        ProcessReturns(RetryLater);
        PublishToRetryFails();

        await Service(maxRepublishFailures: 1).HandleDeliveryAsync(
            _consume, _retry, Delivery(new Dictionary<string, object?> { [UserImportLineConsumerService.RetryCountHeader] = ImportRetryPolicy.MaxCycles }),
            TestContext.Current.CancellationToken);

        await _fail.Received(1).HandleAsync(
            new FailUserImportLineCommand(MessageId, BatchId, LineId, ImportFailureCodes.RetriesExhausted), Arg.Any<CancellationToken>());
        await _consume.Received(1).BasicNackAsync(7, false, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryPublishFailing_WithHighBrokerDeliveryCount_GivesUpAtOnceAfterMarkingTheLine()
    {
        ProcessReturns(RetryLater);
        PublishToRetryFails();

        await Service(maxRepublishFailures: 3).HandleDeliveryAsync(
            _consume, _retry, Delivery(new Dictionary<string, object?> { ["x-delivery-count"] = 3L }), TestContext.Current.CancellationToken);

        await _fail.Received(1).HandleAsync(Arg.Any<FailUserImportLineCommand>(), Arg.Any<CancellationToken>());
        await _consume.Received(1).BasicNackAsync(7, false, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryPublishFailing_WhenMarkingTheLineAlsoFails_NeverDeadLettersAPendingLine()
    {
        ProcessReturns(RetryLater);
        PublishToRetryFails();
        _fail.HandleAsync(Arg.Any<FailUserImportLineCommand>(), Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("db down"));

        await Service(maxRepublishFailures: 1).HandleDeliveryAsync(_consume, _retry, Delivery(), TestContext.Current.CancellationToken);

        await _consume.Received(1).BasicNackAsync(7, false, true, Arg.Any<CancellationToken>());
        await _consume.DidNotReceive().BasicNackAsync(7, false, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AckFailingWithTheChannelOpen_IsRetriedOnceAfterAPause()
    {
        ProcessReturns(new LineProcessingResult(LineProcessingDisposition.Completed));
        var calls = 0;
        _consume.BasicAckAsync(default, default, TestContext.Current.CancellationToken).ReturnsForAnyArgs(_ =>
            ++calls == 1 ? throw new InvalidOperationException("ack failed") : ValueTask.CompletedTask);

        await Service().HandleDeliveryAsync(_consume, _retry, Delivery(), TestContext.Current.CancellationToken);

        calls.ShouldBe(2);
        await _consume.DidNotReceiveWithAnyArgs().BasicNackAsync(default, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AckFailingTwice_DoesNotThrowAndDoesNotNack()
    {
        ProcessReturns(new LineProcessingResult(LineProcessingDisposition.Completed));
        _consume.BasicAckAsync(default, default, TestContext.Current.CancellationToken).ReturnsForAnyArgs<ValueTask>(_ => throw new InvalidOperationException("ack failed"));

        await Service().HandleDeliveryAsync(_consume, _retry, Delivery(), TestContext.Current.CancellationToken);

        await _consume.DidNotReceiveWithAnyArgs().BasicNackAsync(default, default, default, TestContext.Current.CancellationToken);
    }
}
