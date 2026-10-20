using Microsoft.Extensions.Logging.Abstractions;
using Reconciliation.Csv;
using Reconciliation.Generation;
using Reconciliation.Model;
using Reconciliation.Processing;

namespace Reconciliation.Tests;

public sealed class ReconciliationEngineTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 9);
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"recon-tests-{Guid.NewGuid():N}");

    public ReconciliationEngineTests() => Directory.CreateDirectory(_workDir);

    public void Dispose() => Directory.Delete(_workDir, recursive: true);

    private ReconciliationSummary Run(string listA, string listB, int? partitions = null, string outputName = "out")
    {
        var options = new ReconciliationOptions
        {
            ListAPath = listA,
            ListBPath = listB,
            OutputDirectory = Path.Combine(_workDir, outputName),
            PartitionCount = partitions,
            TempDirectory = _workDir,
            Today = Today,
        };
        return new ReconciliationEngine(options, NullLogger<ReconciliationEngine>.Instance).Run();
    }

    private List<string[]> ReadOutput(string file, string outputName = "out")
    {
        using var reader = CsvReader.Open(Path.Combine(_workDir, outputName, file));
        var rows = new List<string[]>();
        reader.TryReadRecord(out _); // header
        while (reader.TryReadRecord(out var row))
        {
            rows.Add(row);
        }

        return rows;
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_workDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Provided_sample_files_reconcile_as_expected()
    {
        var summary = Run(Path.Combine("data", "ListA.csv"), Path.Combine("data", "ListB.csv"));

        Assert.Equal(24, summary.ListA.ValidRows);
        Assert.Equal(24, summary.ListB.ValidRows);
        Assert.Equal(17, summary.MatchedTotal);
        Assert.Equal(1, summary.MatchedWithDiscrepancies.Count);
        Assert.Equal(7, summary.MissingInB.Count);
        Assert.Equal(7, summary.MissingInA.Count);

        var discrepancy = Assert.Single(ReadOutput(ReportWriter.MatchedFile), r => r[1] == "DISCREPANCY");
        Assert.Equal("2696123", discrepancy[0]);
        Assert.Equal("Amount; Net Total", discrepancy[2]);

        Assert.Equal(
            ["2696128", "2696129", "2696130", "2696131", "2696132", "2696133", "2696134"],
            ReadOutput(ReportWriter.MissingInBFile).Select(r => r[0]));
        Assert.Equal(
            ["99999", "22025", "12479", "1111", "47552", "7841483", "7782247"],
            ReadOutput(ReportWriter.MissingInAFile).Select(r => r[0]));
    }

    [Fact]
    public void Result_is_the_same_regardless_of_partition_count()
    {
        var data = SampleDataGenerator.Generate(_workDir, rows: 5_000, overlap: 0.8);

        var single = Run(data.ListAPath, data.ListBPath, partitions: 1, outputName: "p1");
        var many = Run(data.ListAPath, data.ListBPath, partitions: 37, outputName: "p37");

        Assert.Equal(4_000, single.MatchedTotal);
        Assert.Equal(1_000, single.MissingInA.Count);
        Assert.Equal(1_000, single.MissingInB.Count);
        Assert.True(single.MatchedWithDiscrepancies.Count > 0);

        foreach (var file in new[] { ReportWriter.MatchedFile, ReportWriter.MissingInAFile, ReportWriter.MissingInBFile })
        {
            var expected = ReadOutput(file, "p1").Select(r => string.Join(",", r)).Order().ToList();
            var actual = ReadOutput(file, "p37").Select(r => string.Join(",", r)).Order().ToList();
            Assert.Equal(expected, actual);
        }

        Assert.Equal(single.MatchedWithDiscrepancies.Count, many.MatchedWithDiscrepancies.Count);
        Assert.Equal(single.MatchedExact.TotalAmount, many.MatchedExact.TotalAmount);
    }

    [Fact]
    public void Invalid_rows_are_rejected_and_reported_without_stopping_the_run()
    {
        var listA = WriteFile("a.csv", """
            #,Order Number,Transaction Date,Amount,Fees1,Fees2,Net Total,Status
            1,100,2025-04-16,10.00,-0.5,-0.5,9.00,Success
            2,101,16/04/2025,10.00,-0.5,-0.5,9.00,Success
            3,102,2025-04-16,ten,-0.5,-0.5,9.00,Success
            4,103,2025-04-16,10.00,-0.5,-0.5,9.00,Success
            """);
        var listB = WriteFile("b.csv", """
            #,Invoice Number,Transaction Date,Amount,Fees1,Fees2,Net Total,Card Number,Status
            1,100,16-04-2025,10.00,-0.5,-0.5,9.00,555555****1111,Success
            2,103,16-04-2025,10.00,-0.5,-0.5,9.00,4111111111111111,Success
            """);

        var summary = Run(listA, listB);

        Assert.Equal(2, summary.ListA.RejectedRows);
        Assert.Equal(1, summary.ListB.RejectedRows);
        Assert.Equal(1, summary.MatchedTotal);
        Assert.Equal(1, summary.MissingInB.Count); // 103 - its B row was rejected

        var rejected = ReadOutput(ReportWriter.RejectedFile);
        Assert.Equal(3, rejected.Count);
        Assert.Contains(rejected, r => r[1] == "3" && r[3].Contains("Transaction Date"));
        Assert.DoesNotContain("4111111111111111", File.ReadAllText(Path.Combine(_workDir, "out", ReportWriter.RejectedFile)));
    }

    [Fact]
    public void Duplicate_references_are_reported_and_first_occurrence_wins()
    {
        var listA = WriteFile("a.csv", """
            #,Order Number,Transaction Date,Amount,Fees1,Fees2,Net Total,Status
            1,100,2025-04-16,10.00,-0.5,-0.5,9.00,Success
            2,100,2025-04-16,99.00,-0.5,-0.5,98.00,Success
            """);
        var listB = WriteFile("b.csv", """
            #,Invoice Number,Transaction Date,Amount,Fees1,Fees2,Net Total,Card Number,Status
            1,100,16-04-2025,10.00,-0.5,-0.5,9.00,555555****1111,Success
            """);

        var summary = Run(listA, listB);

        Assert.Equal(1, summary.ListA.DuplicateReferences);
        Assert.Equal(1, summary.MatchedExact.Count);
        var duplicate = Assert.Single(ReadOutput(ReportWriter.DuplicatesFile));
        Assert.Equal(["List A", "100", "3", "2"], duplicate[..4]);
    }

    [Fact]
    public void Missing_required_column_fails_and_leaves_no_partial_output()
    {
        var listA = WriteFile("a.csv", "#,Order Number,Amount\n1,100,10.00\n");
        var listB = WriteFile("b.csv", "#,Invoice Number,Transaction Date,Amount,Fees1,Fees2,Net Total,Card Number,Status\n");

        Assert.Throws<SchemaException>(() => Run(listA, listB));
        Assert.Empty(Directory.GetFiles(Path.Combine(_workDir, "out")));
    }

    [Fact]
    public void Missing_input_file_is_reported()
        => Assert.Throws<FileNotFoundException>(() => Run(Path.Combine(_workDir, "nope.csv"), Path.Combine(_workDir, "nope2.csv")));

    [Fact]
    public void Temporary_partition_files_are_cleaned_up()
    {
        Run(Path.Combine("data", "ListA.csv"), Path.Combine("data", "ListB.csv"), partitions: 8);

        Assert.Empty(Directory.GetDirectories(_workDir, "reconciliation-*"));
    }
}
