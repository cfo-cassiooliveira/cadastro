using System.Text.RegularExpressions;

namespace Cadastro.Application.LogSearch;

/// <summary>Pedido de consulta por token (ADR-0003 do graylog, item 7). O valor nunca é registrado nem devolvido.</summary>
/// <param name="DataType"><c>cpf</c>, <c>cnpj</c>, <c>phone</c>, <c>email</c> ou <c>login</c>.</param>
/// <param name="Value">Valor pesquisado, em qualquer formatação do tipo.</param>
/// <param name="CaseReference">Motivo da pesquisa (chamado/incidente), ex.: <c>INC-1234</c>.</param>
public sealed record GetLogSearchQuery(string? DataType, string? Value, string? CaseReference);

/// <summary>Consulta pronta para colar no Graylog.</summary>
/// <param name="DataType">Tipo pesquisado, como informado (já validado).</param>
/// <param name="Tokens">Token do valor com cada chave configurada.</param>
/// <param name="GraylogQuery">Consulta sobre todos os campos do tipo e todos os tokens.</param>
public sealed record LogSearchQueryDto(string DataType, IReadOnlyList<PseudonymToken> Tokens, string GraylogQuery);

/// <summary>Resultado da geração: a consulta, ou os códigos de erro por campo (sem eco do valor informado).</summary>
public sealed record GetLogSearchQueryResult
{
    /// <summary>Consulta gerada; <c>null</c> quando há erros.</summary>
    public LogSearchQueryDto? Query { get; private init; }

    /// <summary>Códigos de erro por campo da requisição (<c>dataType</c>, <c>value</c>, <c>caseReference</c>).</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; private init; } = new Dictionary<string, string[]>();

    /// <summary>Resultado de sucesso.</summary>
    public static GetLogSearchQueryResult Success(LogSearchQueryDto query) => new() { Query = query };

    /// <summary>Resultado de falha de validação.</summary>
    public static GetLogSearchQueryResult Invalid(IReadOnlyDictionary<string, string[]> errors) => new() { Errors = errors };
}

/// <summary>Caso de uso "Gerar consulta de pesquisa em logs" (leitura: não altera estado).</summary>
public interface IGetLogSearchQueryHandler
{
    /// <summary>Valida o pedido e monta tokens e consulta; nunca lança por entrada inválida.</summary>
    GetLogSearchQueryResult Handle(GetLogSearchQuery query);
}

/// <summary>
/// Calcula os tokens pela porta <see cref="ILogPseudonymizer"/> e monta a consulta sobre todos os campos do tipo
/// (<see cref="LogSearchFields"/>). Os erros só trazem códigos: o valor informado nunca volta na resposta.
/// Permite investigar logs pseudonimizados pelo token, sem digitar o dado pessoal no Graylog. Só encontra o que foi logado
/// como campo estruturado e reconhecido no formato do tipo.
/// </summary>
internal sealed partial class GetLogSearchQueryHandler(ILogPseudonymizer pseudonymizer) : IGetLogSearchQueryHandler
{
    private const int MaxValueLength = 254;

    // Com 8+ dígitos (em qualquer pontuação) o motivo pode ser um CPF/CNPJ/telefone digitado por engano, e iria em claro
    // para a auditoria (case_reference não é pseudonimizado): recusa.
    private const int MaxCaseReferenceDigits = 8;
    private const string Required = "log_search.required";

    /// <inheritdoc />
    public GetLogSearchQueryResult Handle(GetLogSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var kind = ParseKind(query.DataType);
        if (string.IsNullOrWhiteSpace(query.DataType))
        {
            errors["dataType"] = [Required];
        }
        else if (kind is null)
        {
            errors["dataType"] = ["log_search.invalid_data_type"];
        }

        if (string.IsNullOrWhiteSpace(query.CaseReference))
        {
            errors["caseReference"] = [Required];
        }
        else if (!CaseReferencePattern().IsMatch(query.CaseReference)
                 || query.CaseReference.Count(char.IsAsciiDigit) >= MaxCaseReferenceDigits)
        {
            errors["caseReference"] = ["log_search.invalid_case_reference"];
        }

        IReadOnlyList<PseudonymToken> tokens = [];
        if (string.IsNullOrWhiteSpace(query.Value))
        {
            errors["value"] = [Required];
        }
        else if (kind is { } valueKind)
        {
            // E-mail precisa de "@" (o canônico de login aceita qualquer texto de 4+ caracteres).
            var isEmailWithoutAt = query.DataType == "email" && !query.Value.Contains('@', StringComparison.Ordinal);
            tokens = query.Value.Length <= MaxValueLength && !isEmailWithoutAt ? pseudonymizer.Tokenize(valueKind, query.Value) : [];
            if (tokens.Count == 0)
            {
                errors["value"] = ["log_search.invalid_value"];
            }
        }

        return errors.Count > 0
            ? GetLogSearchQueryResult.Invalid(errors)
            : GetLogSearchQueryResult.Success(
                new LogSearchQueryDto(query.DataType!, tokens, LogSearchFields.BuildQuery(kind!.Value, tokens)));
    }

    private static PersonalDataKind? ParseKind(string? dataType) => dataType switch
    {
        "cpf" => PersonalDataKind.Cpf,
        "cnpj" => PersonalDataKind.Cnpj,
        "phone" => PersonalDataKind.Phone,
        "email" or "login" => PersonalDataKind.Login,
        _ => null,
    };

    [GeneratedRegex("^[A-Za-z0-9._/-]{3,40}\\z")]
    private static partial Regex CaseReferencePattern();
}
