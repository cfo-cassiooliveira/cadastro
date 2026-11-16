using System.Collections.Frozen;

namespace Cadastro.Application.LogSearch;

/// <summary>
/// Campos de log em que a pesquisa por token procura cada tipo de dado. Derivados de
/// <see cref="PersonalDataFields"/> (as listas <c>LGPD_*_FIELDS</c> do graylog, ADR-0002): não há lista própria
/// para divergir. E-mail e login são o mesmo tipo, então usam a mesma lista.
/// </summary>
public static class LogSearchFields
{
    private static readonly FrozenDictionary<PersonalDataKind, string[]> ByKind = PersonalDataFields.ByName
        .GroupBy(pair => pair.Value, pair => pair.Key)
        .ToFrozenDictionary(group => group.Key, group => group.Order(StringComparer.Ordinal).ToArray());

    /// <summary>Nomes dos campos do tipo (ex.: <c>cpf</c>, <c>Cpf</c>, <c>CPF</c>), em ordem estável.</summary>
    public static IReadOnlyList<string> For(PersonalDataKind kind) => ByKind[kind];

    /// <summary>
    /// Consulta do Graylog: OR de <c>&lt;campo&gt;_token:("tok1" OR "tok2")</c> para cada nome de campo do tipo e
    /// cada token (uma chave por token, para achar eventos de antes e depois da rotação).
    /// </summary>
    public static string BuildQuery(PersonalDataKind kind, IReadOnlyList<PseudonymToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var values = string.Join(" OR ", tokens.Select(token => $"\"{token.Token}\""));
        return string.Join(" OR ", For(kind).Select(field => $"{field}{PersonalDataFields.TokenSuffix}:({values})"));
    }
}
