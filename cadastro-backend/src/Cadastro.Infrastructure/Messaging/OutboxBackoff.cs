namespace Cadastro.Infrastructure.Messaging;

/// <summary>Backoff exponencial com jitter para as republicações da outbox (Art. VII.1).</summary>
internal static class OutboxBackoff
{
    /// <summary>
    /// Atraso após a tentativa de número <paramref name="attempts"/> (1 = primeira falha): <c>base * 2^(n-1)</c> limitado a
    /// <paramref name="max"/>, multiplicado por um fator entre 0,5 e 1,5 (jitter) para as instâncias não sincronizarem.
    /// </summary>
    /// <param name="attempts">Tentativas já feitas, incluindo a que acabou de falhar.</param>
    /// <param name="baseDelay">Atraso da primeira falha.</param>
    /// <param name="max">Teto do atraso (antes do jitter).</param>
    /// <param name="randomUnit">Valor aleatório em [0, 1), injetado para teste.</param>
    public static TimeSpan Next(int attempts, TimeSpan baseDelay, TimeSpan max, double randomUnit)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 30);
        var delayMs = Math.Min(baseDelay.TotalMilliseconds * Math.Pow(2, exponent), max.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(delayMs * (0.5 + randomUnit));
    }
}
