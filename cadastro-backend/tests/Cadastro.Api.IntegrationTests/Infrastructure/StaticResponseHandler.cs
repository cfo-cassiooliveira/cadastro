using System.Net;
using System.Text;

namespace Cadastro.Api.IntegrationTests.Infrastructure;

/// <summary>HttpMessageHandler que devolve sempre a mesma resposta (substitui serviços externos nos testes).</summary>
public sealed class StaticResponseHandler(HttpStatusCode status, string body = "{}") : HttpMessageHandler
{
    /// <summary>Requisições recebidas, para os testes conferirem caminho e cabeçalhos.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

/// <summary>HttpMessageHandler que simula o serviço fora do ar (conexão recusada).</summary>
public sealed class UnreachableHandler : HttpMessageHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("Connection refused (simulated).");
}
