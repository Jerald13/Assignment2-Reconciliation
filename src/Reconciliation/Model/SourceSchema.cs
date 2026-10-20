namespace Reconciliation.Model;

/// <summary>
/// Describes one input file: which column is the unique reference, how dates are written,
/// and whether a card number column is expected. Columns are located by header name, not position,
/// so reordered or extra columns do not break the import.
/// </summary>
public sealed record SourceSchema(
    string Name,
    string ReferenceColumn,
    string DateFormat,
    bool HasCardNumber)
{
    public const string RowNumberColumn = "#";
    public const string TransactionDateColumn = "Transaction Date";
    public const string AmountColumn = "Amount";
    public const string Fees1Column = "Fees1";
    public const string Fees2Column = "Fees2";
    public const string NetTotalColumn = "Net Total";
    public const string CardNumberColumn = "Card Number";
    public const string StatusColumn = "Status";

    public static SourceSchema ListA(string dateFormat = "yyyy-MM-dd") => new("List A", "Order Number", dateFormat, HasCardNumber: false);

    public static SourceSchema ListB(string dateFormat = "dd-MM-yyyy") => new("List B", "Invoice Number", dateFormat, HasCardNumber: true);

    public IEnumerable<string> RequiredColumns()
    {
        yield return ReferenceColumn;
        yield return TransactionDateColumn;
        yield return AmountColumn;
        yield return Fees1Column;
        yield return Fees2Column;
        yield return NetTotalColumn;
        yield return StatusColumn;
        if (HasCardNumber)
        {
            yield return CardNumberColumn;
        }
    }
}

/// <summary>Resolved column positions for a particular file's header row.</summary>
public sealed class ColumnMap
{
    private ColumnMap(int columnCount, int reference, int rowNumber, int date, int amount, int fees1, int fees2, int netTotal, int status, int cardNumber)
    {
        ColumnCount = columnCount;
        Reference = reference;
        RowNumber = rowNumber;
        TransactionDate = date;
        Amount = amount;
        Fees1 = fees1;
        Fees2 = fees2;
        NetTotal = netTotal;
        Status = status;
        CardNumber = cardNumber;
    }

    public int ColumnCount { get; }
    public int Reference { get; }
    public int RowNumber { get; }
    public int TransactionDate { get; }
    public int Amount { get; }
    public int Fees1 { get; }
    public int Fees2 { get; }
    public int NetTotal { get; }
    public int Status { get; }
    public int CardNumber { get; }

    /// <exception cref="SchemaException">A required column is missing or duplicated.</exception>
    public static ColumnMap FromHeader(string[] header, SourceSchema schema)
    {
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            var name = header[i].Trim().TrimStart('﻿');
            if (name.Length > 0 && !positions.TryAdd(name, i))
            {
                throw new SchemaException($"{schema.Name}: column '{name}' appears more than once in the header.");
            }
        }

        var missing = schema.RequiredColumns().Where(c => !positions.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            throw new SchemaException(
                $"{schema.Name}: missing required column(s): {string.Join(", ", missing.Select(m => $"'{m}'"))}. " +
                $"Found: {string.Join(", ", header.Select(h => $"'{h}'"))}.");
        }

        int Find(string column) => positions.TryGetValue(column, out var index) ? index : -1;

        return new ColumnMap(
            header.Length,
            Find(schema.ReferenceColumn),
            Find(SourceSchema.RowNumberColumn),
            Find(SourceSchema.TransactionDateColumn),
            Find(SourceSchema.AmountColumn),
            Find(SourceSchema.Fees1Column),
            Find(SourceSchema.Fees2Column),
            Find(SourceSchema.NetTotalColumn),
            Find(SourceSchema.StatusColumn),
            schema.HasCardNumber ? Find(SourceSchema.CardNumberColumn) : -1);
    }
}

/// <summary>The file as a whole is unusable (bad header) - processing cannot continue.</summary>
public sealed class SchemaException(string message) : Exception(message);
