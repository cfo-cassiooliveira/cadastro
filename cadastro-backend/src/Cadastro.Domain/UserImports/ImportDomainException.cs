namespace Cadastro.Domain.UserImports;

/// <summary>Violação de invariante ou transição inválida no domínio de importação.</summary>
public sealed class ImportDomainException(string message) : InvalidOperationException(message);
