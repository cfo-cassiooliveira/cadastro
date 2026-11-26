namespace Cadastro.Domain.UserImports;

/// <summary>Estado do ciclo de vida de um lote de importação.</summary>
public enum ImportBatchStatus
{
    /// <summary>Arquivo validado; aguarda a confirmação do administrador (nada foi publicado).</summary>
    AwaitingConfirmation = 0,

    /// <summary>Confirmado; as linhas estão sendo processadas.</summary>
    Processing = 1,

    /// <summary>Todas as linhas terminaram sem erro.</summary>
    Completed = 2,

    /// <summary>Todas as linhas terminaram e ao menos uma ficou com erro.</summary>
    CompletedWithErrors = 3,

    /// <summary>Descartado antes da confirmação.</summary>
    Cancelled = 4,
}
