using System.Diagnostics.CodeAnalysis;

namespace Cadastro.Application.LogSearch;

/// <summary>
/// Porta da pseudonimização de dado pessoal nos logs (ADR-0003 do graylog), implementada na Infrastructure com
/// HMAC-SHA256. A mesma especificação do legado: o mesmo valor gera o mesmo token nos dois sistemas.
/// </summary>
public interface ILogPseudonymizer
{
    /// <summary>
    /// Máscara do ADR-0002 e token da chave atual (o que vai ao log). <c>false</c> quando o valor está fora do
    /// formato do tipo: nesse caso não há máscara nem token.
    /// </summary>
    bool TryPseudonymize(PersonalDataKind kind, string raw, [NotNullWhen(true)] out PseudonymizedValue? value);

    /// <summary>
    /// Tokens do valor com cada chave configurada, para pesquisar eventos emitidos antes e depois da rotação.
    /// Lista vazia quando o valor está fora do formato do tipo.
    /// </summary>
    IReadOnlyList<PseudonymToken> Tokenize(PersonalDataKind kind, string raw);
}

/// <summary>Valor pronto para o log: máscara parcial e token da chave atual.</summary>
/// <param name="Mask">Máscara do ADR-0002 (ex.: <c>***.456.789-**</c>).</param>
/// <param name="Token">Token pesquisável (ex.: <c>k1-b81c27bb5f38080e6ca0ed38b828f262</c>).</param>
public sealed record PseudonymizedValue(string Mask, string Token);

/// <summary>Token gerado com uma chave específica.</summary>
/// <param name="KeyId">Id da chave (<c>k1</c>, <c>k2</c>...).</param>
/// <param name="Token">Token com o prefixo da chave.</param>
public sealed record PseudonymToken(string KeyId, string Token);
