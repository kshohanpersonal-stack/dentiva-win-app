using Dentiva.Core.Models;
using Dentiva.Core.Services;
using Xunit;

namespace Dentiva.Tests;

public class ValidationTests
{
    [Theory]
    [InlineData("01712345678")]
    [InlineData("+8801712345678")]
    [InlineData("+880 1712-345678")]
    [InlineData("02-9612345")]
    [InlineData("+14155552671")]
    public void ValidatePhone_AcceptsBangladeshiAndInternationalNumbers(string phone)
        => Assert.True(Validation.ValidatePhone(phone).IsValid, phone);

    [Theory]
    [InlineData("abc")]
    [InlineData("12")]
    [InlineData("++8801712345678")]
    [InlineData("017123456789012345")]
    public void ValidatePhone_RejectsInvalidNumbers(string phone)
        => Assert.False(Validation.ValidatePhone(phone).IsValid, phone);

    [Fact]
    public void ValidatePhone_AllowsEmptyOnlyWhenOptional()
    {
        Assert.True(Validation.ValidatePhone(null).IsValid);
        Assert.True(Validation.ValidatePhone("").IsValid);
        Assert.False(Validation.ValidatePhone("", required: true).IsValid);
    }

    [Theory]
    [InlineData("doctor@clinic.com")]
    [InlineData("first.last@sub.example.co.uk")]
    public void ValidateEmail_AcceptsValidAddresses(string email)
        => Assert.True(Validation.ValidateEmail(email).IsValid, email);

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing@domain")]
    [InlineData("@nouser.com")]
    [InlineData("spaces in@email.com")]
    public void ValidateEmail_RejectsInvalidAddresses(string email)
        => Assert.False(Validation.ValidateEmail(email).IsValid, email);

    [Fact]
    public void ValidateFullName_RejectsEmptyAndOverlongNames()
    {
        Assert.False(Validation.ValidateFullName("").IsValid);
        Assert.False(Validation.ValidateFullName("   ").IsValid);
        Assert.False(Validation.ValidateFullName("A").IsValid);
        Assert.False(Validation.ValidateFullName(new string('x', Validation.MaxNameLength + 1)).IsValid);
    }

    [Fact]
    public void ValidateFullName_AcceptsBanglaUnicodeNames()
        => Assert.True(Validation.ValidateFullName("মোহাম্মদ রফিকুল ইসলাম").IsValid);

    [Fact]
    public void ValidateDateOfBirth_RejectsFutureAndPre1900Dates()
    {
        Assert.False(Validation.ValidateDateOfBirth(DateTime.Today.AddDays(1)).IsValid);
        Assert.False(Validation.ValidateDateOfBirth(new DateTime(1800, 1, 1)).IsValid);
        Assert.True(Validation.ValidateDateOfBirth(new DateTime(1990, 5, 20)).IsValid);
        Assert.True(Validation.ValidateDateOfBirth(null).IsValid);
    }

    [Fact]
    public void ValidateAmount_EnforcesBounds()
    {
        Assert.False(Validation.ValidateAmount(-1).IsValid);
        Assert.False(Validation.ValidateAmount(0, allowZero: false).IsValid);
        Assert.False(Validation.ValidateAmount(500_000_000m).IsValid);
        Assert.True(Validation.ValidateAmount(1500).IsValid);
        Assert.True(Validation.ValidateAmount(0).IsValid);
    }

    [Fact]
    public void ValidateNotes_RejectsBeyondMaximumLength()
    {
        Assert.True(Validation.ValidateNotes(new string('n', Validation.MaxNotesLength)).IsValid);
        Assert.False(Validation.ValidateNotes(new string('n', Validation.MaxNotesLength + 1)).IsValid);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("report:2024?.pdf", "report_2024_.pdf")]
    [InlineData("", "document")]
    [InlineData("   ", "document")]
    public void SanitiseFileName_StripsTraversalAndIllegalCharacters(string input, string expected)
        => Assert.Equal(expected, Validation.SanitiseFileName(input));

    [Fact]
    public void SanitiseFileName_TruncatesVeryLongNames()
        => Assert.True(Validation.SanitiseFileName(new string('a', 400) + ".pdf").Length <= 120);

    [Fact]
    public void SanitiseFileName_UsesSuppliedFallback()
        => Assert.Equal("invoice.pdf", Validation.SanitiseFileName(null, "invoice.pdf"));

    [Theory]
    [InlineData("2024-03-15")]
    [InlineData("15/03/2024")]
    [InlineData("15 Mar 2024")]
    [InlineData("2024/03/15")]
    public void TryParseDate_HandlesCommonFormats(string text)
        => Assert.True(Validation.TryParseDate(text, out _), text);

    [Fact]
    public void TryParseDate_RejectsGarbage()
    {
        Assert.False(Validation.TryParseDate("not a date", out _));
        Assert.False(Validation.TryParseDate("", out _));
        Assert.False(Validation.TryParseDate(null, out _));
        Assert.False(Validation.TryParseDate("32/13/2024", out _));
    }

    [Theory]
    [InlineData("1,500.50", 1500.50)]
    [InlineData("৳2000", 2000)]
    [InlineData("BDT 350.25", 350.25)]
    [InlineData("0", 0)]
    public void TryParseAmount_HandlesCurrencyDecoration(string text, decimal expected)
    {
        Assert.True(Validation.TryParseAmount(text, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryParseAmount_RejectsNonNumericText()
    {
        Assert.False(Validation.TryParseAmount("free of charge", out _));
        Assert.False(Validation.TryParseAmount("", out _));
    }

    [Fact]
    public void NormalisePhone_StripsFormattingButKeepsCountryCode()
    {
        Assert.Equal("+8801712345678", Validation.NormalisePhone("+880 1712-345678"));
        Assert.Equal("01712345678", Validation.NormalisePhone("0171 234 5678"));
        Assert.Equal(string.Empty, Validation.NormalisePhone(null));
    }
}

public class BillingCalculatorTests
{
    [Theory]
    [InlineData(1, 500, 0, 500)]
    [InlineData(2, 500, 100, 900)]
    [InlineData(3, 333.33, 0, 999.99)]
    [InlineData(1, 0, 0, 0)]
    public void LineTotal_ComputesNetAmount(decimal qty, decimal price, decimal discount, decimal expected)
        => Assert.Equal(expected, BillingCalculator.LineTotal(qty, price, discount));

    [Fact]
    public void LineTotal_NeverReturnsNegative_WhenDiscountExceedsValue()
        => Assert.Equal(0m, BillingCalculator.LineTotal(1, 100, 500));

    [Theory]
    [InlineData(-1, 100, 0)]
    [InlineData(1, -100, 0)]
    [InlineData(1, 100, -5)]
    public void LineTotal_RejectsNegativeInputs(decimal qty, decimal price, decimal discount)
        => Assert.Throws<ArgumentOutOfRangeException>(() => BillingCalculator.LineTotal(qty, price, discount));

    [Fact]
    public void Compute_AppliesAmountDiscountThenTax()
    {
        var items = new List<InvoiceItem>
        {
            new() { Quantity = 1, UnitPrice = 1000 },
            new() { Quantity = 2, UnitPrice = 500 }
        };

        var totals = BillingCalculator.Compute(items, discount: 200, discountType: "Amount", taxRate: 10, paidAmount: 0);

        Assert.Equal(2000m, totals.Subtotal);
        Assert.Equal(200m, totals.DiscountAmount);
        Assert.Equal(180m, totals.TaxAmount);
        Assert.Equal(1980m, totals.Total);
        Assert.Equal(1980m, totals.Due);
        Assert.Equal("Unpaid", totals.Status);
    }

    [Fact]
    public void Compute_AppliesPercentDiscount()
    {
        var items = new List<InvoiceItem> { new() { Quantity = 1, UnitPrice = 2000 } };
        var totals = BillingCalculator.Compute(items, 15, "Percent", 0, 0);

        Assert.Equal(300m, totals.DiscountAmount);
        Assert.Equal(1700m, totals.Total);
    }

    [Fact]
    public void Compute_CapsPercentDiscountAtHundred()
    {
        var items = new List<InvoiceItem> { new() { Quantity = 1, UnitPrice = 1000 } };
        Assert.Equal(1000m, BillingCalculator.Compute(items, 500, "Percent", 0, 0).DiscountAmount);
    }

    [Fact]
    public void Compute_CapsAmountDiscountAtSubtotal()
    {
        var items = new List<InvoiceItem> { new() { Quantity = 1, UnitPrice = 500 } };
        var totals = BillingCalculator.Compute(items, 5000, "Amount", 0, 0);

        Assert.Equal(500m, totals.DiscountAmount);
        Assert.Equal(0m, totals.Total);
    }

    [Fact]
    public void Compute_WritesLineTotalsBackToItems()
    {
        var items = new List<InvoiceItem> { new() { Quantity = 3, UnitPrice = 250, Discount = 50 } };
        BillingCalculator.Compute(items, 0, "Amount", 0, 0);
        Assert.Equal(700m, items[0].LineTotal);
    }

    [Fact]
    public void Compute_DerivesPartialAndPaidStatus()
    {
        var items = new List<InvoiceItem> { new() { Quantity = 1, UnitPrice = 1000 } };

        Assert.Equal("Partial", BillingCalculator.Compute(items, 0, "Amount", 0, 400).Status);
        Assert.Equal("Paid", BillingCalculator.Compute(items, 0, "Amount", 0, 1000).Status);
        Assert.Equal("Paid", BillingCalculator.Compute(items, 0, "Amount", 0, 1500).Status);
    }

    [Fact]
    public void Compute_DueNeverGoesNegative()
    {
        var items = new List<InvoiceItem> { new() { Quantity = 1, UnitPrice = 100 } };
        Assert.Equal(0m, BillingCalculator.Compute(items, 0, "Amount", 0, 250).Due);
    }

    [Fact]
    public void Compute_NegativeDiscountIsIgnoredRatherThanInflatingTheBill()
    {
        var items = new List<InvoiceItem> { new() { Quantity = 1, UnitPrice = 1000 } };
        var totals = BillingCalculator.Compute(items, -500, "Amount", 0, 0);
        Assert.Equal(1000m, totals.Total);
    }

    [Fact]
    public void ValidatePayment_RejectsZeroAndNegative()
    {
        Assert.False(BillingCalculator.ValidatePayment(0, 100, 0, true).IsValid);
        Assert.False(BillingCalculator.ValidatePayment(-50, 100, 0, true).IsValid);
    }

    [Fact]
    public void ValidatePayment_BlocksOverpaymentWhenAdvanceDisabled()
    {
        var result = BillingCalculator.ValidatePayment(500, 300, 0, allowAdvance: false);
        Assert.False(result.IsValid);
        Assert.Contains("outstanding balance", result.Message!);
    }

    [Fact]
    public void ValidatePayment_AllowsAdvanceWhenEnabled()
        => Assert.True(BillingCalculator.ValidatePayment(500, 300, 0, allowAdvance: true).IsValid);

    [Fact]
    public void ValidatePayment_RejectsAbsurdAmounts()
        => Assert.False(BillingCalculator.ValidatePayment(999_999_999m, 100, 0, true).IsValid);

    [Fact]
    public void DetermineStatus_TreatsZeroTotalAsCancelled()
        => Assert.Equal("Cancelled", BillingCalculator.DetermineStatus(0, 0));

    [Fact]
    public void Round_UsesCommercialAwayFromZeroRounding()
    {
        Assert.Equal(2.35m, BillingCalculator.Round(2.345m));
        Assert.Equal(10.13m, BillingCalculator.Round(10.125m));
    }
}
