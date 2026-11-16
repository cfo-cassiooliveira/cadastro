using System.Text.RegularExpressions;
using Cadastro.Application.LogSearch;

namespace Cadastro.Api.ClientLogs;

/// <summary>
/// Mascara dado pessoal reconhecido pelo formato em texto livre que não passou por campo estruturado (ex.: o que o
/// navegador envia em <c>POST /api/v1/client-logs</c>). Mesmos padrões e mesma ordem do estágio 4 do pipeline do
/// Graylog (<c>lgpd-text.rule.tmpl</c>, ADR-0002): e-mail, CNPJ formatado, CPF e celular com DDD. A máscara vem da
/// porta <see cref="ILogPseudonymizer"/> (mesma regra dos campos estruturados); texto livre nunca vira token
/// (ADR-0003 do graylog, item 8).
/// </summary>
internal static partial class PersonalDataTextMasker
{
    /// <summary>Valor gravado quando o trecho reconhecido está fora do formato do tipo: nunca fica em claro.</summary>
    public const string Redacted = "***";

    // Limite de tempo por expressão: a entrada vem de fora e não pode travar a requisição.
    private const int MatchTimeoutMilliseconds = 100;
    private const string EncodedAt = "%40";

    /// <summary>
    /// Troca cada e-mail, CNPJ formatado, CPF e celular com DDD do texto pela máscara do ADR-0002. 11 dígitos sem
    /// pontuação são ambíguos (CPF ou celular) e viram CPF, a máscara que esconde mais.
    /// </summary>
    /// <exception cref="RegexMatchTimeoutException">Quando uma expressão estoura o limite de tempo.</exception>
    public static string Mask(ILogPseudonymizer pseudonymizer, string text)
    {
        ArgumentNullException.ThrowIfNull(pseudonymizer);
        ArgumentNullException.ThrowIfNull(text);
        var withEmail = EmailPattern().Replace(text, m => MaskMatch(pseudonymizer, PersonalDataKind.Login, m.Value.Replace(EncodedAt, "@", StringComparison.Ordinal)));
        var withCnpj = CnpjPattern().Replace(withEmail, m => MaskMatch(pseudonymizer, PersonalDataKind.Cnpj, m.Value));
        var withCpf = CpfPattern().Replace(withCnpj, m => MaskMatch(pseudonymizer, PersonalDataKind.Cpf, m.Value));
        return PhonePattern().Replace(withCpf, m => MaskMatch(pseudonymizer, PersonalDataKind.Phone, m.Value));
    }

    private static string MaskMatch(ILogPseudonymizer pseudonymizer, PersonalDataKind kind, string raw) =>
        pseudonymizer.TryPseudonymize(kind, raw, out var value) ? value.Mask : Redacted;

    // E-mail, também com "%40" (e-mail dentro de URL).
    [GeneratedRegex("(?<![A-Za-z0-9._%+-])[A-Za-z0-9._%+-]+(?:@|%40)[A-Za-z0-9-]+(?:\\.[A-Za-z0-9-]+)*\\.[A-Za-z]{2,}", RegexOptions.None, MatchTimeoutMilliseconds)]
    private static partial Regex EmailPattern();

    // CNPJ formatado, numérico ou alfanumérico (IN RFB 2.229/2024).
    [GeneratedRegex("(?<![A-Za-z0-9])[A-Z0-9]{2}\\.[A-Z0-9]{3}\\.[A-Z0-9]{3}/[A-Z0-9]{4}-[0-9]{2}(?![A-Za-z0-9])", RegexOptions.None, MatchTimeoutMilliseconds)]
    private static partial Regex CnpjPattern();

    // CPF com ou sem pontuação.
    [GeneratedRegex("(?<![A-Za-z0-9])[0-9]{3}\\.?[0-9]{3}\\.?[0-9]{3}-?[0-9]{2}(?![A-Za-z0-9])", RegexOptions.None, MatchTimeoutMilliseconds)]
    private static partial Regex CpfPattern();

    // Celular com DDD (9 + 8 dígitos), com ou sem +55 e pontuação. Sem DDD fica de fora (ADR-0003 do graylog, item 9).
    [GeneratedRegex("(?<![A-Za-z0-9+])(?:\\+?55[ .-]?)?(?:\\([1-9][0-9]\\) ?|[1-9][0-9][ .-]?)9[ .-]?[0-9]{4}[ .-]?[0-9]{4}(?![A-Za-z0-9])", RegexOptions.None, MatchTimeoutMilliseconds)]
    private static partial Regex PhonePattern();
}
