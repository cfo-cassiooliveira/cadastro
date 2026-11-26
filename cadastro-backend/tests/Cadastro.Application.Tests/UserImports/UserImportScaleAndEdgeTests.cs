using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Cadastro.Application.UserImports;
using Cadastro.Domain.UserImports;
using Cadastro.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Cadastro.Application.Tests.UserImports;

/// <summary>Lote no limite (5.000 linhas) de ponta a ponta, variações de arquivo e e-mail/CPF, e reprocesso repetido (QA da onda 2).</summary>
public sealed class UserImportScaleAndEdgeTests
{
    private const string Header = "nome,email,cpf,telefone";
    private static readonly ImportRequestContext Admin = new("admin-1", "corr-12345678", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

    private readonly InMemoryUserImportStore _store = new();
    private readonly IVirusScanner _scanner = Substitute.For<IVirusScanner>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 25, 13, 0, 0, TimeSpan.Zero));
    private readonly IPersonalDataProtector _protector = Substitute.For<IPersonalDataProtector>();
    private readonly HmacImportSummarySigner _signer = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private readonly List<string> _protectedValues = [];

    public UserImportScaleAndEdgeTests()
    {
        _scanner.ScanAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(VirusScanResult.Clean);
        _protector.CurrentKeyVersion.Returns("k1");
        _protector.Protect(Arg.Any<string>(), Arg.Any<string>()).Returns(call =>
        {
            _protectedValues.Add(call.ArgAt<string>(0));
            return Encoding.UTF8.GetBytes("enc:" + call.ArgAt<string>(0));
        });
        _protector.ComputeEmailIndex(Arg.Any<string>()).Returns(call => SHA256.HashData(Encoding.UTF8.GetBytes(call.ArgAt<string>(0))));
    }

    private Task<UploadUserImportResult> UploadAsync(byte[] content) =>
        new UploadUserImportHandler(_scanner, new CsvUserExtractor(), _protector, _signer, _store, _clock, NullLogger<UploadUserImportHandler>.Instance)
            .HandleAsync(new UploadUserImportCommand(new MemoryStream(content), Admin), TestContext.Current.CancellationToken);

    private Task<UploadUserImportResult> UploadAsync(string content) => UploadAsync(Encoding.UTF8.GetBytes(content));

    private Task<ImportCommandResult> ConfirmAsync(UploadSummary summary) =>
        new ConfirmUserImportHandler(_store, _signer, _clock, NullLogger<ConfirmUserImportHandler>.Instance)
            .HandleAsync(new ConfirmUserImportCommand(summary.BatchId, summary.SummaryToken, Admin), TestContext.Current.CancellationToken);

    /// <summary>Gera CPFs válidos e distintos a partir de um número base de 9 dígitos.</summary>
    private static string ValidCpf(int seed)
    {
        var digits = (100_000_000 + (seed * 7)).ToString("D9", System.Globalization.CultureInfo.InvariantCulture);
        var all = digits.Select(c => c - '0').ToList();
        for (var length = 9; length <= 10; length++)
        {
            var sum = 0;
            for (var i = 0; i < length; i++)
            {
                sum += all[i] * (length + 1 - i);
            }

            var rest = sum * 10 % 11;
            all.Add(rest == 10 ? 0 : rest);
        }

        return string.Concat(all);
    }

    [Fact]
    public async Task FiveThousandValidLines_GoFromUploadToOutboxOneMessageEachWithLinearWork()
    {
        var csv = new StringBuilder(Header).Append("\r\n");
        for (var i = 1; i <= ImportLimits.MaxLines; i++)
        {
            csv.Append("Pessoa ").Append(i).Append(",user").Append(i).Append("@example.com,").Append(ValidCpf(i)).Append(",11999990000\r\n");
        }

        var watch = Stopwatch.StartNew();
        var summary = (await UploadAsync(csv.ToString())).Summary!;
        var confirm = await ConfirmAsync(summary);
        watch.Stop();

        summary.ValidLines.ShouldBe(ImportLimits.MaxLines);
        summary.InvalidLines.ShouldBe(0);
        summary.DuplicateLines.ShouldBe(0);
        confirm.ShouldBe(new ImportCommandResult(ImportCommandOutcome.Done, ImportLimits.MaxLines));
        _store.Events.Count.ShouldBe(ImportLimits.MaxLines);
        _store.Events.Select(e => e.MessageId).Distinct().Count().ShouldBe(ImportLimits.MaxLines);
        _store.Events.Select(e => e.Payload).Distinct().Count().ShouldBe(ImportLimits.MaxLines);
        _store.Lines.Count.ShouldBe(ImportLimits.MaxLines);
        // Trabalho linear: um único índice de e-mail e no máximo 4 cifras por linha (sem N+1 nem recálculo na confirmação).
        _protector.Received(ImportLimits.MaxLines).ComputeEmailIndex(Arg.Any<string>());
        _protectedValues.Count.ShouldBeLessThanOrEqualTo(ImportLimits.MaxLines * 4);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task BomCrlfAndQuotedFields_KeepCommaAndRejectLineBreakInName()
    {
        // Quebra de linha dentro do nome é caractere de controle: a linha é inválida; as demais passam.
        var csv = Header + "\r\n\"Silva, Ana\",ana@example.com,529.982.247-25,\r\n\"Maria\r\nSouza\",maria@example.com,39053344705,\r\nBeto,beto@example.com,11144477735,\r\n";
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(csv)).ToArray();

        var summary = (await UploadAsync(bytes)).Summary!;

        summary.ValidLines.ShouldBe(2);
        summary.Issues.Select(i => (i.Line, i.Code)).ShouldBe([(2, "row.name_invalid")]);
        _protectedValues.ShouldContain("Silva, Ana");
        _protectedValues.ShouldContain("52998224725");
    }

    [Fact]
    public async Task Upload_EmailAndCpfVariations_NormalizeOrRejectConsistently()
    {
        var csv = Header + "\n" +
            "Ana,  ANA@Example.COM  ,529.982.247-25,\n" +
            "Beto,beto@example.com,111.444.777-35 ,\n" +
            "Caio,caio@exämple.com,52998224725,\n" +
            "Dani,dani@example.com,５２９９８２２４７２５,\n" +
            "Eva,eva@example.com,529 982 247 25,\n" +
            "Fabio,ana@example.com,39053344705,\n";

        var summary = (await UploadAsync(csv)).Summary!;

        summary.ValidLines.ShouldBe(2);
        summary.InvalidLines.ShouldBe(3);
        summary.DuplicateLines.ShouldBe(1);
        summary.Issues.Select(i => (i.Line, i.Code)).ShouldBe([(3, "row.email_invalid"), (4, "row.cpf_invalid"), (5, "row.cpf_invalid"), (6, "row.duplicate_email")]);
    }

    [Fact]
    public async Task ReprocessSameLineTwice_SecondIsRefusedAndQueuesNothingMore()
    {
        var summary = (await UploadAsync(Header + "\nAna,ana@example.com,52998224725,\nBeto,beto@example.com,11144477735,\n")).Summary!;
        await ConfirmAsync(summary);
        var failed = _store.Lines[0];
        failed.MarkFailed("keycloak.unavailable", _clock.GetUtcNow());
        _store.Batches.Single().RecordLineOutcome(ImportLineStatus.Error, _clock.GetUtcNow());
        var handler = new ReprocessUserImportLineHandler(_store, _clock, NullLogger<ReprocessUserImportLineHandler>.Instance);
        var before = _store.Events.Count;

        var first = await handler.HandleAsync(new ReprocessUserImportLineCommand(summary.BatchId, failed.Id, Admin), TestContext.Current.CancellationToken);
        var second = await handler.HandleAsync(new ReprocessUserImportLineCommand(summary.BatchId, failed.Id, Admin), TestContext.Current.CancellationToken);

        first.Outcome.ShouldBe(ImportCommandOutcome.Done);
        second.Outcome.ShouldBe(ImportCommandOutcome.InvalidState);
        failed.ReprocessCount.ShouldBe(1);
        _store.Events.Count.ShouldBe(before + 1);
        _store.Audits.Count(a => a.Action == "line.reprocess-requested").ShouldBe(1);
    }

    [Fact]
    public async Task CancelAfterConfirm_IsRefusedAndKeepsLinesAndOutbox()
    {
        var summary = (await UploadAsync(Header + "\nAna,ana@example.com,52998224725,\n")).Summary!;
        await ConfirmAsync(summary);

        var result = await new CancelUserImportHandler(_store, _clock, NullLogger<CancelUserImportHandler>.Instance)
            .HandleAsync(new CancelUserImportCommand(summary.BatchId, Admin), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ImportCommandOutcome.InvalidState);
        _store.Batches.Single().Status.ShouldBe(ImportBatchStatus.Processing);
        _store.Lines.ShouldAllBe(l => l.HasPersonalData);
        _store.Events.Count.ShouldBe(1);
    }
}
