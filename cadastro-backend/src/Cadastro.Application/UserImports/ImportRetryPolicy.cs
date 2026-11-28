namespace Cadastro.Application.UserImports;

/// <summary>
/// Política dos ciclos de retentativa por fila com TTL (30 s por ciclo, até 5 ciclos; ADR-0005 do rabbitmq). Esperar na fila,
/// e não no consumidor, libera o prefetch para outras linhas e sobrevive a reinício. O intervalo é fixo, sem backoff, e uma
/// falha que dure mais que os ciclos encerra a linha com erro, para reprocessamento manual.
/// </summary>
public static class ImportRetryPolicy
{
    /// <summary>Máximo de ciclos de retentativa depois da primeira entrega.</summary>
    public const int MaxCycles = 5;

    /// <summary>Indica se a entrega número <paramref name="attempt"/> (ciclos já feitos) esgotou as retentativas.</summary>
    public static bool IsExhausted(int attempt) => attempt >= MaxCycles;

    /// <summary>Contador do próximo ciclo.</summary>
    public static int NextAttempt(int attempt) => attempt + 1;
}
