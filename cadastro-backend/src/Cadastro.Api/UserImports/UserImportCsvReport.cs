using System.Text;
using Cadastro.Application.UserImports;

namespace Cadastro.Api.UserImports;

/// <summary>
/// Escreve o relatório por linha em CSV. Só números, estados e códigos entram, mas o escritor protege qualquer campo contra
/// injeção de fórmula (planilha): valor iniciado por <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab ou CR ganha um apóstrofo na frente.
/// </summary>
internal static class UserImportCsvReport
{
    private const string Header = "line,status,failureReason,legacyMarkStatus,reprocessCount";

    /// <summary>Monta o CSV (UTF-8, quebra de linha LF) com cabeçalho e uma linha por item.</summary>
    public static string Write(IReadOnlyList<UserImportLineDto> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var csv = new StringBuilder(Header).Append('\n');
        foreach (var line in lines)
        {
            csv.Append(line.LineNumber).Append(',')
                .Append(Field(line.Status)).Append(',')
                .Append(Field(line.FailureReason)).Append(',')
                .Append(Field(line.LegacyMarkStatus)).Append(',')
                .Append(line.ReprocessCount).Append('\n');
        }

        return csv.ToString();
    }

    /// <summary>Neutraliza fórmula e aplica aspas quando o campo tem vírgula, aspas ou quebra de linha.</summary>
    internal static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }
}
