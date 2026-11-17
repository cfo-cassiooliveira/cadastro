using System.Globalization;
using Cadastro.Application.LogSearch;
using Serilog.Core;
using Serilog.Events;

namespace Cadastro.Api.Common;

/// <summary>
/// Pseudonimiza na origem (ADR-0003 do graylog) os campos cujo nome está em <see cref="PersonalDataFields"/>, em
/// qualquer nível do evento: troca o valor pela máscara do ADR-0002 e soma o token (HMAC, via
/// <see cref="ILogPseudonymizer"/>) como propriedade de topo. Valor fora do formato, ou não escalar (objeto/lista),
/// vira <c>***</c> sem token. Mesmo comportamento do enricher do legado: os tokens dos dois sistemas são iguais.
/// </summary>
/// <remarks>
/// <para><b>Topo do evento</b> (<c>{Cpf}</c> no template, <c>LogContext</c>, escopo com dicionário): o token vai em
/// <c>Cpf_token</c>, o campo pesquisado pela pesquisa em logs.</para>
/// <para><b>Aninhado</b> (<c>{@Customer}</c>, dicionário, lista): mascarado no próprio nível; o token vai no topo do
/// evento com o caminho unido por <c>_</c> (<c>Customer_Email_token</c>, <c>People_0_login_token</c>), nunca dentro do
/// objeto, para não aparecer no texto renderizado. O Graylog grava o campo mascarado como <c>Customer_Email</c>.</para>
/// <para><b>Textos</b>: <c>message</c>/<c>full_message</c> são renderizados depois, a partir das propriedades já
/// trocadas. O texto do escopo (<c>Scope</c>, já renderizado pelo provider do Microsoft.Extensions.Logging com o valor
/// em claro) recebe a troca do valor original pela máscara (ou por <c>***</c> quando CPF/CNPJ/telefone está fora do
/// formato com 8+ dígitos; login ou e-mail curto ou inválido, de qualquer tamanho, não é trocado, para não corromper palavras). A exceção não é alterada.</para>
/// </remarks>
internal sealed class PseudonymizationEnricher(ILogPseudonymizer pseudonymizer) : ILogEventEnricher
{
    /// <summary>Propriedade em que o Serilog.Extensions.Logging guarda o texto renderizado dos escopos.</summary>
    public const string ScopeProperty = "Scope";

    /// <summary>Valor gravado quando a entrada está fora do formato do tipo: nunca fica em claro e não gera token.</summary>
    public const string Redacted = "***";

    private const int MinDigitsToRedactInText = 8;

    /// <summary>Pseudonimiza os campos de dado pessoal do evento (topo e aninhados) e o texto do escopo.</summary>
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var pass = new Pass();

        foreach (var (name, value) in logEvent.Properties.ToArray())
        {
            if (PersonalDataFields.ByName.TryGetValue(name, out var kind))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, PseudonymizeField(name, kind, value, pass)));
            }
            else if (value is not ScalarValue && name != ScopeProperty)
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, Walk(name, value, pass)));
            }
        }

        if (logEvent.Properties.TryGetValue(ScopeProperty, out var scope))
        {
            var walked = Walk(ScopeProperty, scope, pass);
            logEvent.AddOrUpdateProperty(new LogEventProperty(ScopeProperty, ReplaceInText(walked, pass.Replacements)));
        }

        // Tokens sempre no topo: fora de qualquer objeto renderizado no texto (message só leva a máscara).
        foreach (var token in pass.Tokens)
        {
            logEvent.AddOrUpdateProperty(token);
        }
    }

    /// <summary>
    /// Devolve o valor mascarado (ou <c>***</c>) e registra o token de topo <c>&lt;path&gt;_token</c>, onde
    /// <c>path</c> é o nome do campo no topo ou o caminho aninhado unido por <c>_</c> (ex.: <c>Customer_Email</c>).
    /// </summary>
    private LogEventPropertyValue PseudonymizeField(string path, PersonalDataKind kind, LogEventPropertyValue value, Pass pass)
    {
        if (value is ScalarValue { Value: null })
        {
            return value;
        }

        var raw = value is ScalarValue scalar ? Convert.ToString(scalar.Value, CultureInfo.InvariantCulture) : null;
        if (raw is not null && pseudonymizer.TryPseudonymize(kind, raw, out var pseudonym))
        {
            pass.Replacements.Add(KeyValuePair.Create(raw, pseudonym.Mask));
            pass.Tokens.Add(new LogEventProperty(path + PersonalDataFields.TokenSuffix, new ScalarValue(pseudonym.Token)));
            return new ScalarValue(pseudonym.Mask);
        }

        if (raw is not null && kind != PersonalDataKind.Login && raw.Count(char.IsAsciiDigit) >= MinDigitsToRedactInText)
        {
            pass.Replacements.Add(KeyValuePair.Create(raw, Redacted));
        }

        return new ScalarValue(Redacted);
    }

    private LogEventPropertyValue Walk(string path, LogEventPropertyValue value, Pass pass) =>
        value switch
        {
            StructureValue structure => new StructureValue(
                structure.Properties.Select(p => new LogEventProperty(p.Name, WalkMember(Child(path, p.Name), p.Name, p.Value, pass))).ToList(),
                structure.TypeTag),
            DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(e => KeyValuePair.Create(e.Key,
                e.Key.Value is string key ? WalkMember(Child(path, key), key, e.Value, pass) : Walk(path, e.Value, pass))).ToList()),
            SequenceValue sequence => new SequenceValue(
                sequence.Elements.Select((e, index) => Walk(Child(path, index.ToString(CultureInfo.InvariantCulture)), e, pass)).ToList()),
            _ => value,
        };

    private LogEventPropertyValue WalkMember(string path, string name, LogEventPropertyValue value, Pass pass) =>
        PersonalDataFields.ByName.TryGetValue(name, out var kind) ? PseudonymizeField(path, kind, value, pass) : Walk(path, value, pass);

    // "_" e não ".": o Graylog troca o ponto dos nomes GELF por "_" (Customer.Email é gravado como Customer_Email).
    private static string Child(string path, string name) => $"{path}_{name}";

    private static LogEventPropertyValue ReplaceInText(LogEventPropertyValue value, List<KeyValuePair<string, string>> replacements) =>
        value switch
        {
            ScalarValue { Value: string text } => new ScalarValue(Replace(text, replacements)),
            SequenceValue sequence => new SequenceValue(sequence.Elements.Select(e => ReplaceInText(e, replacements)).ToList()),
            _ => value,
        };

    private static string Replace(string text, List<KeyValuePair<string, string>> replacements) =>
        // O valor mais longo primeiro: evita trocar só um pedaço de outro valor que o contém.
        replacements.OrderByDescending(r => r.Key.Length)
            .Aggregate(text, (current, r) => current.Replace(r.Key, r.Value, StringComparison.Ordinal));

    /// <summary>Estado de uma passada: valores a trocar nos textos e tokens a somar no topo do evento.</summary>
    private sealed class Pass
    {
        public List<KeyValuePair<string, string>> Replacements { get; } = [];

        public List<LogEventProperty> Tokens { get; } = [];
    }
}
