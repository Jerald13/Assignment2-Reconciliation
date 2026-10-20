using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Reconciliation.Cli;
using Reconciliation.Csv;
using Reconciliation.Generation;
using Reconciliation.Model;
using Reconciliation.Processing;

namespace Reconciliation;

public static class Program
{
    public const int ExitSuccess = 0;
    public const int ExitUnexpectedError = 1;
    public const int ExitInvalidArguments = 2;
    public const int ExitInvalidInput = 3;
    public const int ExitCancelled = 130;

    public static int Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            }));
        var logger = loggerFactory.CreateLogger("Reconciliation");

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
            logger.LogWarning("Cancellation requested - stopping and cleaning up temporary files...");
        };

        CommandLineOptions options;
        try
        {
            options = CommandLineOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLineOptions.Usage);
            return ExitInvalidArguments;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(CommandLineOptions.Usage);
            return ExitSuccess;
        }

        try
        {
            return options.Command == CliCommand.Generate
                ? Generate(options, logger)
                : Reconcile(options, loggerFactory, cancellation.Token);
        }
        catch (FileNotFoundException ex)
        {
            logger.LogError("{Message}", ex.Message);
            return ExitInvalidArguments;
        }
        catch (SchemaException ex)
        {
            logger.LogError("Invalid input file. {Message}", ex.Message);
            return ExitInvalidInput;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Reconciliation cancelled. No output files were produced.");
            return ExitCancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError("File system error: {Message}", ex.Message);
            return ExitUnexpectedError;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Unexpected error");
            return ExitUnexpectedError;
        }
    }

    private static int Reconcile(CommandLineOptions cli, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var options = new ReconciliationOptions
        {
            ListAPath = cli.ListAPath!,
            ListBPath = cli.ListBPath!,
            OutputDirectory = cli.OutputDirectory,
            SchemaA = SourceSchema.ListA(cli.DateFormatA),
            SchemaB = SourceSchema.ListB(cli.DateFormatB),
            PartitionCount = cli.Partitions,
            TempDirectory = cli.TempDirectory ?? Path.GetTempPath(),
        };

        var engine = new ReconciliationEngine(options, loggerFactory.CreateLogger<ReconciliationEngine>());
        var summary = engine.Run(cancellationToken);

        var summaryPath = Path.Combine(options.OutputDirectory, "Reconciliation_Summary.json");
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine();
        Console.WriteLine(SummaryFormatter.Format(summary));
        return ExitSuccess;
    }

    private static int Generate(CommandLineOptions cli, ILogger logger)
    {
        logger.LogInformation("Generating {Rows:N0} rows per list into {Directory}...", cli.GenerateRows, cli.OutputDirectory);
        var result = SampleDataGenerator.Generate(cli.OutputDirectory, cli.GenerateRows);
        logger.LogInformation("Wrote {ListA} and {ListB} ({Shared:N0} shared references)",
            result.ListAPath, result.ListBPath, result.SharedReferences);
        return ExitSuccess;
    }
}

internal static class SummaryFormatter
{
    public static string Format(ReconciliationSummary s)
    {
        string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);
        string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        return string.Join(Environment.NewLine,
            "==================== RECONCILIATION SUMMARY ====================",
            $"{s.ListA.Name,-8} rows read: {Count(s.ListA.RowsRead),12}   valid: {Count(s.ListA.ValidRows),12}   rejected: {Count(s.ListA.RejectedRows),8}   duplicates: {Count(s.ListA.DuplicateReferences),8}",
            $"{s.ListB.Name,-8} rows read: {Count(s.ListB.RowsRead),12}   valid: {Count(s.ListB.ValidRows),12}   rejected: {Count(s.ListB.RejectedRows),8}   duplicates: {Count(s.ListB.DuplicateReferences),8}",
            "----------------------------------------------------------------",
            $"Matched (exact)               {Count(s.MatchedExact.Count),12}   amount {Money(s.MatchedExact.TotalAmount),20}",
            $"Matched (with discrepancies)  {Count(s.MatchedWithDiscrepancies.Count),12}   amount {Money(s.MatchedWithDiscrepancies.TotalAmount),20}",
            $"Missing in B (only in A)      {Count(s.MissingInB.Count),12}   amount {Money(s.MissingInB.TotalAmount),20}",
            $"Missing in A (only in B)      {Count(s.MissingInA.Count),12}   amount {Money(s.MissingInA.TotalAmount),20}",
            "----------------------------------------------------------------",
            $"Partitions: {s.Partitions}   Elapsed: {s.ElapsedSeconds:0.00}s   Peak working set: {s.PeakWorkingSetMb:0.0} MB",
            $"Output: {s.OutputDirectory}",
            $"  {ReportWriter.MatchedFile}, {ReportWriter.MissingInBFile}, {ReportWriter.MissingInAFile},",
            $"  {ReportWriter.RejectedFile}, {ReportWriter.DuplicatesFile}, Reconciliation_Summary.json",
            s.HasIssues
                ? "Result: DISCREPANCIES FOUND - review the files above."
                : "Result: FULLY RECONCILED.");
    }
}
