using System.Text;
using Cadastro.Application.UserImports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Cadastro.Infrastructure.Messaging;

/// <summary>
/// Publica as mensagens da outbox no RabbitMQ 7.x: uma <see cref="IConnection"/> longa (singleton, recuperação automática),
/// um canal com publisher confirms e rastreamento, mensagens persistentes e <c>mandatory</c>. Nunca declara exchange, fila
/// nem binding (o usuário <c>cadastro-app</c> não tem configure). Falhas viram desfecho por mensagem, nunca exceção.
/// Sem confirms e <c>mandatory</c>, uma mensagem recusada ou sem fila de destino seria dada como publicada. O custo é esperar o
/// broker a cada publicação e, se a confirmação se perder, publicar de novo (o consumidor descarta pela inbox).
/// </summary>
internal sealed partial class RabbitMqOutboxPublisher(IOptions<RabbitMqOptions> options, ILogger<RabbitMqOutboxPublisher> logger)
    : IOutboxPublisher, IAsyncDisposable
{
    private const string TraceParentHeader = "traceparent";
    private const string ContentType = "application/json";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    private volatile bool _lastConnectFailed;
    private long _connectionDownSinceTicks;

    /// <summary>Indica se há conexão e canal abertos (usado pelo health check, sem abrir conexão).</summary>
    public bool IsConnected => _connection is { IsOpen: true } && _channel is { IsOpen: true };

    /// <summary>Indica que a última tentativa de conectar falhou ou a conexão aberta caiu; antes da primeira publicação é <c>false</c>.</summary>
    public bool IsDegraded => _lastConnectFailed || (_connection is not null && !IsConnected);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PublishOutcome>> PublishAsync(IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        IChannel channel;
        try
        {
            channel = await GetChannelAsync(cancellationToken);
            _lastConnectFailed = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Broker fora do ar: a causa vai ao log uma vez; as mensagens continuam pendentes na outbox.
            _lastConnectFailed = true;
            LogUnavailable(ex);
            return [.. messages.Select(m => new PublishOutcome(m.MessageId, false, PublishErrorCodes.BrokerUnavailable))];
        }

        var settings = options.Value;
        var tasks = messages.Select(message => PublishOneAsync(channel, settings, message, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    /// <summary>Fecha o canal e a conexão.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }

    private async Task<PublishOutcome> PublishOneAsync(IChannel channel, RabbitMqOptions settings, OutboundMessage message, CancellationToken cancellationToken)
    {
        var properties = new BasicProperties
        {
            MessageId = message.MessageId.ToString("D"),
            Type = message.RoutingKey,
            ContentType = ContentType,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(message.OccurredAtUtc.ToUnixTimeSeconds()),
            Headers = message.TraceParent is null ? null : new Dictionary<string, object?> { [TraceParentHeader] = message.TraceParent },
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ConfirmTimeoutSeconds));
        try
        {
            // Com o rastreamento de confirmações ligado, o await só termina depois do ack do broker.
            await channel.BasicPublishAsync(
                settings.Exchange, message.RoutingKey, mandatory: true, properties, Encoding.UTF8.GetBytes(message.Body), timeout.Token);
            return new PublishOutcome(message.MessageId, true, null);
        }
        catch (PublishReturnException)
        {
            LogRejected(message.MessageId, PublishErrorCodes.Unroutable);
            return new PublishOutcome(message.MessageId, false, PublishErrorCodes.Unroutable);
        }
        catch (PublishException)
        {
            LogRejected(message.MessageId, PublishErrorCodes.Nack);
            return new PublishOutcome(message.MessageId, false, PublishErrorCodes.Nack);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogPublishFailed(ex, message.MessageId);
            var code = ex is AlreadyClosedException or BrokerUnreachableException or OperationInterruptedException
                ? PublishErrorCodes.BrokerUnavailable
                : PublishErrorCodes.PublishFailed;
            return new PublishOutcome(message.MessageId, false, code);
        }
    }

    // Reabre só se a conexão/canal caiu; a recuperação automática cuida das quedas curtas.
    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectionUsableAsync();
            if (_channel is { IsOpen: true } open && _connection is { IsOpen: true })
            {
                return open;
            }

            if (_channel is not null)
            {
                await _channel.DisposeAsync();
                _channel = null;
            }

            _connection ??= await CreateConnectionAsync(cancellationToken);
            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                cancellationToken);
            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Conexão fechada: a recuperação automática tem um prazo para religar; passado o prazo (ela desistiu ou foi fechada pela
    // aplicação) a conexão e o canal velhos são descartados e uma nova conexão é criada, sem reiniciar a API e sem vazar a antiga.
    // Dentro do prazo a chamada falha como indisponível, o que evita uma tempestade de reconexões a cada ciclo do relay.
    private async Task EnsureConnectionUsableAsync()
    {
        if (_connection is null || _connection.IsOpen)
        {
            _connectionDownSinceTicks = 0;
            return;
        }

        var now = Environment.TickCount64;
        if (_connectionDownSinceTicks == 0)
        {
            _connectionDownSinceTicks = now;
        }

        if (now - _connectionDownSinceTicks < TimeSpan.FromSeconds(options.Value.ConnectionRecreateAfterSeconds).TotalMilliseconds)
        {
            throw new InvalidOperationException("The broker connection is recovering.");
        }

        await DiscardConnectionAsync();
        _connectionDownSinceTicks = 0;
    }

    private async Task DiscardConnectionAsync()
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;
        try
        {
            if (channel is not null)
            {
                await channel.DisposeAsync();
            }

            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A conexão velha já estava morta: o descarte é melhor esforço e não impede a nova conexão.
            LogDiscardFailed(ex);
        }
    }

    private Task<IConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var factory = new ConnectionFactory
        {
            HostName = settings.Host,
            Port = settings.Port,
            VirtualHost = settings.VirtualHost,
            UserName = settings.UserName,
            Password = settings.Password,
            ClientProvidedName = settings.ClientProvidedName,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = false,
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            NetworkRecoveryInterval = TimeSpan.FromSeconds(settings.NetworkRecoveryIntervalSeconds),
        };
        return factory.CreateConnectionAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Discarding the dead broker connection did not complete cleanly")]
    private partial void LogDiscardFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The message broker is unavailable; outbox messages stay pending")]
    private partial void LogUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} not confirmed by the broker: {ErrorCode}")]
    private partial void LogRejected(Guid messageId, string errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Publishing outbox message {MessageId} failed")]
    private partial void LogPublishFailed(Exception exception, Guid messageId);
}
