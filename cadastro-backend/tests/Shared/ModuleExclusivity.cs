[assembly: Xunit.AssemblyFixture(typeof(Cadastro.Tests.Shared.ModuleExclusivity))]

namespace Cadastro.Tests.Shared;

/// <summary>
/// Faz os módulos de teste que sobem containers rodarem um de cada vez, mesmo com <c>dotnet test --solution</c> executando os
/// módulos em paralelo: SQL Server, Keycloak, RabbitMQ e ClamAV juntos estouram a memória do Docker (~9,7 GiB) e o SQL Server cai
/// ao subir. A trava é um arquivo exclusivo mantido durante todo o módulo (liberado ao fechar, inclusive se o processo morrer),
/// então o comando padrão já fica seguro, sem depender do flag <c>--max-parallel-test-modules</c>.
/// </summary>
internal sealed class ModuleExclusivity : IAsyncLifetime
{
    private static readonly string LockPath = Path.Combine(Path.GetTempPath(), "cadastro-tests-module.lock");
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(40);
    private FileStream? _lock;

    /// <summary>Espera a vez do módulo e segura a trava até o fim.</summary>
    public async ValueTask InitializeAsync()
    {
        var deadline = DateTime.UtcNow + MaxWait;
        while (true)
        {
            try
            {
                _lock = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    /// <summary>Libera a trava para o próximo módulo.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_lock is not null)
        {
            await _lock.DisposeAsync();
        }
    }
}
