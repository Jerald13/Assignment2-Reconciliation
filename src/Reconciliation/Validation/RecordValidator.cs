using System.Globalization;
using System.Text.RegularExpressions;
using Reconciliation.Model;

namespace Reconciliation.Validation;

public sealed record ValidationResult(TransactionRecord? Record, IReadOnlyList<string> Errors)
{
    public bool IsValid => Record is not null;
}

/// <summary>
/// Validates and normalises one CSV row. All problems in a row are collected (not just the first),
/// so a rejects report tells the data owner everything that needs fixing in one pass.
/// </summary>
public sealed partial class RecordValidator
{
    public static readonly DateOnly MinTransactionDate = new(2000, 1, 1);
    public const decimal MaxAmount = 1_000_000_000m;

    private static readonly HashSet<string> KnownStatuses = new(StringComparer.OrdinalIgnoreCase) { "Success", "Failed", "Pending" };

    private readonly SourceSchema _schema;
    private readonly ColumnMap _columns;
    private readonly DateOnly _maxTransactionDate;

    /// <param name="today">Injected so validation is deterministic in tests. Dates later than today + 1 day are rejected (1 day of slack for time zones).</param>
    public RecordValidator(SourceSchema schema, ColumnMap columns, DateOnly today)
    {
        _schema = schema;
        _columns = columns;
        _maxTransactionDate = today.AddDays(1);
    }

    public ValidationResult Validate(string[] fields, long line)
    {
        var errors = new List<string>();

        if (fields.Length != _columns.ColumnCount)
        {
            errors.Add($"Expected {_columns.ColumnCount} columns but found {fields.Length}.");
            return new ValidationResult(null, errors);
        }

        var reference = fields[_columns.Reference].Trim();
        if (reference.Length == 0)
        {
            errors.Add($"{_schema.ReferenceColumn} is required.");
        }
        else if (!ReferenceRegex().IsMatch(reference))
        {
            errors.Add($"{_schema.ReferenceColumn} must be 1-50 characters of letters, digits, '-', '_' or '/'.");
        }

        var rowNumber = _columns.RowNumber >= 0 ? fields[_columns.RowNumber].Trim() : string.Empty;
        if (rowNumber.Length > 0 && !(long.TryParse(rowNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0))
        {
            errors.Add("# must be a positive integer.");
        }

        var date = ParseDate(fields[_columns.TransactionDate].Trim(), errors);
        var amount = ParseMoney(SourceSchema.AmountColumn, fields[_columns.Amount], errors);
        var fees1 = ParseMoney(SourceSchema.Fees1Column, fields[_columns.Fees1], errors);
        var fees2 = ParseMoney(SourceSchema.Fees2Column, fields[_columns.Fees2], errors);
        var netTotal = ParseMoney(SourceSchema.NetTotalColumn, fields[_columns.NetTotal], errors);

        if (amount is { } a && (a <= 0 || a > MaxAmount))
        {
            errors.Add($"Amount must be greater than 0 and at most {MaxAmount:N0}.");
        }

        CheckFee(SourceSchema.Fees1Column, fees1, amount, errors);
        CheckFee(SourceSchema.Fees2Column, fees2, amount, errors);

        if (amount is { } gross && fees1 is { } f1 && fees2 is { } f2 && netTotal is { } net && gross + f1 + f2 != net)
        {
            errors.Add($"Net Total {net.ToString(CultureInfo.InvariantCulture)} does not equal Amount + Fees1 + Fees2 " +
                       $"({(gross + f1 + f2).ToString(CultureInfo.InvariantCulture)}).");
        }

        var status = fields[_columns.Status].Trim();
        if (!KnownStatuses.TryGetValue(status, out var normalisedStatus))
        {
            errors.Add($"Status must be one of: {string.Join(", ", KnownStatuses)}.");
        }

        string? cardNumber = null;
        if (_schema.HasCardNumber)
        {
            cardNumber = fields[_columns.CardNumber].Trim();
            if (!MaskedCardRegex().IsMatch(cardNumber))
            {
                errors.Add(UnmaskedCardRegex().IsMatch(cardNumber)
                    ? "Card Number is NOT masked - full card numbers must never appear in settlement files (PCI DSS)."
                    : "Card Number must be masked, e.g. 555555****1111.");
            }
        }

        if (errors.Count > 0)
        {
            return new ValidationResult(null, errors);
        }

        return new ValidationResult(
            new TransactionRecord(reference, line, rowNumber, date!.Value, amount!.Value, fees1!.Value, fees2!.Value, netTotal!.Value, normalisedStatus!, cardNumber),
            errors);
    }

    /// <summary>Replaces anything that looks like a full card number so rejected rows can be written out safely.</summary>
    public static string MaskPotentialCardNumbers(string value)
        => UnmaskedCardSearchRegex().Replace(value, m => string.Concat(m.Value.AsSpan(0, 6), new string('*', m.Length - 10), m.Value.AsSpan(m.Length - 4)));

    private DateOnly? ParseDate(string value, List<string> errors)
    {
        if (!DateOnly.TryParseExact(value, _schema.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            errors.Add($"Transaction Date must be a valid date in format {_schema.DateFormat}.");
            return null;
        }

        if (date < MinTransactionDate || date > _maxTransactionDate)
        {
            errors.Add($"Transaction Date must be between {MinTransactionDate:yyyy-MM-dd} and {_maxTransactionDate:yyyy-MM-dd}.");
            return null;
        }

        return date;
    }

    private static decimal? ParseMoney(string column, string raw, List<string> errors)
    {
        var value = raw.Trim();
        if (value.Length == 0)
        {
            errors.Add($"{column} is required.");
            return null;
        }

        if (!MoneyRegex().IsMatch(value)
            || !decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount))
        {
            errors.Add($"{column} must be a number with at most 2 decimal places (e.g. 1,234.50).");
            return null;
        }

        return amount;
    }

    private static void CheckFee(string column, decimal? fee, decimal? amount, List<string> errors)
    {
        if (fee is not { } f)
        {
            return;
        }

        if (f > 0)
        {
            errors.Add($"{column} must be zero or negative (fees are deductions).");
        }
        else if (amount is { } a && a > 0 && -f > a)
        {
            errors.Add($"{column} must not exceed the Amount.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-/]{1,50}$")]
    private static partial Regex ReferenceRegex();

    // Optional sign, digits with optional correctly placed thousands separators, up to 2 decimals.
    [GeneratedRegex(@"^-?(\d{1,3}(,\d{3})+|\d+)(\.\d{1,2})?$")]
    private static partial Regex MoneyRegex();

    [GeneratedRegex(@"^(\d{6}|\d{4}|)\*{2,12}\d{4}$")]
    private static partial Regex MaskedCardRegex();

    [GeneratedRegex(@"^\d{13,19}$")]
    private static partial Regex UnmaskedCardRegex();

    [GeneratedRegex(@"(?<!\d)\d{13,19}(?!\d)")]
    private static partial Regex UnmaskedCardSearchRegex();
}
