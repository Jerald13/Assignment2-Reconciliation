using System.Text;

namespace Reconciliation.Csv;

/// <summary>
/// Minimal buffered RFC 4180 CSV writer. Quotes a field only when it needs to be quoted.
/// Also neutralises spreadsheet formula injection ("=cmd|..."), since outputs are typically opened in Excel.
/// </summary>
public sealed class CsvWriter : IDisposable
{
    private readonly TextWriter _writer;
    private readonly char _delimiter;
    private readonly bool _neutraliseFormulas;
    private bool _atRowStart = true;

    public CsvWriter(TextWriter writer, char delimiter = ',', bool neutraliseFormulas = true)
    {
        _writer = writer;
        _delimiter = delimiter;
        _neutraliseFormulas = neutraliseFormulas;
    }

    public static CsvWriter Create(string path, int bufferSize = 1 << 16, bool neutraliseFormulas = true)
        => new(
            new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize),
            neutraliseFormulas: neutraliseFormulas);

    /// <summary>Encodes a row as a single CSV line (without line terminator).</summary>
    public static string FormatRow(IEnumerable<string> fields)
    {
        using var buffer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var writer = new CsvWriter(buffer, neutraliseFormulas: false);
        foreach (var field in fields)
        {
            writer.WriteField(field);
        }

        return buffer.ToString();
    }

    public void WriteRow(params ReadOnlySpan<string?> fields)
    {
        foreach (var field in fields)
        {
            WriteField(field);
        }

        EndRow();
    }

    public void WriteField(string? value)
    {
        if (!_atRowStart)
        {
            _writer.Write(_delimiter);
        }

        _atRowStart = false;
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (_neutraliseFormulas && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !IsNumber(value))
        {
            value = "'" + value;
        }

        if (value.AsSpan().IndexOfAny([_delimiter, '"', '\r', '\n']) < 0)
        {
            _writer.Write(value);
            return;
        }

        _writer.Write('"');
        _writer.Write(value.Replace("\"", "\"\""));
        _writer.Write('"');
    }

    public void EndRow()
    {
        _writer.Write("\r\n");
        _atRowStart = true;
    }

    public void Dispose() => _writer.Dispose();

    private static bool IsNumber(string value)
        => decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _);
}
