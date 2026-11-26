using System.Security.Cryptography;
using Cadastro.Domain.UserImports;
using Microsoft.Extensions.Logging;

namespace Cadastro.Application.UserImports;

/// <summary>Quem fez a requisição e como correlacioná-la (sem dado pessoal).</summary>
/// <param name="ActorId">Identificador (<c>sub</c>) do administrador.</param>
/// <param name="CorrelationId">Correlação da requisição.</param>
/// <param name="TraceParent">Cabeçalho W3C <c>traceparent</c> da requisição.</param>
public sealed record ImportRequestContext(string ActorId, string? CorrelationId, string? TraceParent);

/// <summary>Envio de um arquivo para importação. O fluxo precisa ser pesquisável (arquivo temporário), nunca carregado em memória.</summary>
/// <param name="Content">Conteúdo do arquivo, posicionável.</param>
/// <param name="Context">Quem enviou.</param>
public sealed record UploadUserImportCommand(Stream Content, ImportRequestContext Context);

/// <summary>Resultado do envio.</summary>
public enum UploadOutcome
{
    /// <summary>Lote criado, aguardando confirmação.</summary>
    Created = 0,

    /// <summary>O mesmo arquivo já foi enviado (lote existente não cancelado).</summary>
    Duplicate = 1,

    /// <summary>Arquivo recusado; veja o código.</summary>
    Rejected = 2,
}

/// <summary>Problema de uma linha: só o número e o código, nunca o valor.</summary>
/// <param name="Line">Número da linha de dados.</param>
/// <param name="Code">Código do problema (<c>row.cpf_invalid</c>...).</param>
public sealed record ImportRowIssue(int Line, string Code);

/// <summary>Resumo do arquivo validado, sem dados pessoais.</summary>
/// <param name="BatchId">Lote criado ou existente.</param>
/// <param name="ValidLines">Linhas válidas.</param>
/// <param name="InvalidLines">Linhas inválidas.</param>
/// <param name="DuplicateLines">Linhas duplicadas no arquivo (e-mail ou CPF repetido).</param>
/// <param name="SummaryToken">Token exigido na confirmação.</param>
/// <param name="Issues">Primeiros problemas por linha (até <see cref="ImportLimits.MaxReportedIssues"/>).</param>
/// <param name="IssuesTruncated">Indica que há mais problemas do que os listados.</param>
public sealed record UploadSummary(
    Guid BatchId, int ValidLines, int InvalidLines, int DuplicateLines, string SummaryToken, IReadOnlyList<ImportRowIssue> Issues, bool IssuesTruncated);

/// <summary>Resultado do upload: lote criado, duplicado ou arquivo recusado com código.</summary>
public sealed record UploadUserImportResult
{
    /// <summary>Desfecho.</summary>
    public UploadOutcome Outcome { get; private init; }

    /// <summary>Código da recusa (<see cref="UploadOutcome.Rejected"/>).</summary>
    public string? Code { get; private init; }

    /// <summary>Resumo (criado) ou apenas os problemas (recusa por falta de linhas válidas).</summary>
    public UploadSummary? Summary { get; private init; }

    /// <summary>Lote já existente (<see cref="UploadOutcome.Duplicate"/>).</summary>
    public Guid? ExistingBatchId { get; private init; }

    /// <summary>Problemas por linha, quando o arquivo foi recusado por não ter nenhuma linha válida.</summary>
    public IReadOnlyList<ImportRowIssue> Issues { get; private init; } = [];

    internal static UploadUserImportResult Created(UploadSummary summary) => new() { Outcome = UploadOutcome.Created, Summary = summary };

    internal static UploadUserImportResult Duplicate(Guid existing) => new() { Outcome = UploadOutcome.Duplicate, ExistingBatchId = existing };

    internal static UploadUserImportResult Rejected(string code, IReadOnlyList<ImportRowIssue>? issues = null) =>
        new() { Outcome = UploadOutcome.Rejected, Code = code, Issues = issues ?? [] };
}

/// <summary>Caso de uso "Enviar arquivo de importação".</summary>
public interface IUploadUserImportHandler
{
    /// <summary>Verifica o antivírus, valida o arquivo e cria o lote aguardando confirmação.</summary>
    Task<UploadUserImportResult> HandleAsync(UploadUserImportCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// Fluxo: tamanho, antivírus (fail-closed), hash e reenvio, extração e validação em streaming, cifra dos dados das
/// linhas válidas e gravação do lote em <see cref="ImportBatchStatus.AwaitingConfirmation"/>. Linhas inválidas não são
/// gravadas: só entram nas contagens e nos códigos devolvidos.
/// </summary>
internal sealed partial class UploadUserImportHandler(
    IVirusScanner scanner,
    IUserExtractor extractor,
    IPersonalDataProtector protector,
    IImportSummarySigner signer,
    IUserImportWriter writer,
    TimeProvider timeProvider,
    ILogger<UploadUserImportHandler> logger) : IUploadUserImportHandler
{
    /// <inheritdoc />
    public async Task<UploadUserImportResult> HandleAsync(UploadUserImportCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var content = command.Content;
        if (!content.CanSeek)
        {
            throw new ArgumentException("The upload stream must be seekable.", nameof(command));
        }

        if (content.Length > ImportLimits.MaxFileBytes)
        {
            return UploadUserImportResult.Rejected("file.too_large");
        }

        if (await ScanAsync(content, cancellationToken) is { } scanRejection)
        {
            return scanRejection;
        }

        var fileHash = await ComputeHashAsync(content, cancellationToken);
        if (await writer.FindActiveBatchIdByHashAsync(fileHash, cancellationToken) is { } existing)
        {
            LogDuplicate(existing);
            return UploadUserImportResult.Duplicate(existing);
        }

        UserImportValidation validation;
        try
        {
            validation = await ValidateAsync(content, cancellationToken);
        }
        catch (ImportFileException ex)
        {
            LogRejected(ex.Code);
            return UploadUserImportResult.Rejected(ex.Code);
        }

        if (validation.Users.Count == 0)
        {
            LogRejected("import.no_valid_lines");
            return UploadUserImportResult.Rejected("import.no_valid_lines", validation.Issues);
        }

        return await CreateBatchAsync(command.Context, fileHash, validation, cancellationToken);
    }

    private async Task<UploadUserImportResult?> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        try
        {
            content.Position = 0;
            var result = await scanner.ScanAsync(content, cancellationToken);
            if (result == VirusScanResult.Infected)
            {
                LogInfected();
                return UploadUserImportResult.Rejected("file.infected");
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-closed: qualquer falha do antivírus recusa o arquivo, nunca o aceita sem verificar.
            LogScannerUnavailable(ex);
            return UploadUserImportResult.Rejected("scanner.unavailable");
        }
    }

    private static async Task<string> ComputeHashAsync(Stream content, CancellationToken cancellationToken)
    {
        content.Position = 0;
        var hash = await SHA256.HashDataAsync(content, cancellationToken);
        content.Position = 0;
        return Convert.ToHexStringLower(hash);
    }

    private async Task<UserImportValidation> ValidateAsync(Stream content, CancellationToken cancellationToken)
    {
        var validation = new UserImportValidation();
        await foreach (var row in extractor.ExtractAsync(content, cancellationToken))
        {
            validation.Add(row);
        }

        return validation;
    }

    private async Task<UploadUserImportResult> CreateBatchAsync(
        ImportRequestContext context, string fileHash, UserImportValidation validation, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var batchId = Guid.CreateVersion7();
        var batch = ImportBatch.Create(
            batchId, fileHash, context.ActorId, validation.Users.Count, validation.InvalidLines, validation.DuplicateLines, now);
        var lines = validation.Users.Select(user => ToLine(user, batchId, now)).ToList();
        writer.AddBatch(batch, lines);
        writer.AddAudit(ImportAuditEntry.Create(Guid.CreateVersion7(), context.ActorId, "batch.uploaded", batchId, context.CorrelationId, now));

        if (await writer.SaveChangesAsync(cancellationToken) == ImportSaveResult.DuplicateFileHash
            && await writer.FindActiveBatchIdByHashAsync(fileHash, cancellationToken) is { } winner)
        {
            // Dois envios iguais ao mesmo tempo: o índice único deixou só um; este vira reenvio.
            LogDuplicate(winner);
            return UploadUserImportResult.Duplicate(winner);
        }

        var token = signer.Sign(batchId, context.ActorId, fileHash, batch.ValidLines, batch.InvalidLines, batch.DuplicateLines);
        LogCreated(batchId, batch.ValidLines, batch.InvalidLines, batch.DuplicateLines);
        return UploadUserImportResult.Created(new UploadSummary(
            batchId, batch.ValidLines, batch.InvalidLines, batch.DuplicateLines, token, validation.Issues, validation.IssuesTruncated));
    }

    // Cifra cada campo com o contexto "{lineId}:{campo}", para o valor não servir em outra linha ou coluna.
    private ImportLine ToLine(ValidatedUser user, Guid batchId, DateTimeOffset now)
    {
        var lineId = Guid.CreateVersion7();
        var data = new ProtectedPersonalData(
            protector.Protect(user.Name, ImportLineProtection.Context(lineId, ImportLineProtection.NameField)),
            protector.Protect(user.Email, ImportLineProtection.Context(lineId, ImportLineProtection.EmailField)),
            protector.Protect(user.Cpf, ImportLineProtection.Context(lineId, ImportLineProtection.CpfField)),
            user.Phone is null ? null : protector.Protect(user.Phone, ImportLineProtection.Context(lineId, ImportLineProtection.PhoneField)),
            protector.ComputeEmailIndex(user.Email),
            protector.CurrentKeyVersion);
        return ImportLine.Create(lineId, batchId, user.LineNumber, data, now);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import upload refused: the virus scanner flagged the file")]
    private partial void LogInfected();

    [LoggerMessage(Level = LogLevel.Error, Message = "Import upload refused: the virus scanner could not verify the file (fail-closed)")]
    private partial void LogScannerUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import upload rejected with code {Code}")]
    private partial void LogRejected(string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import upload repeated an existing file; batch {BatchId} returned")]
    private partial void LogDuplicate(Guid batchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Import batch {BatchId} created awaiting confirmation: {ValidLines} valid, {InvalidLines} invalid, {DuplicateLines} duplicated")]
    private partial void LogCreated(Guid batchId, int validLines, int invalidLines, int duplicateLines);
}

/// <summary>Convenção do contexto autenticado (AAD) usado para cifrar cada campo; o consumidor usa a mesma para decifrar.</summary>
public static class ImportLineProtection
{
    /// <summary>Campo nome.</summary>
    public const string NameField = "name";

    /// <summary>Campo e-mail.</summary>
    public const string EmailField = "email";

    /// <summary>Campo CPF.</summary>
    public const string CpfField = "cpf";

    /// <summary>Campo telefone.</summary>
    public const string PhoneField = "phone";

    /// <summary>Contexto <c>{lineId}:{campo}</c> do valor.</summary>
    public static string Context(Guid lineId, string field) => $"{lineId:D}:{field}";
}
