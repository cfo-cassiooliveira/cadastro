using System.Runtime.CompilerServices;
using System.Text;

namespace Cadastro.Application.UserImports;

/// <summary>
/// Extrai pessoas de um CSV UTF-8 estrito, em streaming: lê em blocos de caracteres, sem carregar o arquivo em memória.
/// Aceita campos entre aspas (vírgula, aspas dobradas e quebra de linha dentro do campo) e ignora linhas em branco.
/// A memória por registro é limitada: cada campo guarda no máximo 500 caracteres e só as primeiras colunas são mantidas,
/// então uma linha com milhões de vírgulas vira <c>row.malformed</c> sem alocar milhões de campos.
/// </summary>
internal sealed class CsvUserExtractor : IUserExtractor
{
    private const int MaxFieldLength = 500;
    private const int ColumnCount = 4;
    private const int BufferSize = 16 * 1024;

    // UTF-8 estrito: byte inválido lança em vez de virar U+FFFD em silêncio.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <inheritdoc />
    public async IAsyncEnumerable<ExtractedRow> ExtractAsync(Stream content, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var streamReader = new StreamReader(content, StrictUtf8, detectEncodingFromByteOrderMarks: true, BufferSize, leaveOpen: true);
        var reader = new CsvRecordReader(streamReader);

        var header = await reader.ReadRecordAsync(cancellationToken);
        if (header is null || header.FieldCount != ColumnCount || string.Join(',', header.Fields) != ImportLimits.CsvHeader)
        {
            throw new ImportFileException("file.header_invalid");
        }

        var lineNumber = 0;
        while (await reader.ReadRecordAsync(cancellationToken) is { } record)
        {
            if (record.IsBlank)
            {
                continue;
            }

            if (++lineNumber > ImportLimits.MaxLines)
            {
                throw new ImportFileException("file.too_many_lines");
            }

            yield return ToRow(lineNumber, record);
        }
    }

    private static ExtractedRow ToRow(int lineNumber, CsvRecord record)
    {
        if (record.FieldCount != ColumnCount)
        {
            return new ExtractedRow(lineNumber, null, null, null, null, "row.malformed");
        }

        return new ExtractedRow(
            lineNumber, record.Fields[0], record.Fields[1], record.Fields[2], record.Fields[3], record.HasOversizedField ? "row.field_too_long" : null);
    }

    private sealed record CsvRecord(List<string> Fields, int FieldCount, bool HasOversizedField)
    {
        public bool IsBlank => FieldCount == 1 && Fields[0].Length == 0;
    }

    // Máquina de estados sobre um buffer de caracteres; o estado de aspas atravessa o limite do buffer.
    private sealed class CsvRecordReader(StreamReader reader)
    {
        private readonly char[] _buffer = new char[BufferSize];
        private int _position;
        private int _length;

        public async Task<CsvRecord?> ReadRecordAsync(CancellationToken cancellationToken)
        {
            var fields = new List<string>(ColumnCount + 1);
            var fieldCount = 0;
            var field = new StringBuilder();
            var inQuotes = false;
            var quoteSeen = false;
            var fieldStarted = false;
            var oversized = false;
            var anyChar = false;

            while (true)
            {
                if (_position >= _length && !await FillAsync(cancellationToken))
                {
                    if (!anyChar)
                    {
                        return null;
                    }

                    AddField(fields, ref fieldCount, field);
                    return new CsvRecord(fields, fieldCount, oversized);
                }

                var c = _buffer[_position++];
                anyChar = true;

                if (quoteSeen)
                {
                    quoteSeen = false;
                    if (c == '"')
                    {
                        // Aspas dobradas dentro do campo viram uma aspa literal.
                        Append(field, '"', ref oversized);
                        continue;
                    }

                    inQuotes = false;
                }
                else if (inQuotes)
                {
                    if (c == '"')
                    {
                        quoteSeen = true;
                    }
                    else
                    {
                        Append(field, c, ref oversized);
                    }

                    continue;
                }

                switch (c)
                {
                    case '"' when !fieldStarted:
                        inQuotes = true;
                        fieldStarted = true;
                        break;
                    case ',':
                        AddField(fields, ref fieldCount, field);
                        field.Clear();
                        fieldStarted = false;
                        break;
                    case '\r':
                        break;
                    case '\n':
                        AddField(fields, ref fieldCount, field);
                        return new CsvRecord(fields, fieldCount, oversized);
                    default:
                        fieldStarted = true;
                        Append(field, c, ref oversized);
                        break;
                }
            }
        }

        private async Task<bool> FillAsync(CancellationToken cancellationToken)
        {
            try
            {
                _length = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken);
            }
            catch (DecoderFallbackException)
            {
                throw new ImportFileException("file.not_utf8");
            }

            _position = 0;
            return _length > 0;
        }

        // Conta todas as colunas, mas guarda só as primeiras (acima de ColumnCount a linha já é malformada).
        private static void AddField(List<string> fields, ref int fieldCount, StringBuilder field)
        {
            fieldCount++;
            if (fields.Count <= ColumnCount)
            {
                fields.Add(field.ToString());
            }
        }

        private static void Append(StringBuilder field, char c, ref bool oversized)
        {
            if (field.Length >= MaxFieldLength)
            {
                oversized = true;
                return;
            }

            field.Append(c);
        }
    }
}
