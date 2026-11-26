namespace Cadastro.Infrastructure.Legado;

/// <summary>
/// Assina cada tentativa de chamada ao Legado com um horário e um nonce novos. Fica DENTRO do pipeline de resiliência: o
/// retry reenvia a requisição por aqui, e o Legado recusa nonce repetido (anti-reenvio).
/// </summary>
internal sealed class LegadoSigningHandler(byte[] key, string keyId, TimeProvider timeProvider) : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var header = LegadoSignature.Header(
            keyId, key, timeProvider.GetUtcNow().ToUnixTimeSeconds(), LegadoSignature.NewNonce(),
            request.Method.Method, request.RequestUri!.PathAndQuery, body);
        request.Headers.Remove(LegadoSignature.HeaderName);
        request.Headers.TryAddWithoutValidation(LegadoSignature.HeaderName, header);
        return await base.SendAsync(request, cancellationToken);
    }
}
