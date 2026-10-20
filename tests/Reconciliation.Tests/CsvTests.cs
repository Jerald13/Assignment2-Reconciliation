using Reconciliation.Csv;

namespace Reconciliation.Tests;

public sealed class CsvTests
{
    private static List<string[]> ReadAll(string csv)
    {
        using var reader = new CsvReader(new StringReader(csv));
        var rows = new List<string[]>();
        while (reader.TryReadRecord(out var fields))
        {
            rows.Add(fields);
        }

        return rows;
    }

    [Fact]
    public void Reads_simple_rows_with_crlf_and_lf()
    {
        var rows = ReadAll("a,b,c\r\n1,2,3\n4,5,6");

        Assert.Equal(3, rows.Count);
        Assert.Equal(["4", "5", "6"], rows[2]);
    }

    [Fact]
    public void Quoted_fields_may_contain_delimiters_quotes_and_newlines()
    {
        var rows = ReadAll("1,\"3,900.00\",\"say \"\"hi\"\"\",\"line1\nline2\"\n2,x,y,z");

        Assert.Equal(["1", "3,900.00", "say \"hi\"", "line1\nline2"], rows[0]);
        Assert.Equal(["2", "x", "y", "z"], rows[1]);
    }

    [Fact]
    public void Tracks_the_physical_line_of_each_record()
    {
        using var reader = new CsvReader(new StringReader("h\n\"multi\nline\"\nnext"));
        reader.TryReadRecord(out _);
        reader.TryReadRecord(out _);
        Assert.Equal(2, reader.RecordLine);
        reader.TryReadRecord(out _);
        Assert.Equal(4, reader.RecordLine);
    }

    [Fact]
    public void Empty_fields_and_trailing_delimiter_are_preserved()
    {
        Assert.Equal(["", "a", ""], ReadAll(",a,")[0]);
    }

    [Fact]
    public void Unterminated_quote_is_reported_with_line_number()
    {
        var ex = Assert.Throws<CsvFormatException>(() => ReadAll("a,b\n1,\"oops"));

        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public void Writer_round_trips_through_reader()
    {
        var buffer = new StringWriter();
        using (var writer = new CsvWriter(buffer, neutraliseFormulas: false))
        {
            writer.WriteRow("plain", "with,comma", "with \"quote\"", "multi\r\nline", null);
        }

        var rows = ReadAll(buffer.ToString());

        Assert.Equal(["plain", "with,comma", "with \"quote\"", "multi\r\nline", ""], rows[0]);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "'=HYPERLINK")]
    [InlineData("@SUM(A1)", "'@SUM")]
    [InlineData("-0.80", "-0.80")] // negative numbers are left alone
    public void Writer_neutralises_spreadsheet_formulas(string value, string expectedPrefix)
    {
        var buffer = new StringWriter();
        using (var writer = new CsvWriter(buffer))
        {
            writer.WriteRow(value);
        }

        Assert.StartsWith(expectedPrefix, buffer.ToString().TrimStart('"'));
    }
}
