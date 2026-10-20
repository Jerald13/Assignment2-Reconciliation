using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Reconciliation.Csv;
using Reconciliation.Model;
using Reconciliation.Validation;

namespace Reconciliation.Processing;

/// <summary>
/// Reconciles List A against List B on the unique reference (A.Order Number = B.Invoice Number).
/// </summary>
/// <remarks>
/// <para><b>Phase 1 - validate and partition (streaming).</b> Each file is read one row at a time,
/// validated, and appended to one of N partition files chosen by hash(reference). Invalid rows go
/// to the rejects report. Nothing is accumulated in memory.</para>
/// <para><b>Phase 2 - join each partition.</b> For partition i, B's records are loaded into a
/// dictionary and A's records are streamed against it. Since equal references always hash to the
/// same partition, every match is found, and peak memory is one partition rather than one file.</para>
/// <para>Time is O(|A| + |B|); memory is O((|A| + |B|) / N).</para>
/// </remarks>
public sealed class ReconciliationEngine(ReconciliationOptions options, ILogger<ReconciliationEngine> logger)
{
    private const int ProgressInterval = 1_000_000;

    public ReconciliationSummary Run(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        EnsureInputExists(options.ListAPath, options.SchemaA);
        EnsureInputExists(options.ListBPath, options.SchemaB);

        var summary = new ReconciliationSummary
        {
            ListA = new SourceStatistics { Name = options.SchemaA.Name, File = Path.GetFullPath(options.ListAPath) },
            ListB = new SourceStatistics { Name = options.SchemaB.Name, File = Path.GetFullPath(options.ListBPath) },
            Partitions = options.ResolvePartitionCount(),
            OutputDirectory = Path.GetFullPath(options.OutputDirectory),
        };

        logger.LogInformation("Reconciling {ListA} against {ListB} using {Partitions} partition(s)",
            summary.ListA.File, summary.ListB.File, summary.Partitions);

        using var partitions = new PartitionStore(options.TempDirectory, summary.Partitions);
        var reports = new ReportWriter(options.OutputDirectory, options.SchemaA, options.SchemaB);
        try
        {
            ValidateAndPartition(options.ListAPath, options.SchemaA, PartitionStore.SideA, partitions, reports, summary.ListA, cancellationToken);
            ValidateAndPartition(options.ListBPath, options.SchemaB, PartitionStore.SideB, partitions, reports, summary.ListB, cancellationToken);

            for (var p = 0; p < partitions.PartitionCount; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReconcilePartition(p, partitions, reports, summary);
            }

            reports.Commit();
        }
        catch
        {
            reports.Abort();
            throw;
        }

        summary.ElapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2);
        summary.PeakWorkingSetMb = Math.Round(Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024.0), 1);
        return summary;
    }

    private void ValidateAndPartition(
        string path,
        SourceSchema schema,
        string side,
        PartitionStore partitions,
        ReportWriter reports,
        SourceStatistics stats,
        CancellationToken cancellationToken)
    {
        using var reader = CsvReader.Open(path);
        if (!reader.TryReadRecord(out var header))
        {
            throw new SchemaException($"{schema.Name}: file is empty - a header row is required.");
        }

        var columns = ColumnMap.FromHeader(header, schema);
        var validator = new RecordValidator(schema, columns, options.Today);

        using var writer = partitions.CreateWriter(side);
        while (true)
        {
            string[] fields;
            try
            {
                if (!reader.TryReadRecord(out fields))
                {
                    break;
                }
            }
            catch (CsvFormatException ex)
            {
                stats.RowsRead++;
                stats.RejectedRows++;
                reports.WriteRejected(schema.Name, ex.Line, null, [ex.Message], []);
                break; // only possible on the final record (unterminated quote at EOF)
            }

            if (fields is [""])
            {
                continue; // blank line
            }

            stats.RowsRead++;
            if (stats.RowsRead % ProgressInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger.LogInformation("{Source}: {Rows:N0} rows validated", schema.Name, stats.RowsRead);
            }

            var result = validator.Validate(fields, reader.RecordLine);
            if (!result.IsValid)
            {
                stats.RejectedRows++;
                var reference = columns.Reference < fields.Length ? fields[columns.Reference] : null;
                reports.WriteRejected(schema.Name, reader.RecordLine, reference, result.Errors, fields);
                continue;
            }

            stats.ValidRows++;
            writer.Write(result.Record!);
        }

        logger.LogInformation("{Source}: {Rows:N0} rows read, {Valid:N0} valid, {Rejected:N0} rejected",
            schema.Name, stats.RowsRead, stats.ValidRows, stats.RejectedRows);
    }

    private void ReconcilePartition(int partition, PartitionStore partitions, ReportWriter reports, ReconciliationSummary summary)
    {
        // Build side: B. A list keeps B's original order for the Missing_In_A output.
        var bRecords = new List<TransactionRecord>();
        var bIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in partitions.Read(PartitionStore.SideB, partition))
        {
            if (bIndex.TryGetValue(b.Reference, out var first))
            {
                summary.ListB.DuplicateReferences++;
                reports.WriteDuplicate(options.SchemaB.Name, b, bRecords[first].SourceLine);
                continue;
            }

            bIndex.Add(b.Reference, bRecords.Count);
            bRecords.Add(b);
            summary.ListB.TotalAmount += b.Amount;
            summary.ListB.TotalNet += b.NetTotal;
        }

        var bMatched = new bool[bRecords.Count];
        var aSeen = new Dictionary<string, long>(StringComparer.Ordinal);

        // Probe side: A, streamed.
        foreach (var a in partitions.Read(PartitionStore.SideA, partition))
        {
            if (!aSeen.TryAdd(a.Reference, a.SourceLine))
            {
                summary.ListA.DuplicateReferences++;
                reports.WriteDuplicate(options.SchemaA.Name, a, aSeen[a.Reference]);
                continue;
            }

            summary.ListA.TotalAmount += a.Amount;
            summary.ListA.TotalNet += a.NetTotal;

            if (bIndex.TryGetValue(a.Reference, out var bPosition))
            {
                bMatched[bPosition] = true;
                var b = bRecords[bPosition];
                var discrepancies = RecordComparer.Compare(a, b);
                reports.WriteMatched(a, b, discrepancies);
                (discrepancies.Count == 0 ? summary.MatchedExact : summary.MatchedWithDiscrepancies).Add(a.Amount);
            }
            else
            {
                reports.WriteMissingInB(a);
                summary.MissingInB.Add(a.Amount);
            }
        }

        for (var i = 0; i < bRecords.Count; i++)
        {
            if (!bMatched[i])
            {
                reports.WriteMissingInA(bRecords[i]);
                summary.MissingInA.Add(bRecords[i].Amount);
            }
        }
    }

    private static void EnsureInputExists(string path, SourceSchema schema)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{schema.Name} input file not found: {Path.GetFullPath(path)}", path);
        }
    }
}

public static class RecordComparer
{
    /// <summary>Names of the fields whose values differ between the two sides of a match.</summary>
    public static IReadOnlyList<string> Compare(TransactionRecord a, TransactionRecord b)
    {
        var differences = new List<string>(0);
        if (a.TransactionDate != b.TransactionDate)
        {
            differences.Add(SourceSchema.TransactionDateColumn);
        }

        if (a.Amount != b.Amount)
        {
            differences.Add(SourceSchema.AmountColumn);
        }

        if (a.Fees1 != b.Fees1)
        {
            differences.Add(SourceSchema.Fees1Column);
        }

        if (a.Fees2 != b.Fees2)
        {
            differences.Add(SourceSchema.Fees2Column);
        }

        if (a.NetTotal != b.NetTotal)
        {
            differences.Add(SourceSchema.NetTotalColumn);
        }

        if (!string.Equals(a.Status, b.Status, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add(SourceSchema.StatusColumn);
        }

        return differences;
    }
}
