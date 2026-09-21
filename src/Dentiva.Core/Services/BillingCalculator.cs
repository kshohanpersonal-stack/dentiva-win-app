using Dentiva.Core.Models;

namespace Dentiva.Core.Services;

/// <summary>
/// Pure, deterministic money maths for invoices. Kept free of I/O so it is fully unit-testable.
/// All monetary results are rounded to 2 decimals using away-from-zero (commercial) rounding.
/// </summary>
public static class BillingCalculator
{
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal LineTotal(decimal quantity, decimal unitPrice, decimal discount)
    {
        if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity cannot be negative.");
        if (unitPrice < 0) throw new ArgumentOutOfRangeException(nameof(unitPrice), "Unit price cannot be negative.");
        if (discount < 0) throw new ArgumentOutOfRangeException(nameof(discount), "Discount cannot be negative.");

        var gross = Round(quantity * unitPrice);
        var net = gross - Round(discount);
        return Round(Math.Max(0m, net));
    }

    /// <summary>
    /// Recomputes every derived total on the invoice from its line items.
    /// </summary>
    public static InvoiceTotals Compute(IEnumerable<InvoiceItem> items, decimal discount, string discountType, decimal taxRate, decimal paidAmount)
    {
        var list = items as IList<InvoiceItem> ?? items.ToList();
        decimal subtotal = 0m;
        foreach (var item in list)
        {
            item.LineTotal = LineTotal(item.Quantity, item.UnitPrice, item.Discount);
            subtotal += item.LineTotal;
        }

        subtotal = Round(subtotal);

        if (discount < 0) discount = 0m;
        decimal discountAmount = string.Equals(discountType, "Percent", StringComparison.OrdinalIgnoreCase)
            ? Round(subtotal * Math.Min(discount, 100m) / 100m)
            : Round(discount);

        discountAmount = Math.Min(discountAmount, subtotal);
        var taxable = Round(subtotal - discountAmount);
        if (taxRate < 0) taxRate = 0m;
        var taxAmount = Round(taxable * taxRate / 100m);
        var total = Round(taxable + taxAmount);
        var paid = Round(Math.Max(0m, paidAmount));
        var due = Round(Math.Max(0m, total - paid));

        return new InvoiceTotals(subtotal, discountAmount, taxAmount, total, paid, due, DetermineStatus(total, paid));
    }

    public static string DetermineStatus(decimal total, decimal paid)
    {
        total = Round(total);
        paid = Round(paid);
        if (total <= 0m) return "Cancelled";
        if (paid <= 0m) return "Unpaid";
        if (paid >= total) return "Paid";
        return "Partial";
    }

    /// <summary>
    /// Validates that a payment does not overpay an invoice unless advance credit is explicitly allowed.
    /// </summary>
    public static PaymentValidation ValidatePayment(decimal amount, decimal invoiceTotal, decimal alreadyPaid, bool allowAdvance)
    {
        if (amount <= 0m)
        {
            return new PaymentValidation(false, "The payment amount must be greater than zero.");
        }

        if (amount > 99_999_999m)
        {
            return new PaymentValidation(false, "The payment amount exceeds the supported maximum.");
        }

        var due = Round(invoiceTotal - alreadyPaid);
        if (!allowAdvance && Round(amount) > due)
        {
            return new PaymentValidation(false, $"The payment exceeds the outstanding balance of {due:0.00}. Enable advance payments to accept a larger amount.");
        }

        return new PaymentValidation(true, null);
    }
}

public sealed record InvoiceTotals(
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal Total,
    decimal Paid,
    decimal Due,
    string Status);

public sealed record PaymentValidation(bool IsValid, string? Message);
