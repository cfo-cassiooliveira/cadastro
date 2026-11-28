using System.Buffers.Binary;
using System.ComponentModel.DataAnnotations;
using System.Net.Sockets;
using System.Text;
using Cadastro.Application.UserImports;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Antivirus;

/// <summary>Opções do ClamAV (<c>ClamAv:*</c>); o container <c>clamd</c> não publica porta, só a rede interna o alcança.</summary>
public sealed class ClamAvOptions
{
    /// <summary>Nome da seção de configuração.</summary>
    public const string SectionName = "ClamAv";

    /// <summary>Host do clamd (nome do serviço no compose).</summary>
    [Required]
    public string Host { get; set; } = "clamav";

    /// <summary>Porta TCP do clamd.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 3310;

    /// <summary>Tempo máximo, em segundos, para verificar um arquivo (conexão, envio e resposta).</summary>
    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Cliente do clamd pelo comando <c>zINSTREAM</c>: envia o arquivo em blocos (4 bytes de tamanho em big-endian + dados,
/// terminando com tamanho zero) e lê a resposta. Qualquer falha (conexão, timeout, resposta inesperada) lança
/// <see cref="VirusScannerUnavailableException"/>: o upload é recusado, nunca aceito sem verificar (fail-closed).
/// O custo do fail-closed é que não há envio de arquivo enquanto o antivírus estiver fora ou carregando as assinaturas.
/// </summary>
internal sealed class ClamdVirusScanner(IOptions<ClamAvOptions> options) : IVirusScanner
{
    private const int ChunkSize = 64 * 1024;
    private const int MaxResponseBytes = 1024;

    /// <inheritdoc />
    public async Task<VirusScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(settings.Host, settings.Port, timeout.Token);
            await using var network = client.GetStream();
            await network.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);
            await SendContentAsync(content, network, timeout.Token);
            return ParseResponse(await ReadResponseAsync(network, timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VirusScannerUnavailableException("The virus scanner timed out.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw new VirusScannerUnavailableException("The virus scanner connection failed.", ex);
        }
    }

    private static async Task SendContentAsync(Stream content, Stream network, CancellationToken cancellationToken)
    {
        var buffer = new byte[ChunkSize];
        var size = new byte[sizeof(int)];
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            BinaryPrimitives.WriteInt32BigEndian(size, read);
            await network.WriteAsync(size, cancellationToken);
            await network.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        // Tamanho zero encerra o stream.
        BinaryPrimitives.WriteInt32BigEndian(size, 0);
        await network.WriteAsync(size, cancellationToken);
        await network.FlushAsync(cancellationToken);
    }

    // O clamd responde uma linha terminada em NUL (z) e fecha; lê até o NUL ou o fim, com teto de tamanho.
    private static async Task<string> ReadResponseAsync(Stream network, CancellationToken cancellationToken)
    {
        var response = new List<byte>();
        var one = new byte[1];
        while (response.Count < MaxResponseBytes && await network.ReadAsync(one, cancellationToken) == 1 && one[0] != 0)
        {
            response.Add(one[0]);
        }

        return Encoding.ASCII.GetString([.. response]).Trim();
    }

    private static VirusScanResult ParseResponse(string response)
    {
        if (response.EndsWith(" OK", StringComparison.Ordinal))
        {
            return VirusScanResult.Clean;
        }

        if (response.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            return VirusScanResult.Infected;
        }

        // ERROR (ex.: limite de tamanho do clamd) ou resposta vazia: não dá para afirmar que está limpo.
        throw new VirusScannerUnavailableException("The virus scanner returned an unexpected response.");
    }
}
