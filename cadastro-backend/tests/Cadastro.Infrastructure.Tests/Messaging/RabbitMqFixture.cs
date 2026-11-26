using System.Net;
using System.Net.Sockets;
using Cadastro.Tests.Shared;
using DotNet.Testcontainers.Builders;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Cadastro.Infrastructure.Tests.Messaging;

/// <summary>
/// RabbitMQ real (Testcontainers, mesma imagem por tag + digest do ../rabbitmq). A porta do host é fixa para o teste poder
/// parar e subir o broker sem mudar o endereço. A topologia (exchange <c>cadastro.events</c> e uma fila ligada) é criada aqui
/// com o usuário administrador do container: o código de produção nunca declara nada (o <c>cadastro-app</c> não tem configure).
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    /// <summary>Exchange de eventos do Cadastro.</summary>
    public const string Exchange = "cadastro.events";

    /// <summary>Routing key do evento da linha aceita.</summary>
    public const string RoutingKey = "cadastro.user-import-line.accepted.v1";

    /// <summary>Fila de teste ligada à exchange pela routing key.</summary>
    public const string Queue = "cadastro.user-import-lines";

    /// <summary>Exchange de retentativa (a fila de retry devolve à principal após o TTL).</summary>
    public const string RetryExchange = "cadastro.retry";

    /// <summary>Fila de retry; o TTL é curto aqui (1 s) para o teste não esperar os 30 s da produção.</summary>
    public const string RetryQueue = "cadastro.user-import-lines.retry";

    /// <summary>Exchange de dead-letter.</summary>
    public const string DlxExchange = "cadastro.dlx";

    /// <summary>Fila de mensagens mortas.</summary>
    public const string DlqQueue = "cadastro.user-import-lines.dlq";

    private const string Image = "rabbitmq:4.3-management@sha256:fd4626e7c8ff066fc2ad8a608fd0364419ca919de00ddaca4bf1159f8350c1c8";
    private const string User = "cadastro-test";
    private const string AppUser = "cadastro-app";
    private const string VirtualHost = "dev";
    private readonly string _password = $"Rb{Guid.NewGuid():N}";
    private readonly string _appPassword = $"Ap{Guid.NewGuid():N}";
    private readonly int _port = FreePort();
    private readonly RabbitMqContainer _container;

    /// <summary>Cria o fixture com a porta AMQP fixa.</summary>
    public RabbitMqFixture()
    {
        _container = new RabbitMqBuilder(Image)
            .WithUsername(User)
            .WithPassword(_password)
            .WithPortBinding(_port, 5672)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete"))
            .Build();
    }

    /// <summary>Opções de conexão do relay apontando para este broker.</summary>
    public Cadastro.Infrastructure.Messaging.RabbitMqOptions Options() => new()
    {
        Host = "127.0.0.1",
        Port = _port,
        VirtualHost = VirtualHost,
        UserName = AppUser,
        Password = _appPassword,
        BackoffBaseMs = 1000,
        BackoffMaxMs = 5000,
        ConfirmTimeoutSeconds = 5,
    };

    /// <summary>Sobe o broker e a topologia.</summary>
    public async ValueTask InitializeAsync()
    {
        using (await ContainerStartLock.AcquireAsync(TestContext.Current.CancellationToken))
        {
            await _container.StartAsync(TestContext.Current.CancellationToken);
        }

        await CreateVirtualHostAndUsersAsync();
        await DeclareTopologyAsync();
    }

    /// <summary>Abre uma conexão com o usuário restrito <c>cadastro-app</c> (mesmas permissões da produção).</summary>
    public Task<IConnection> ConnectAsRestrictedAppAsync() =>
        new ConnectionFactory { HostName = "127.0.0.1", Port = _port, UserName = AppUser, Password = _appPassword, VirtualHost = VirtualHost }
            .CreateConnectionAsync(TestContext.Current.CancellationToken);

    /// <summary>Quantas conexões abertas no broker têm este nome (<c>ClientProvidedName</c>).</summary>
    public async Task<int> CountConnectionsAsync(string clientProvidedName)
    {
        var result = await _container.ExecAsync(["rabbitmqctl", "list_connections", "client_properties", "--silent"], TestContext.Current.CancellationToken);
        return result.Stdout.Split('\n').Count(line => line.Contains(clientProvidedName, StringComparison.Ordinal));
    }

    /// <summary>Para o broker (simula a queda).</summary>
    public Task StopAsync() => _container.StopAsync(TestContext.Current.CancellationToken);

    /// <summary>Sobe o broker de novo na mesma porta e reaplica a topologia.</summary>
    public async Task StartAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        await DeclareTopologyAsync();
    }

    /// <summary>Esvazia a fila principal, a de retry e a DLQ entre os testes.</summary>
    public async Task PurgeAsync()
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        foreach (var queue in new[] { Queue, RetryQueue, DlqQueue })
        {
            await channel.QueuePurgeAsync(queue, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Publica na exchange de eventos como o relay faria (usuário administrador do teste).</summary>
    public async Task PublishLineAcceptedAsync(string body, Guid messageId)
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), TestContext.Current.CancellationToken);
        await channel.BasicPublishAsync(
            Exchange, RoutingKey, mandatory: true,
            new BasicProperties { MessageId = messageId.ToString("D"), Type = RoutingKey, DeliveryMode = DeliveryModes.Persistent },
            System.Text.Encoding.UTF8.GetBytes(body), TestContext.Current.CancellationToken);
    }

    /// <summary>Quantas mensagens há na fila (contagem do broker).</summary>
    public async Task<uint> MessageCountAsync(string queue)
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        return (await channel.QueueDeclarePassiveAsync(queue, TestContext.Current.CancellationToken)).MessageCount;
    }

    /// <summary>Lê todas as mensagens da fila (ack) e devolve id, propriedades e corpo.</summary>
    public async Task<IReadOnlyList<ReceivedMessage>> DrainAsync()
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        var received = new List<ReceivedMessage>();
        while (await channel.BasicGetAsync(Queue, autoAck: true, TestContext.Current.CancellationToken) is { } message)
        {
            var traceParent = message.BasicProperties.Headers is { } headers && headers.TryGetValue("traceparent", out var value)
                ? System.Text.Encoding.UTF8.GetString((byte[])value!)
                : null;
            received.Add(new ReceivedMessage(
                message.BasicProperties.MessageId,
                message.BasicProperties.DeliveryMode == DeliveryModes.Persistent,
                traceParent,
                message.RoutingKey,
                System.Text.Encoding.UTF8.GetString(message.Body.Span)));
        }

        return received;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    // O usuário da aplicação espelha o definitions do ../rabbitmq: configure nenhum, write só em cadastro.events|cadastro.retry,
    // read só na fila principal. O usuário do fixture (administrador do teste) cria a topologia, como o provisionador faz em produção.
    private async Task CreateVirtualHostAndUsersAsync()
    {
        await RunCtlAsync("add_vhost", VirtualHost);
        await RunCtlAsync("set_permissions", "-p", VirtualHost, User, ".*", ".*", ".*");
        await RunCtlAsync("add_user", AppUser, _appPassword);
        await RunCtlAsync(
            "set_permissions", "-p", VirtualHost, AppUser, string.Empty, "^cadastro\\.(events|retry)$", "^cadastro\\.user-import-lines$");
    }

    private async Task RunCtlAsync(params string[] arguments)
    {
        var result = await _container.ExecAsync(["rabbitmqctl", .. arguments], TestContext.Current.CancellationToken);
        result.ExitCode.ShouldBe(0, result.Stderr);
    }

    private async Task DeclareTopologyAsync()
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, cancellationToken: TestContext.Current.CancellationToken);
        await channel.ExchangeDeclareAsync(RetryExchange, ExchangeType.Topic, durable: true, cancellationToken: TestContext.Current.CancellationToken);
        await channel.ExchangeDeclareAsync(DlxExchange, ExchangeType.Topic, durable: true, cancellationToken: TestContext.Current.CancellationToken);
        var quorum = new Dictionary<string, object?> { ["x-queue-type"] = "quorum" };
        // Espelha o definitions do ../rabbitmq: DLX at-least-once com reject-publish e delivery-limit 5 na fila principal.
        var main = new Dictionary<string, object?>(quorum)
        {
            ["x-dead-letter-exchange"] = DlxExchange,
            ["x-dead-letter-strategy"] = "at-least-once",
            ["x-overflow"] = "reject-publish",
            ["x-delivery-limit"] = 5,
        };
        var retry = new Dictionary<string, object?>(quorum) { ["x-message-ttl"] = 1000, ["x-dead-letter-exchange"] = Exchange };
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false, main, cancellationToken: TestContext.Current.CancellationToken);
        await channel.QueueDeclareAsync(RetryQueue, durable: true, exclusive: false, autoDelete: false, retry, cancellationToken: TestContext.Current.CancellationToken);
        await channel.QueueDeclareAsync(DlqQueue, durable: true, exclusive: false, autoDelete: false, quorum, cancellationToken: TestContext.Current.CancellationToken);
        await channel.QueueBindAsync(Queue, Exchange, RoutingKey, cancellationToken: TestContext.Current.CancellationToken);
        await channel.QueueBindAsync(RetryQueue, RetryExchange, RoutingKey, cancellationToken: TestContext.Current.CancellationToken);
        await channel.QueueBindAsync(DlqQueue, DlxExchange, "#", cancellationToken: TestContext.Current.CancellationToken);
    }

    private Task<IConnection> ConnectAsync() =>
        new ConnectionFactory { HostName = "127.0.0.1", Port = _port, UserName = User, Password = _password, VirtualHost = VirtualHost }
            .CreateConnectionAsync(TestContext.Current.CancellationToken);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>Mensagem lida da fila de teste.</summary>
/// <param name="MessageId">Propriedade <c>message-id</c> AMQP.</param>
/// <param name="Persistent">Entrega persistente.</param>
/// <param name="TraceParent">Header <c>traceparent</c>.</param>
/// <param name="RoutingKey">Routing key de entrega.</param>
/// <param name="Body">Corpo JSON.</param>
public sealed record ReceivedMessage(string? MessageId, bool Persistent, string? TraceParent, string RoutingKey, string Body);

