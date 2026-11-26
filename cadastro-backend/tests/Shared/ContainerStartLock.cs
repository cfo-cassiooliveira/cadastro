namespace Cadastro.Tests.Shared;

/// <summary>
/// Trava entre processos para subir um container de teste por vez. Os projetos de teste rodam em processos separados
/// (<c>dotnet test --solution</c>) e subir SQL Server e Keycloak juntos disputa CPU/memória do Docker e gera timeouts
/// intermitentes. A trava vale só durante o start (até o container ficar pronto).
/// </summary>
internal static class ContainerStartLock
{
    private static readonly string LockPath = Path.Combine(Path.GetTempPath(), "cadastro-tests-container-start.lock");
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

    /// <summary>Espera a vez e devolve um handle; descartá-lo libera a trava para o próximo processo.</summary>
    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + MaxWait;
        while (true)
        {
            try
            {
                // DeleteOnClose: a trava some junto com o handle, mesmo se o processo morrer.
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
    }
}
