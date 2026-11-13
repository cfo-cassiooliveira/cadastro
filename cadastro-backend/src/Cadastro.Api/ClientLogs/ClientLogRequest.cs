namespace Cadastro.Api.ClientLogs;

/// <summary>Evento de log enviado pelo frontend (navegador). Todos os campos são não confiáveis.</summary>
/// <param name="Level">Nível: <c>error</c>, <c>warning</c> ou <c>information</c>.</param>
/// <param name="Event">Código estável do evento (ex.: <c>auth.sign_in_rejected</c>).</param>
/// <param name="Message">Descrição técnica curta (nunca valores digitados pelo usuário).</param>
/// <param name="Source">Tela/componente de origem (ex.: <c>login-page</c>).</param>
/// <param name="Status">Status HTTP relacionado, quando houver.</param>
/// <param name="Stack">Stack trace do erro JavaScript, quando houver.</param>
internal sealed record ClientLogRequest(
    string? Level,
    string? Event,
    string? Message,
    string? Source,
    int? Status,
    string? Stack);
