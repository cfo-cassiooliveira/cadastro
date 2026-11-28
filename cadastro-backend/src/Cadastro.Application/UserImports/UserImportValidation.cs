namespace Cadastro.Application.UserImports;

/// <summary>
/// Acumula o resultado da validação linha a linha: linhas válidas, inválidas e duplicadas (e-mail ou CPF repetido no
/// arquivo). Só linhas bem formadas reservam e-mail e CPF; a primeira ocorrência vale e as seguintes são duplicadas.
/// A comparação é em memória, enquanto os valores ainda estão em claro, e vale só dentro do arquivo; quem já tem conta é
/// descoberto no processamento.
/// </summary>
internal sealed class UserImportValidation
{
    private readonly HashSet<string> _emails = [];
    private readonly HashSet<string> _cpfs = [];
    private readonly List<ValidatedUser> _users = [];
    private readonly List<ImportRowIssue> _issues = [];

    /// <summary>Linhas válidas e únicas.</summary>
    public IReadOnlyList<ValidatedUser> Users => _users;

    /// <summary>Linhas rejeitadas por erro de formato.</summary>
    public int InvalidLines { get; private set; }

    /// <summary>Linhas rejeitadas por repetir e-mail ou CPF de linha anterior.</summary>
    public int DuplicateLines { get; private set; }

    /// <summary>Primeiros problemas (limitados) para o resumo.</summary>
    public IReadOnlyList<ImportRowIssue> Issues => _issues;

    /// <summary>Indica que existem mais problemas do que os listados.</summary>
    public bool IssuesTruncated { get; private set; }

    /// <summary>Valida e contabiliza uma linha.</summary>
    public void Add(ExtractedRow row)
    {
        var codes = UserRowValidator.Validate(row, out var user);
        if (user is null)
        {
            InvalidLines++;
            foreach (var code in codes)
            {
                Report(row.LineNumber, code);
            }

            return;
        }

        var emailKey = UserRowValidator.EmailKey(user.Email);
        var emailTaken = _emails.Contains(emailKey);
        var cpfTaken = _cpfs.Contains(user.Cpf);
        if (emailTaken || cpfTaken)
        {
            DuplicateLines++;
            if (emailTaken)
            {
                Report(row.LineNumber, "row.duplicate_email");
            }

            if (cpfTaken)
            {
                Report(row.LineNumber, "row.duplicate_cpf");
            }

            return;
        }

        // Só linha aceita reserva e-mail e CPF; uma duplicada não bloqueia valores de outras linhas.
        _emails.Add(emailKey);
        _cpfs.Add(user.Cpf);
        _users.Add(user);
    }

    private void Report(int line, string code)
    {
        if (_issues.Count >= ImportLimits.MaxReportedIssues)
        {
            IssuesTruncated = true;
            return;
        }

        _issues.Add(new ImportRowIssue(line, code));
    }
}
