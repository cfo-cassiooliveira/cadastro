using System.Globalization;
using System.Text;
using Cadastro.Application.UserImports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Cadastro.Infrastructure.Messaging;

/// <summary>
/// Consome <c>cadastro.user-import-lines</c> em segundo plano: uma conexão longa (<c>cadastro-consumer</c>), canal próprio do
/// consumidor com prefetch e ack manual, e um segundo canal com confirms para a fila de retry. O ack só acontece depois de o
/// caso de uso concluir e comitar. Nunca declara exchange, fila nem binding (o <c>cadastro-app</c> não tem configure).
/// Ack depois do commit troca perda por repetição: uma queda no meio reentrega a linha. A retry é uma publicação nova,
/// confirmada antes do ack da original, para a mensagem não se perder entre as duas. O prefetch limita o paralelismo, e o
/// consumidor roda no processo da API (ADR-0009), dividindo recursos com as requisições.
/// </summary>
internal sealed partial class UserImportLineConsumerService(
    IOptions<RabbitMqOptions> rabbitOptions,
    IOptions<ImportConsumerOptions> consumerOptions,
    LineDeliveryProcessor processor,
    ILogger<UserImportLineConsumerService> logger) : BackgroundService
{
    internal const string RetryCountHeader = "x-retry-count";
    private const string TraceParentHeader = "traceparent";
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(2);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _republishFailures = new();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = consumerOptions.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeUntilBrokenAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Broker fora ou credencial recusada: loga uma vez por tentativa e reconecta; o consumidor nunca derruba o processo.
                LogConnectionFailed(ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.ReconnectDelaySeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal static int ReadAttempt(IReadOnlyBasicProperties properties) =>
        properties.Headers is { } headers && headers.TryGetValue(RetryCountHeader, out var value)
            ? value switch
            {
                int i => i,
                long l => (int)l,
                byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0,
            }
            : 0;

    private static string? ReadTraceParent(IReadOnlyBasicProperties properties) =>
        properties.Headers is { } headers && headers.TryGetValue(TraceParentHeader, out var value)
            ? value switch { byte[] bytes => Encoding.UTF8.GetString(bytes), string text => text, _ => null }
            : null;

    private async Task ConsumeUntilBrokenAsync(CancellationToken cancellationToken)
    {
        var rabbit = rabbitOptions.Value;
        var settings = consumerOptions.Value;
        var factory = new ConnectionFactory
        {
            HostName = rabbit.Host,
            Port = rabbit.Port,
            VirtualHost = rabbit.VirtualHost,
            UserName = rabbit.UserName,
            Password = rabbit.Password,
            ClientProvidedName = settings.ClientProvidedName,
            // Recuperação automática religa conexão e consumidor; não há fila declarada por este cliente, então nada é redeclarado.
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            NetworkRecoveryInterval = TimeSpan.FromSeconds(rabbit.NetworkRecoveryIntervalSeconds),
            ConsumerDispatchConcurrency = (ushort)settings.Prefetch,
        };
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var consumeChannel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await using var retryChannel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), cancellationToken);
        await consumeChannel.BasicQosAsync(0, (ushort)settings.Prefetch, false, cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(consumeChannel);
        consumer.ReceivedAsync += (_, delivery) => HandleDeliveryAsync(consumeChannel, retryChannel, delivery, cancellationToken);
        await consumeChannel.BasicConsumeAsync(settings.Queue, autoAck: false, consumer, cancellationToken);
        LogConsuming(settings.Queue, settings.Prefetch);

        // A recuperação automática cuida das quedas curtas; passado o prazo sem conexão, descarta tudo e reconecta do zero.
        var downFor = TimeSpan.Zero;
        var recreateAfter = TimeSpan.FromSeconds(rabbit.ConnectionRecreateAfterSeconds);
        while (downFor < recreateAfter)
        {
            await Task.Delay(WatchdogInterval, cancellationToken);
            downFor = connection.IsOpen && consumeChannel.IsOpen ? TimeSpan.Zero : downFor + WatchdogInterval;
        }
    }

    /// <summary>Trata uma entrega: processa, e então confirma, republica na retry ou recusa para a DLQ.</summary>
    internal async Task HandleDeliveryAsync(IChannel consumeChannel, IChannel retryChannel, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        try
        {
            var attempt = ReadAttempt(delivery.BasicProperties);
            var decision = await processor.ProcessAsync(delivery.Body, attempt, ReadTraceParent(delivery.BasicProperties), cancellationToken);
            switch (decision.Action)
            {
                case DeliveryAction.Ack:
                    await AckAsync(consumeChannel, delivery, cancellationToken);
                    break;
                case DeliveryAction.Retry:
                    await RetryAsync(consumeChannel, retryChannel, delivery, decision, cancellationToken);
                    break;
                default:
                    LogRejected(decision.Code ?? "unknown");
                    await consumeChannel.BasicNackAsync(delivery.DeliveryTag, false, requeue: false, cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Desligamento: sem ack, a mensagem volta à fila quando o canal fecha.
        }
        catch (Exception ex)
        {
            // Falha ao confirmar/recusar (canal caiu): o broker reentrega a mensagem; o efeito é idempotente (inbox).
            LogAcknowledgeFailed(ex);
        }
    }

    // Ack que falha com o canal ainda aberto é raro; espera um pouco e tenta de novo uma vez. Se falhar de novo, a reentrega cai na
    // inbox como duplicata (o efeito já foi gravado antes do ack) e é confirmada sem repetir nada.
    private async Task AckAsync(IChannel consumeChannel, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        try
        {
            await consumeChannel.BasicAckAsync(delivery.DeliveryTag, false, cancellationToken);
        }
        catch (Exception ex) when (consumeChannel.IsOpen && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            LogAckRetry(ex);
            await Task.Delay(BackoffFor(1), cancellationToken);
            await consumeChannel.BasicAckAsync(delivery.DeliveryTag, false, cancellationToken);
        }
    }

    private async Task RetryAsync(
        IChannel consumeChannel, IChannel retryChannel, BasicDeliverEventArgs delivery, DeliveryDecision decision, CancellationToken cancellationToken)
    {
        var key = delivery.BasicProperties.MessageId ?? delivery.DeliveryTag.ToString(CultureInfo.InvariantCulture);
        try
        {
            await PublishRetryAsync(retryChannel, delivery, decision.NextAttempt, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await HandleRetryPublishFailureAsync(consumeChannel, delivery, decision, key, ex, cancellationToken);
            return;
        }

        _republishFailures.TryRemove(key, out _);
        LogRetryScheduled(decision.NextAttempt, decision.Code ?? "unknown");
        await AckAsync(consumeChannel, delivery, cancellationToken);
    }

    // A retry não foi confirmada. Reenfileirar sem pausa gira em falso e, ao esgotar o delivery-limit da fila, o broker mandaria a
    // mensagem à DLQ com a linha ainda Pending (lote preso em Processing). Por isso: pausa com backoff e jitter entre as reentregas
    // e, depois de MaxRepublishFailures, a linha é encerrada como erro ANTES de a mensagem ir à DLQ. Se nem isso der certo
    // (banco fora), a mensagem continua viva.
    private async Task HandleRetryPublishFailureAsync(
        IChannel consumeChannel, BasicDeliverEventArgs delivery, DeliveryDecision decision, string key, Exception exception, CancellationToken cancellationToken)
    {
        var settings = consumerOptions.Value;
        var failures = Math.Max(_republishFailures.AddOrUpdate(key, 1, (_, count) => count + 1), ReadDeliveryCount(delivery.BasicProperties));
        LogRetryPublishFailed(exception, decision.NextAttempt, failures);
        if (failures >= settings.MaxRepublishFailures)
        {
            var code = decision.NextAttempt > ImportRetryPolicy.MaxCycles ? ImportFailureCodes.RetriesExhausted : ImportFailureCodes.ProcessingFailed;
            if (await processor.TryFailLineAsync(delivery.Body, code, cancellationToken))
            {
                _republishFailures.TryRemove(key, out _);
                LogRejected(code);
                await consumeChannel.BasicNackAsync(delivery.DeliveryTag, false, requeue: false, cancellationToken);
                return;
            }
        }

        await Task.Delay(BackoffFor(failures), cancellationToken);
        await consumeChannel.BasicNackAsync(delivery.DeliveryTag, false, requeue: true, cancellationToken);
    }

    private TimeSpan BackoffFor(int failures) =>
        OutboxBackoff.Next(failures, TimeSpan.FromMilliseconds(consumerOptions.Value.RepublishBackoffMs), TimeSpan.FromSeconds(10), Random.Shared.NextDouble());

    // O quorum queue grava x-delivery-count nas reentregas; sobrevive a reinício do processo (o contador local não).
    private static int ReadDeliveryCount(IReadOnlyBasicProperties properties) =>
        properties.Headers is { } headers && headers.TryGetValue("x-delivery-count", out var value) && value is int or long
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : 0;

    private async Task PublishRetryAsync(IChannel retryChannel, BasicDeliverEventArgs delivery, int nextAttempt, CancellationToken cancellationToken)
    {
        var source = delivery.BasicProperties;
        var headers = new Dictionary<string, object?> { [RetryCountHeader] = nextAttempt };
        if (ReadTraceParent(source) is { } traceParent)
        {
            headers[TraceParentHeader] = traceParent;
        }

        var properties = new BasicProperties
        {
            MessageId = source.MessageId,
            Type = source.Type,
            ContentType = source.ContentType,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = source.Timestamp,
            Headers = headers,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(consumerOptions.Value.RetryConfirmTimeoutSeconds));
        // O mesmo messageId e o mesmo corpo seguem no retry: a inbox descarta a duplicata se o efeito já foi gravado.
        await retryChannel.BasicPublishAsync(
            consumerOptions.Value.RetryExchange, ImportLimits.LineAcceptedEventType, mandatory: true, properties, delivery.Body, timeout.Token);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Consuming queue {Queue} with prefetch {Prefetch}")]
    private partial void LogConsuming(string queue, int prefetch);

    [LoggerMessage(Level = LogLevel.Error, Message = "The import consumer lost or could not open the broker connection; reconnecting")]
    private partial void LogConnectionFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejecting message to the dead-letter queue: {Code}")]
    private partial void LogRejected(string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled retry cycle {Attempt} ({Code})")]
    private partial void LogRetryScheduled(int attempt, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Publishing retry cycle {Attempt} was not confirmed (failure {Failures} for this message)")]
    private partial void LogRetryPublishFailed(Exception exception, int attempt, int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Acknowledging a delivery failed with the channel open; retrying once")]
    private partial void LogAckRetry(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not acknowledge a delivery; the broker will redeliver it")]
    private partial void LogAcknowledgeFailed(Exception exception);
}
