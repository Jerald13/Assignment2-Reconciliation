using System.Globalization;
using Reconciliation.Csv;

namespace Reconciliation.Generation;

/// <summary>
/// Writes large synthetic List A / List B files in the same layout as the provided samples,
/// used to demonstrate performance and memory behaviour on millions of rows.
/// </summary>
/// <remarks>
/// Every row's values are derived from its reference, so a reference present in both files has the
/// same data on both sides, except for ~1% of shared rows where B's amount is deliberately altered
/// to produce discrepancies.
/// </remarks>
public static class SampleDataGenerator
{
    public sealed record Result(string ListAPath, string ListBPath, long Rows, long SharedReferences);

    private static readonly DateOnly StartDate = new(2025, 1, 1);

    /// <param name="rows">Rows per file.</param>
    /// <param name="overlap">Fraction of references present in both files (0..1).</param>
    public static Result Generate(string outputDirectory, long rows, double overlap = 0.9)
    {
        Directory.CreateDirectory(outputDirectory);
        var listA = Path.Combine(outputDirectory, $"ListA_{rows}.csv");
        var listB = Path.Combine(outputDirectory, $"ListB_{rows}.csv");
        var shared = (long)(rows * Math.Clamp(overlap, 0, 1));

        using (var a = CsvWriter.Create(listA, neutraliseFormulas: false))
        using (var b = CsvWriter.Create(listB, neutraliseFormulas: false))
        {
            a.WriteRow("#", "Order Number", "Transaction Date", "Amount", "Fees1", "Fees2", "Net Total", "Status");
            b.WriteRow("#", "Invoice Number", "Transaction Date", "Amount", "Fees1", "Fees2", "Net Total", "Card Number", "Status");

            for (long i = 0; i < rows; i++)
            {
                // A holds references 10,000,000 + i. B holds the first `shared` of them in reverse order
                // (so the join cannot rely on ordering), followed by references unique to B.
                var referenceA = 10_000_000 + i;
                var referenceB = i < shared ? 10_000_000 + (shared - 1 - i) : 90_000_000 + i;

                WriteRow(a, i + 1, referenceA, isListB: false);
                WriteRow(b, i + 1, referenceB, isListB: true);
            }
        }

        return new Result(listA, listB, rows, shared);
    }

    private static void WriteRow(CsvWriter writer, long rowNumber, long reference, bool isListB)
    {
        var h = SplitMix64((ulong)reference);
        var amount = (long)((h % 5_000_000) + 10_000) / 100m;      // 100.00 .. 50,099.99
        var fees1 = -((long)((h >> 20) & 0x3F) / 100m) - 0.50m;    // -0.50 .. -1.13
        var fees2 = -((long)((h >> 28) % 6_000) / 100m);           //  0.00 .. -59.99
        if (isListB && (h >> 50) % 100 == 0)
        {
            amount += 1.00m; // deliberate discrepancy
        }

        var net = amount + fees1 + fees2;
        var date = StartDate.AddDays((int)((h >> 40) & 0xFF));
        var status = ((h >> 8) & 0xF) == 0 ? "Failed" : "Success";

        writer.WriteField(rowNumber.ToString(CultureInfo.InvariantCulture));
        writer.WriteField(reference.ToString(CultureInfo.InvariantCulture));
        writer.WriteField(date.ToString(isListB ? "dd-MM-yyyy" : "yyyy-MM-dd", CultureInfo.InvariantCulture));
        writer.WriteField(amount.ToString("#,##0.00", CultureInfo.InvariantCulture));
        writer.WriteField(fees1.ToString("0.00", CultureInfo.InvariantCulture));
        writer.WriteField(fees2.ToString("0.00", CultureInfo.InvariantCulture));
        writer.WriteField(net.ToString("#,##0.00", CultureInfo.InvariantCulture));
        if (isListB)
        {
            writer.WriteField("555555****1111");
        }

        writer.WriteField(status);
        writer.EndRow();
    }

    private static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EB;
        return x ^ (x >> 31);
    }
}
