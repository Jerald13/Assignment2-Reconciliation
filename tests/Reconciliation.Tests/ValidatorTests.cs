using Reconciliation.Model;
using Reconciliation.Validation;

namespace Reconciliation.Tests;

public sealed class ValidatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);
    private static readonly string[] HeaderB = ["#", "Invoice Number", "Transaction Date", "Amount", "Fees1", "Fees2", "Net Total", "Card Number", "Status"];

    private static RecordValidator ValidatorB()
    {
        var schema = SourceSchema.ListB();
        return new RecordValidator(schema, ColumnMap.FromHeader(HeaderB, schema), Today);
    }

    private static string[] Row(
        string reference = "2696111", string date = "16-04-2025", string amount = "3,900.00", string fees1 = "-0.8",
        string fees2 = "-21.3", string net = "3,877.90", string card = "555555****1111", string status = "Success")
        => ["1", reference, date, amount, fees1, fees2, net, card, status];

    [Fact]
    public void Valid_row_is_normalised()
    {
        var result = ValidatorB().Validate(Row(status: "success"), line: 2);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var record = result.Record!;
        Assert.Equal("2696111", record.Reference);
        Assert.Equal(new DateOnly(2025, 4, 16), record.TransactionDate);
        Assert.Equal(3900.00m, record.Amount);
        Assert.Equal(3877.90m, record.NetTotal);
        Assert.Equal("Success", record.Status);
        Assert.Equal(2, record.SourceLine);
    }

    [Theory]
    [InlineData("2025-04-16")]   // wrong format for List B
    [InlineData("31-02-2025")]   // impossible date
    [InlineData("16-04-1999")]   // too old
    [InlineData("16-04-2030")]   // in the future
    public void Bad_dates_are_rejected(string date)
        => Assert.False(ValidatorB().Validate(Row(date: date), 2).IsValid);

    [Theory]
    [InlineData("abc")]
    [InlineData("3,90.00")]      // misplaced thousands separator
    [InlineData("3900.001")]     // too many decimals
    [InlineData("0")]            // not positive
    [InlineData("-5")]
    [InlineData("")]
    public void Bad_amounts_are_rejected(string amount)
        => Assert.False(ValidatorB().Validate(Row(amount: amount), 2).IsValid);

    [Fact]
    public void Positive_fee_is_rejected()
        => Assert.False(ValidatorB().Validate(Row(fees1: "0.8", net: "3,879.50"), 2).IsValid);

    [Fact]
    public void Net_total_must_equal_amount_plus_fees()
    {
        var result = ValidatorB().Validate(Row(net: "3,877.91"), 2);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Net Total"));
    }

    [Fact]
    public void Unknown_status_is_rejected()
        => Assert.False(ValidatorB().Validate(Row(status: "Maybe"), 2).IsValid);

    [Fact]
    public void Unmasked_card_number_is_rejected_and_masked_in_output()
    {
        var result = ValidatorB().Validate(Row(card: "5555555555551111"), 2);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("NOT masked"));
        Assert.Equal("x,555555******1111,y", RecordValidator.MaskPotentialCardNumbers("x,5555555555551111,y"));
    }

    [Fact]
    public void All_errors_in_a_row_are_reported_together()
    {
        var result = ValidatorB().Validate(Row(reference: "", date: "bad", amount: "x", status: "?"), 2);

        Assert.True(result.Errors.Count >= 4);
    }

    [Fact]
    public void Wrong_column_count_is_rejected()
        => Assert.False(ValidatorB().Validate(["1", "2696111"], 2).IsValid);

    [Fact]
    public void Missing_required_column_fails_the_whole_file()
    {
        var ex = Assert.Throws<SchemaException>(() => ColumnMap.FromHeader(["#", "Order Number", "Amount"], SourceSchema.ListA()));

        Assert.Contains("Transaction Date", ex.Message);
    }

    [Fact]
    public void Columns_are_located_by_name_not_position()
    {
        string[] reordered = ["Status", "Net Total", "Fees2", "Fees1", "Amount", "Transaction Date", "Order Number", "#"];
        var schema = SourceSchema.ListA();
        var validator = new RecordValidator(schema, ColumnMap.FromHeader(reordered, schema), Today);

        var result = validator.Validate(["Success", "507.44", "-11.76", "-0.8", "520.00", "2025-04-16", "2696112", "2"], 2);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal("2696112", result.Record!.Reference);
    }
}
