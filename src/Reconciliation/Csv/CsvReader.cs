using System.Text;

namespace Reconciliation.Csv;

/// <summary>
/// Streaming RFC 4180 CSV reader. Reads one record at a time from a buffered stream, so memory use
/// is independent of file size. Supports quoted fields containing delimiters, escaped quotes ("")
/// and line breaks, and both CRLF and LF line endings.
/// </summary>
public sealed class CsvReader : IDisposable
{
    private const int BufferSize = 1 << 16;

    private readonly TextReader _reader;
    private readonly char _delimiter;
    private readonly char[] _buffer = new char[BufferSize];
    private readonly StringBuilder _field = new(64);
    private readonly List<string> _fields = new(16);
    private int _position;
    private int _length;
    private long _physicalLine = 1;

    public CsvReader(TextReader reader, char delimiter = ',')
    {
        _reader = reader;
        _delimiter = delimiter;
    }

    public static CsvReader Open(string path, char delimiter = ',')
        => new(new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: BufferSize), delimiter);

    /// <summary>1-based physical line on which the most recently read record started.</summary>
    public long RecordLine { get; private set; }

    /// <summary>
    /// Reads the next record. Returns false at end of input.
    /// </summary>
    /// <exception cref="CsvFormatException">The final record has an unterminated quoted field.</exception>
    public bool TryReadRecord(out string[] fields)
    {
        _fields.Clear();
        _field.Clear();

        var c = Read();
        if (c < 0)
        {
            fields = [];
            return false;
        }

        RecordLine = _physicalLine;
        var inQuotes = false;

        while (true)
        {
            if (c < 0)
            {
                if (inQuotes)
                {
                    throw new CsvFormatException(RecordLine, "Unterminated quoted field.");
                }

                break;
            }

            var ch = (char)c;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (Peek() == '"')
                    {
                        Read();
                        _field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (ch == '\n')
                    {
                        _physicalLine++;
                    }

                    _field.Append(ch);
                }
            }
            else if (ch == _delimiter)
            {
                EndField();
            }
            else if (ch == '\n')
            {
                _physicalLine++;
                break;
            }
            else if (ch == '\r')
            {
                if (Peek() == '\n')
                {
                    Read();
                }

                _physicalLine++;
                break;
            }
            else if (ch == '"' && _field.Length == 0)
            {
                inQuotes = true;
            }
            else
            {
                _field.Append(ch);
            }

            c = Read();
        }

        EndField();
        fields = _fields.ToArray();
        return true;
    }

    public void Dispose() => _reader.Dispose();

    private void EndField()
    {
        _fields.Add(_field.ToString());
        _field.Clear();
    }

    private int Read()
    {
        if (_position >= _length && !Fill())
        {
            return -1;
        }

        return _buffer[_position++];
    }

    private int Peek()
    {
        if (_position >= _length && !Fill())
        {
            return -1;
        }

        return _buffer[_position];
    }

    private bool Fill()
    {
        _length = _reader.Read(_buffer, 0, _buffer.Length);
        _position = 0;
        return _length > 0;
    }
}

public sealed class CsvFormatException(long line, string message) : Exception($"Line {line}: {message}")
{
    public long Line { get; } = line;
}
