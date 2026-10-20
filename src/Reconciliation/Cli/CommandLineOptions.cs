using System.Globalization;

namespace Reconciliation.Cli;

public enum CliCommand
{
    Reconcile,
    Generate,
}

/// <summary>Parses the command line. Throws <see cref="ArgumentException"/> with a user-facing message on bad input.</summary>
public sealed class CommandLineOptions
{
    public const string Usage = """
        Usage:
          reconcile --list-a <file> --list-b <file> [options]
          reconcile generate --rows <n> [--output <dir>]

        Reconcile options:
          --list-a <file>          List A CSV (unique reference column: "Order Number")
          --list-b <file>          List B CSV (unique reference column: "Invoice Number")
          --output <dir>           Output directory (default: ./output)
          --date-format-a <fmt>    Date format in List A (default: yyyy-MM-dd)
          --date-format-b <fmt>    Date format in List B (default: dd-MM-yyyy)
          --partitions <n>         Number of hash partitions (default: derived from file size, ~4 MB each)
          --temp-dir <dir>         Where partition files are spilled (default: system temp)
          -h, --help               Show this help

        Generate options (synthetic data for load testing):
          --rows <n>               Rows per list
          --output <dir>           Output directory (default: ./output)

        Exit codes: 0 success, 1 unexpected error, 2 invalid arguments / missing file, 3 invalid input file, 130 cancelled.
        """;

    public CliCommand Command { get; private set; } = CliCommand.Reconcile;

    public string? ListAPath { get; private set; }

    public string? ListBPath { get; private set; }

    public string OutputDirectory { get; private set; } = "output";

    public string DateFormatA { get; private set; } = "yyyy-MM-dd";

    public string DateFormatB { get; private set; } = "dd-MM-yyyy";

    public int? Partitions { get; private set; }

    public string? TempDirectory { get; private set; }

    public long GenerateRows { get; private set; }

    public bool ShowHelp { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();
        var i = 0;

        if (args.Length > 0 && args[0].Equals("generate", StringComparison.OrdinalIgnoreCase))
        {
            options.Command = CliCommand.Generate;
            i = 1;
        }

        for (; i < args.Length; i++)
        {
            var arg = args[i];
            string Value() => i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : throw new ArgumentException($"Option {arg} requires a value.");

            switch (arg)
            {
                case "-h" or "--help":
                    options.ShowHelp = true;
                    break;
                case "--list-a":
                    options.ListAPath = Value();
                    break;
                case "--list-b":
                    options.ListBPath = Value();
                    break;
                case "--output":
                    options.OutputDirectory = Value();
                    break;
                case "--date-format-a":
                    options.DateFormatA = Value();
                    break;
                case "--date-format-b":
                    options.DateFormatB = Value();
                    break;
                case "--temp-dir":
                    options.TempDirectory = Value();
                    break;
                case "--partitions":
                    options.Partitions = ParsePositive(arg, Value(), max: 4096);
                    break;
                case "--rows":
                    options.GenerateRows = ParsePositive(arg, Value(), max: 100_000_000);
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{arg}'.");
            }
        }

        if (options.ShowHelp)
        {
            return options;
        }

        if (options.Command == CliCommand.Reconcile && (options.ListAPath is null || options.ListBPath is null))
        {
            throw new ArgumentException("Both --list-a and --list-b are required.");
        }

        if (options.Command == CliCommand.Generate && options.GenerateRows <= 0)
        {
            throw new ArgumentException("generate requires --rows <n>.");
        }

        return options;
    }

    private static int ParsePositive(string option, string value, int max)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is > 0 && n <= max
            ? n
            : throw new ArgumentException($"{option} must be an integer between 1 and {max}.");
}
