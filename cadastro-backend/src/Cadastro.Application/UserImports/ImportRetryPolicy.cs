namespace Cadastro.Application.UserImports;

/// <summary>Política dos ciclos de retentativa por fila com TTL (30 s por ciclo, até 5 ciclos; ADR-0005 do rabbitmq).</summary>
public static class ImportRetryPolicy
{
    /// <summary>Máximo de ciclos de retentativa depois da primeira entrega.</summary>
    public const int MaxCycles = 5;

    /// <summary>Indica se a entrega número <paramref name="attempt"/> (ciclos já feitos) esgotou as retentativas.</summary>
    public static bool IsExhausted(int attempt) => attempt >= MaxCycles;

    /// <summary>Contador do próximo ciclo.</summary>
    public static int NextAttempt(int attempt) => attempt + 1;
}
