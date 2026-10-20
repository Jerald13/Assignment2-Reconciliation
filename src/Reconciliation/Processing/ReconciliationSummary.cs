namespace Reconciliation.Processing;

public sealed class SourceStatistics
{
    public required string Name { get; init; }

    public required string File { get; init; }

    public long RowsRead { get; set; }

    public long ValidRows { get; set; }

    public long RejectedRows { get; set; }

    public long DuplicateReferences { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal TotalNet { get; set; }
}

public sealed class CategoryStatistics
{
    public long Count { get; set; }

    public decimal TotalAmount { get; set; }

    public void Add(decimal amount)
    {
        Count++;
        TotalAmount += amount;
    }
}

public sealed class ReconciliationSummary
{
    public required SourceStatistics ListA { get; init; }

    public required SourceStatistics ListB { get; init; }

    /// <summary>Reference in both lists and every compared field agrees.</summary>
    public CategoryStatistics MatchedExact { get; } = new();

    /// <summary>Reference in both lists but at least one field (amount, fees, status...) differs.</summary>
    public CategoryStatistics MatchedWithDiscrepancies { get; } = new();

    public CategoryStatistics MissingInB { get; } = new();

    public CategoryStatistics MissingInA { get; } = new();

    public int Partitions { get; set; }

    public double ElapsedSeconds { get; set; }

    public double PeakWorkingSetMb { get; set; }

    public string OutputDirectory { get; set; } = string.Empty;

    public long MatchedTotal => MatchedExact.Count + MatchedWithDiscrepancies.Count;

    public bool HasIssues =>
        MatchedWithDiscrepancies.Count + MissingInA.Count + MissingInB.Count
        + ListA.RejectedRows + ListB.RejectedRows + ListA.DuplicateReferences + ListB.DuplicateReferences > 0;
}
