using System.Globalization;
using Reconciliation.Csv;
using Reconciliation.Model;

namespace Reconciliation.Processing;

/// <summary>
/// Owns the output CSV files. Files are written under a temporary name and only renamed to their
/// final names by <see cref="Commit"/>, so a crash never leaves a half-written report that looks complete.
/// </summary>
public sealed class ReportWriter : IDisposable
{
    public const string MatchedFile = "Matched_Records.csv";
    public const string MissingInBFile = "Missing_In_B.csv";
    public const string MissingInAFile = "Missing_In_A.csv";
    public const string RejectedFile = "Rejected_Records.csv";
    public const string DuplicatesFile = "Duplicate_References.csv";

    private const string TempSuffix = ".partial";

    private readonly string _outputDirectory;
    private readonly SourceSchema _schemaA;
    private readonly SourceSchema _schemaB;
    private readonly CsvWriter _matched;
    private readonly CsvWriter _missingInB;
    private readonly CsvWriter _missingInA;
    private readonly CsvWriter _rejected;
    private readonly CsvWriter _duplicates;
    private bool _disposed;

    public ReportWriter(string outputDirectory, SourceSchema schemaA, SourceSchema schemaB)
    {
        _outputDirectory = outputDirectory;
        _schemaA = schemaA;
        _schemaB = schemaB;
        Directory.CreateDirectory(outputDirectory);

        _matched = Open(MatchedFile);
        _matched.WriteRow(
            "Unique Reference", "Match Status", "Discrepancy Fields",
            "A Line", "A Transaction Date", "A Amount", "A Fees1", "A Fees2", "A Net Total", "A Status",
            "B Line", "B Transaction Date", "B Amount", "B Fees1", "B Fees2", "B Net Total", "B Status", "B Card Number",
            "Amount Difference (A-B)", "Net Total Difference (A-B)");

        _missingInB = Open(MissingInBFile);
        _missingInB.WriteRow(RecordHeader(schemaA));

        _missingInA = Open(MissingInAFile);
        _missingInA.WriteRow(RecordHeader(schemaB));

        _rejected = Open(RejectedFile);
        _rejected.WriteRow("Source", "Line", "Reference", "Errors", "Raw Row");

        _duplicates = Open(DuplicatesFile);
        _duplicates.WriteRow("Source", "Reference", "Duplicate Line", "First Seen Line", "Note");
    }

    public void WriteMatched(TransactionRecord a, TransactionRecord b, IReadOnlyList<string> discrepancies)
    {
        _matched.WriteRow(
            a.Reference,
            discrepancies.Count == 0 ? "MATCHED" : "DISCREPANCY",
            string.Join("; ", discrepancies),
            Line(a.SourceLine), Date(a.TransactionDate), Money(a.Amount), Money(a.Fees1), Money(a.Fees2), Money(a.NetTotal), a.Status,
            Line(b.SourceLine), Date(b.TransactionDate), Money(b.Amount), Money(b.Fees1), Money(b.Fees2), Money(b.NetTotal), b.Status, b.CardNumber,
            Money(a.Amount - b.Amount),
            Money(a.NetTotal - b.NetTotal));
    }

    public void WriteMissingInB(TransactionRecord a) => WriteRecord(_missingInB, a, _schemaA);

    public void WriteMissingInA(TransactionRecord b) => WriteRecord(_missingInA, b, _schemaB);

    public void WriteRejected(string source, long line, string? reference, IEnumerable<string> errors, string[] rawFields)
        => _rejected.WriteRow(
            source,
            Line(line),
            reference,
            string.Join(" | ", errors),
            // Rejected rows are echoed back for the data owner, but never with a full card number.
            Validation.RecordValidator.MaskPotentialCardNumbers(CsvWriter.FormatRow(rawFields)));

    public void WriteDuplicate(string source, TransactionRecord duplicate, long firstSeenLine)
        => _duplicates.WriteRow(
            source,
            duplicate.Reference,
            Line(duplicate.SourceLine),
            Line(firstSeenLine),
            "Duplicate reference excluded from matching; the first occurrence was used.");

    /// <summary>Flushes everything and atomically moves the files to their final names.</summary>
    public void Commit()
    {
        Dispose();
        foreach (var name in new[] { MatchedFile, MissingInBFile, MissingInAFile, RejectedFile, DuplicatesFile })
        {
            File.Move(Path.Combine(_outputDirectory, name + TempSuffix), Path.Combine(_outputDirectory, name), overwrite: true);
        }
    }

    /// <summary>Removes partial files after a failed run.</summary>
    public void Abort()
    {
        Dispose();
        foreach (var name in new[] { MatchedFile, MissingInBFile, MissingInAFile, RejectedFile, DuplicatesFile })
        {
            File.Delete(Path.Combine(_outputDirectory, name + TempSuffix));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _matched.Dispose();
        _missingInB.Dispose();
        _missingInA.Dispose();
        _rejected.Dispose();
        _duplicates.Dispose();
    }

    private CsvWriter Open(string name) => CsvWriter.Create(Path.Combine(_outputDirectory, name + TempSuffix));

    private static string[] RecordHeader(SourceSchema schema)
    {
        var header = new List<string>
        {
            schema.ReferenceColumn, "Source Line", SourceSchema.RowNumberColumn, SourceSchema.TransactionDateColumn,
            SourceSchema.AmountColumn, SourceSchema.Fees1Column, SourceSchema.Fees2Column, SourceSchema.NetTotalColumn,
        };
        if (schema.HasCardNumber)
        {
            header.Add(SourceSchema.CardNumberColumn);
        }

        header.Add(SourceSchema.StatusColumn);
        return header.ToArray();
    }

    private static void WriteRecord(CsvWriter writer, TransactionRecord r, SourceSchema schema)
    {
        writer.WriteField(r.Reference);
        writer.WriteField(Line(r.SourceLine));
        writer.WriteField(r.RowNumber);
        writer.WriteField(Date(r.TransactionDate));
        writer.WriteField(Money(r.Amount));
        writer.WriteField(Money(r.Fees1));
        writer.WriteField(Money(r.Fees2));
        writer.WriteField(Money(r.NetTotal));
        if (schema.HasCardNumber)
        {
            writer.WriteField(r.CardNumber);
        }

        writer.WriteField(r.Status);
        writer.EndRow();
    }


    private static string Line(long line) => line.ToString(CultureInfo.InvariantCulture);

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
