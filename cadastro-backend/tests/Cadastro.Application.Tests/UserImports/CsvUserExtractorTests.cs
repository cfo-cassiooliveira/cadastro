using System.Text;
using Cadastro.Application.UserImports;

namespace Cadastro.Application.Tests.UserImports;

/// <summary>Extração do CSV: cabeçalho exato, UTF-8 estrito, aspas, limite de linhas e linhas malformadas.</summary>
public sealed class CsvUserExtractorTests
{
    private const string Header = "nome,email,cpf,telefone\n";
    private static readonly CsvUserExtractor Extractor = new();

    private static async Task<List<ExtractedRow>> ExtractAsync(byte[] content)
    {
        var rows = new List<ExtractedRow>();
        using var stream = new MemoryStream(content);
        await foreach (var row in Extractor.ExtractAsync(stream, TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static Task<List<ExtractedRow>> ExtractAsync(string content) => ExtractAsync(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task Extract_ValidFile_ReturnsRowsNumberedFromOne()
    {
        var rows = await ExtractAsync(Header + "Ana,ana@example.com,529.982.247-25,11999990000\r\nBeto,beto@example.com,11144477735,\r\n");

        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(new ExtractedRow(1, "Ana", "ana@example.com", "529.982.247-25", "11999990000"));
        rows[1].LineNumber.ShouldBe(2);
        rows[1].Phone.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Extract_WithUtf8Bom_AcceptsTheFile()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Header + "João,j@example.com,52998224725,\n")).ToArray();

        var rows = await ExtractAsync(bytes);

        rows.Single().Name.ShouldBe("João");
    }

    [Fact]
    public async Task Extract_QuotedFields_KeepCommasQuotesAndLineBreaks()
    {
        var rows = await ExtractAsync(Header + "\"Silva, \"\"Ana\"\"\nMaria\",a@example.com,52998224725,\n");

        rows.Single().Name.ShouldBe("Silva, \"Ana\"\nMaria");
    }

    [Fact]
    public async Task Extract_BlankLines_AreSkippedAndNotCounted()
    {
        var rows = await ExtractAsync(Header + "\nAna,a@example.com,52998224725,\n\n\nBeto,b@example.com,11144477735,\n");

        rows.Select(r => r.LineNumber).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Extract_FileWithoutTrailingNewline_ReadsTheLastRow()
    {
        var rows = await ExtractAsync(Header + "Ana,a@example.com,52998224725,");

        rows.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nome,email,cpf\n")]
    [InlineData("Nome,Email,Cpf,Telefone\n")]
    [InlineData("email,nome,cpf,telefone\n")]
    [InlineData("nome,email,cpf,telefone,extra\n")]
    public async Task Extract_UnexpectedHeader_ThrowsFileError(string content)
    {
        var error = await Should.ThrowAsync<ImportFileException>(() => ExtractAsync(content));

        error.Code.ShouldBe("file.header_invalid");
    }

    [Fact]
    public async Task Extract_InvalidUtf8_ThrowsFileError()
    {
        var bytes = Encoding.UTF8.GetBytes(Header + "Ana,a@example.com,52998224725,\n").Concat(new byte[] { 0xFF, 0xFE, 0x41 }).ToArray();

        var error = await Should.ThrowAsync<ImportFileException>(() => ExtractAsync(bytes));

        error.Code.ShouldBe("file.not_utf8");
    }

    [Fact]
    public async Task Extract_MoreThanFiveThousandLines_ThrowsFileError()
    {
        var content = new StringBuilder(Header);
        for (var i = 0; i <= ImportLimits.MaxLines; i++)
        {
            content.Append("Ana,a").Append(i).Append("@example.com,52998224725,\n");
        }

        var error = await Should.ThrowAsync<ImportFileException>(() => ExtractAsync(content.ToString()));

        error.Code.ShouldBe("file.too_many_lines");
    }

    [Fact]
    public async Task Extract_ExactlyFiveThousandLines_IsAccepted()
    {
        var content = new StringBuilder(Header);
        for (var i = 0; i < ImportLimits.MaxLines; i++)
        {
            content.Append("Ana,a").Append(i).Append("@example.com,52998224725,\n");
        }

        (await ExtractAsync(content.ToString())).Count.ShouldBe(ImportLimits.MaxLines);
    }

    [Theory]
    [InlineData("Ana,a@example.com,52998224725\n")]
    [InlineData("Ana,a@example.com,52998224725,1,2\n")]
    public async Task Extract_WrongColumnCount_MarksTheRowAsMalformed(string line)
    {
        var rows = await ExtractAsync(Header + line);

        rows.Single().StructuralIssue.ShouldBe("row.malformed");
    }

    [Fact]
    public async Task Extract_LineWithMillionsOfCommas_IsMalformedWithoutKeepingTheFields()
    {
        var rows = await ExtractAsync(Header + new string(',', 2_000_000) + "\nAna,a@example.com,52998224725,\n");

        rows.Count.ShouldBe(2);
        rows[0].StructuralIssue.ShouldBe("row.malformed");
        rows[1].Name.ShouldBe("Ana");
    }

    [Fact]
    public async Task Extract_QuotedFieldCrossingTheReadBuffer_KeepsEscapedQuotesCorrect()
    {
        // Campo entre aspas maior que o buffer de leitura (16 KB), com aspas dobradas exatamente na emenda de blocos.
        for (var padding = 16370; padding < 16390; padding++)
        {
            var name = new string('x', padding) + "\"\"y";
            var rows = await ExtractAsync(Header + "\"" + name + "\",a@example.com,52998224725,\n");

            rows.Single().Email.ShouldBe("a@example.com");
            rows.Single().Cpf.ShouldBe("52998224725");
        }
    }

    [Fact]
    public async Task Extract_OversizedField_MarksTheRowWithoutKeepingTheWholeValue()
    {
        var rows = await ExtractAsync(Header + new string('a', 5000) + ",a@example.com,52998224725,\n");

        rows.Single().StructuralIssue.ShouldBe("row.field_too_long");
        rows.Single().Name!.Length.ShouldBeLessThanOrEqualTo(500);
    }
}
