using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cadastro.Api.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cadastro.Api.IntegrationTests.Logging;

/// <summary>
/// Envio real em GELF TCP pelo <see cref="LoggingSetup.AddCadastroLogging"/> (C01 da spec 001 do graylog): um listener
/// TCP local faz o papel do input GELF do Graylog e recebe o JSON que o sink escreveu, terminado em byte nulo.
/// </summary>
public sealed partial class GelfTransportTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task AddCadastroLogging_WithGraylogHost_SendsGelfOverTcpWithServiceTraceAndCorrelation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var correlationId = "gelf-" + Guid.NewGuid().ToString("N");
        await using var provider = BuildProvider(port);
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Cadastro.Api.GelfProbe");
        var receiving = ReceiveGelfMessageAsync(listener, TestContext.Current.CancellationToken);
        string expectedTraceId;
        using (var activity = new Activity("gelf-probe").Start())
        {
            expectedTraceId = activity.TraceId.ToHexString();
            using (logger.BeginScope(new Dictionary<string, object> { ["correlation_id"] = correlationId }))
            {
                LogProbe(logger, "gelf-transport");
            }
        }

        // O sink envia em segundo plano: a leitura acontece com o host ainda vivo, como em produção.
        var gelf = await receiving;

        gelf.GetProperty("_service_name").GetString().ShouldBe(LoggingSetup.ServiceName);
        gelf.GetProperty("_trace_id").GetString().ShouldBe(expectedTraceId);
        gelf.GetProperty("_correlation_id").GetString().ShouldBe(correlationId);
        gelf.GetProperty("_ProbeName").GetString().ShouldBe("gelf-transport");
        gelf.GetProperty("_message_template").GetString().ShouldBe("Probe event {ProbeName}");
        gelf.GetProperty("short_message").GetString().ShouldNotBeNull().ShouldContain("gelf-transport");
    }

    // Graylog fora do ar não derruba a aplicação: o log continua no console e a escrita não lança exceção.
    [Fact]
    public async Task AddCadastroLogging_WhenGraylogIsUnreachable_LoggingDoesNotThrow()
    {
        int closedPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        await using var provider = BuildProvider(closedPort);
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Cadastro.Api.GelfProbe");

        Should.NotThrow(() => LogGraylogDown(logger));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Probe event {ProbeName}")]
    private static partial void LogProbe(ILogger logger, string probeName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Probe event while Graylog is down")]
    private static partial void LogGraylogDown(ILogger logger);

    private static ServiceProvider BuildProvider(int port)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graylog:Host"] = "127.0.0.1",
                ["Graylog:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Testing");

        var services = new ServiceCollection();
        services.AddCadastroLogging(configuration, environment);
        return services.BuildServiceProvider();
    }

    // Lê até o byte nulo que delimita a mensagem GELF no TCP; conexão fechada sem dados é ignorada.
    private static async Task<JsonElement> ReceiveGelfMessageAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReceiveTimeout);
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var bytes = await ReadUntilNullAsync(client.GetStream(), timeout.Token);
            if (bytes.Count > 0)
            {
                using var document = JsonDocument.Parse(Encoding.UTF8.GetString([.. bytes]));
                return document.RootElement.Clone();
            }
        }
    }

    private static async Task<List<byte>> ReadUntilNullAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var end = Array.IndexOf(buffer, (byte)0, 0, read);
            bytes.AddRange(buffer.Take(end >= 0 ? end : read));
            if (end >= 0)
            {
                break;
            }
        }

        return bytes;
    }
}
