namespace Reconciliation.Model;

/// <summary>
/// One validated, normalised transaction from either list.
/// </summary>
/// <param name="Reference">The unique reference: Order Number (List A) or Invoice Number (List B).</param>
/// <param name="SourceLine">Line number in the source file, for traceability.</param>
/// <param name="RowNumber">The "#" column as supplied.</param>
/// <param name="CardNumber">Masked card number (List B only).</param>
public sealed record TransactionRecord(
    string Reference,
    long SourceLine,
    string RowNumber,
    DateOnly TransactionDate,
    decimal Amount,
    decimal Fees1,
    decimal Fees2,
    decimal NetTotal,
    string Status,
    string? CardNumber);
