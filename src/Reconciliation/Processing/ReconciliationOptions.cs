using Reconciliation.Model;

namespace Reconciliation.Processing;

public sealed record ReconciliationOptions
{
    public required string ListAPath { get; init; }

    public required string ListBPath { get; init; }

    public required string OutputDirectory { get; init; }

    public SourceSchema SchemaA { get; init; } = SourceSchema.ListA();

    public SourceSchema SchemaB { get; init; } = SourceSchema.ListB();

    /// <summary>Fixed number of partitions; when null it is derived from input size.</summary>
    public int? PartitionCount { get; init; }

    /// <summary>
    /// Target size of one partition, measured on the larger input file. Each partition is loaded
    /// into memory on its own, and 1 MB of CSV takes up roughly 7-8 MB of managed heap, so with
    /// 4 MB the peak working set stays around 150 MB however large the inputs are (measured:
    /// 2M rows/134 MB with 96 partitions vs 427 MB with 6).
    /// </summary>
    public long PartitionTargetBytes { get; init; } = 4L * 1024 * 1024;

    public string TempDirectory { get; init; } = Path.GetTempPath();

    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);

    public const int MaxPartitions = 4096;

    public int ResolvePartitionCount()
    {
        if (PartitionCount is { } fixedCount)
        {
            return Math.Clamp(fixedCount, 1, MaxPartitions);
        }

        var largest = Math.Max(new FileInfo(ListAPath).Length, new FileInfo(ListBPath).Length);
        var needed = (int)Math.Ceiling(largest / (double)PartitionTargetBytes);
        return Math.Clamp(needed, 1, MaxPartitions);
    }
}
