namespace Cadastro.Domain.UserImports;

/// <summary>Prazos de retenção aprovados na decisão Q9 da spec 002 do rabbitmq (LGPD, minimização).</summary>
public static class ImportRetention
{
    /// <summary>Meses de retenção da trilha de auditoria, contados do evento.</summary>
    public const int AuditMonths = 12;

    /// <summary>Relatório por linha: 90 dias a partir do fim do lote.</summary>
    public static readonly TimeSpan Report = TimeSpan.FromDays(90);

    /// <summary>Lote que fica em aguardando confirmação além deste prazo é cancelado e os dados das linhas são apagados (minimização, LGPD).</summary>
    public static readonly TimeSpan AbandonedBatch = TimeSpan.FromDays(7);
}
