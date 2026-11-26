using System.Net;
using System.Net.Sockets;
using System.Text;
using Cadastro.Application.UserImports;
using Cadastro.Infrastructure.Antivirus;
using Cadastro.Tests.Shared;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Tests.Antivirus;

/// <summary>
/// Cliente do clamd: protocolo e fail-closed contra um clamd falso (rápido) e detecção real do arquivo de teste EICAR
/// contra o container oficial do ClamAV (mesma imagem por tag + digest do docker-compose).
/// </summary>
public sealed class ClamdVirusScannerTests : IAsyncLifetime
{
    private const string Image = "clamav/clamav:1.4.3@sha256:75fb5fd95fcbe1d7e6d240c369c1572b686ee2c95949d1042b5148de8eddebb4";
    private const ushort ClamdPort = 3310;
    private IContainer? _container;

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static ClamdVirusScanner Scanner(string host, int port, int timeoutSeconds = 5) =>
        new(Options.Create(new ClamAvOptions { Host = host, Port = port, TimeoutSeconds = timeoutSeconds }));

    private static MemoryStream Text(string value) => new(Encoding.ASCII.GetBytes(value));

    // Montado em partes para o arquivo de código-fonte não ser detectado por antivírus do desenvolvedor.
    private static string Eicar() => "X5O!P%@AP[4\\PZX54(P^)7CC)7}$" + "EICAR-STANDARD-ANTIVIRUS-TEST-FILE!" + "$H+H*";

    [Theory]
    [InlineData("stream: OK", VirusScanResult.Clean)]
    [InlineData("stream: Eicar-Signature FOUND", VirusScanResult.Infected)]
    public async Task Scan_ParsesTheCleanAndInfectedResponses(string response, VirusScanResult expected)
    {
        await using var server = FakeClamd.Start(response);

        var result = await Scanner("127.0.0.1", server.Port).ScanAsync(Text("hello"), TestContext.Current.CancellationToken);

        result.ShouldBe(expected);
        server.Received.ShouldStartWith("zINSTREAM\0");
    }

    [Fact]
    public async Task Scan_SendsTheContentInSizedChunksEndedByZero()
    {
        await using var server = FakeClamd.Start("stream: OK");
        var big = new string('x', 150_000);

        await Scanner("127.0.0.1", server.Port).ScanAsync(Text(big), TestContext.Current.CancellationToken);

        // 3 blocos (65536 + 65536 + 18928), cada um com 4 bytes de tamanho, mais o terminador de 4 zeros.
        server.ReceivedBytes.ShouldBe("zINSTREAM\0".Length + 150_000 + 3 * 4 + 4);
    }

    [Theory]
    [InlineData("stream: INSTREAM size limit exceeded. ERROR")]
    [InlineData("")]
    [InlineData("something unexpected")]
    public async Task Scan_ErrorOrUnexpectedResponse_FailsClosed(string response)
    {
        await using var server = FakeClamd.Start(response);

        await Should.ThrowAsync<VirusScannerUnavailableException>(
            () => Scanner("127.0.0.1", server.Port).ScanAsync(Text("hello"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Scan_NobodyListening_FailsClosed()
    {
        using var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        await Should.ThrowAsync<VirusScannerUnavailableException>(
            () => Scanner("127.0.0.1", port).ScanAsync(Text("hello"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Scan_ServerThatNeverAnswers_TimesOutAndFailsClosed()
    {
        await using var server = FakeClamd.Start(response: null);

        await Should.ThrowAsync<VirusScannerUnavailableException>(
            () => Scanner("127.0.0.1", server.Port, timeoutSeconds: 1).ScanAsync(Text("hello"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Scan_WithTheRealClamd_DetectsEicarAndAcceptsACleanCsv()
    {
        _container = new ContainerBuilder(Image)
            .WithPortBinding(ClamdPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("socket found, clamd started"))
            .Build();
        using (await ContainerStartLock.AcquireAsync(TestContext.Current.CancellationToken))
        {
            await _container.StartAsync(TestContext.Current.CancellationToken);
        }

        var scanner = Scanner(_container.Hostname, _container.GetMappedPublicPort(ClamdPort), timeoutSeconds: 60);

        (await scanner.ScanAsync(Text(Eicar()), TestContext.Current.CancellationToken)).ShouldBe(VirusScanResult.Infected);
        (await scanner.ScanAsync(Text("nome,email,cpf,telefone\nAna,ana@example.com,52998224725,\n"), TestContext.Current.CancellationToken))
            .ShouldBe(VirusScanResult.Clean);
    }

    /// <summary>clamd falso: aceita uma conexão, lê tudo até o terminador de tamanho zero e responde (ou nunca responde).</summary>
    private sealed class FakeClamd : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serving;
        private readonly CancellationTokenSource _stop = new();
        private readonly MemoryStream _received = new();

        private FakeClamd(string? response)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serving = Task.Run(() => ServeAsync(response));
        }

        public int Port { get; }

        public string Received => Encoding.ASCII.GetString(_received.ToArray());

        public int ReceivedBytes => (int)_received.Length;

        public static FakeClamd Start(string? response) => new(response);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _serving;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
            {
                // Encerramento do servidor falso.
            }

            _stop.Dispose();
        }

        private async Task ServeAsync(string? response)
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using var stream = client.GetStream();
            var buffer = new byte[8192];
            // Termina quando chega o terminador (4 bytes zero) no fim do que foi recebido.
            while (!EndsWithTerminator(_received))
            {
                var read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    return;
                }

                _received.Write(buffer, 0, read);
            }

            if (response is null)
            {
                await Task.Delay(Timeout.Infinite, _stop.Token);
                return;
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\0"), _stop.Token);
        }

        private static bool EndsWithTerminator(MemoryStream received)
        {
            var bytes = received.GetBuffer();
            var length = (int)received.Length;
            return length >= 4 && bytes[length - 1] == 0 && bytes[length - 2] == 0 && bytes[length - 3] == 0 && bytes[length - 4] == 0;
        }
    }
}
