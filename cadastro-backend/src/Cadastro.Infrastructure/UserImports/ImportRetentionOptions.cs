using System.ComponentModel.DataAnnotations;

namespace Cadastro.Infrastructure.UserImports;

/// <summary>Opções do job de limpeza por retenção da importação (<c>ImportRetention:*</c>).</summary>
public sealed class ImportRetentionOptions
{
    /// <summary>Nome da seção de configuração.</summary>
    public const string SectionName = "ImportRetention";

    /// <summary>Liga o job; desligado, nada vence (só para diagnóstico: em produção deve ficar ligado).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Intervalo entre ciclos, em minutos.</summary>
    [Range(1, 10080)]
    public int IntervalMinutes { get; set; } = 360;

    /// <summary>Atraso do primeiro ciclo depois da subida, em segundos (a API sobe e atende antes de limpar).</summary>
    [Range(0, 3600)]
    public int InitialDelaySeconds { get; set; } = 60;

    /// <summary>Itens removidos por comando (limita o tamanho das transações).</summary>
    [Range(1, 5000)]
    public int BatchSize { get; set; } = 500;
}
