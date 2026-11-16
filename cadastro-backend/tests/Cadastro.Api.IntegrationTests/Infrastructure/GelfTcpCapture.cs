using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Cadastro.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Servidor GELF TCP falso em <c>127.0.0.1</c>: recebe exatamente o que o sink do Graylog enviaria (JSON terminado
/// em <c>\0</c>) para os testes inspecionarem cada campo GELF, sem depender do Graylog real.
/// </summary>
public sealed class GelfTcpCapture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly Task _acceptLoop;

    /// <summary>Abre a porta (escolhida pelo sistema) e passa a aceitar conexões.</summary>
    public GelfTcpCapture()
    {
        _listener.Start();
        _acceptLoop = AcceptAsync(_stop.Token);
    }

    /// <summary>Porta em que o servidor escuta.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Espera a primeira mensagem GELF cujo JSON contém o marcador e devolve o texto bruto.</summary>
    public async Task<string> WaitForAsync(string marker, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        do
        {
            if (_messages.FirstOrDefault(m => m.Contains(marker, StringComparison.Ordinal)) is { } found)
            {
                return found;
            }
        }
        while (await timer.WaitForNextTickAsync(timeoutSource.Token));

        throw new TimeoutException($"No GELF message with marker {marker}.");
    }

    /// <summary>Campos de uma mensagem GELF (nome → texto).</summary>
    public static IReadOnlyDictionary<string, string> Fields(string gelf)
    {
        using var document = JsonDocument.Parse(gelf);
        return document.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.GetRawText());
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = ReadAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Encerrado pelo DisposeAsync.
        }
    }

    private async Task ReadAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            var pending = new List<byte>();
            var buffer = new byte[8192];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    foreach (var b in buffer.AsSpan(0, read))
                    {
                        if (b == 0)
                        {
                            _messages.Enqueue(Encoding.UTF8.GetString([.. pending]));
                            pending.Clear();
                        }
                        else
                        {
                            pending.Add(b);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // Conexão fechada pelo sink ou pelo fim do teste.
            }
        }
    }

    /// <summary>Para de aceitar conexões e libera a porta.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _acceptLoop;
        _stop.Dispose();
    }
}
