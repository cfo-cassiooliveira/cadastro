namespace Cadastro.Infrastructure.Keycloak;

/// <summary>
/// Limita as criações por segundo espaçando as permissões (intervalo mínimo entre duas): com 10 por segundo, uma a cada 100 ms.
/// Todas as instâncias de consumo do processo compartilham o mesmo limitador.
/// </summary>
internal sealed class CreationRateLimiter(int permitsPerSecond, TimeProvider timeProvider)
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(1d / permitsPerSecond);
    private readonly Lock _gate = new();
    private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;

    /// <summary>Espera a vez de criar um usuário.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay;
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            var slot = _nextSlot > now ? _nextSlot : now;
            _nextSlot = slot + _interval;
            delay = slot - now;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }
}
