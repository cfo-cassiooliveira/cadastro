using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Cadastro.Infrastructure.Legado;

/// <summary>Valida o certificado do Legado contra a CA do Legado como única âncora (nunca o repositório do sistema).</summary>
internal static class LegadoTlsTrust
{
    /// <summary>
    /// Aceita o certificado só se a cadeia termina na CA informada e o nome confere. Qualquer outro erro (nome, certificado
    /// ausente, cadeia fora da CA) recusa.
    /// </summary>
    public static bool IsTrusted(X509Certificate2? certificate, SslPolicyErrors errors, X509Certificate2 authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        // CA local sem CRL/OCSP: a revogação é por troca do certificado (validade de 1 ano).
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }
}
