using Dentiva.Core.Models;
using Dentiva.Core.Repositories;
using Dentiva.Core.Services;
using Xunit;

namespace Dentiva.Tests;

public class BillingWorkflowTests
{
    [Fact]
    public async Task CreateInvoice_ComputesTotalsAndAllocatesNumber()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var id = await h.NewInvoiceAsync(pid, ("Consultation", 1, 800), ("Scaling", 1, 1500));

        var invoice = await h.Billing.GetInvoiceAsync(id);

        Assert.Equal("INV-000001", invoice!.InvoiceNumber);
        Assert.Equal(2300m, invoice.Subtotal);
        Assert.Equal(2300m, invoice.Total);
        Assert.Equal(2300m, invoice.Due);
        Assert.Equal("Unpaid", invoice.Status);
        Assert.Equal(2, invoice.Items.Count);
    }

    [Fact]
    public async Task Invoice_RejectsEmptyOrInvalidLineItems()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();

        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Billing.SaveInvoiceAsync(new Invoice { PatientId = pid }, "t"));

        var blankDescription = new Invoice { PatientId = pid };
        blankDescription.Items.Add(new InvoiceItem { Description = "  ", Quantity = 1, UnitPrice = 100 });
        await Assert.ThrowsAsync<DentivaValidationException>(() => h.Billing.SaveInvoiceAsync(blankDescription, "t"));

        var overDiscounted = new Invoice { PatientId = pid };
        overDiscounted.Items.Add(new InvoiceItem { Description = "Filling", Quantity = 1, UnitPrice = 100, Discount = 500 });
        await Assert.ThrowsAsync<DentivaValidationException>(() => h.Billing.SaveInvoiceAsync(overDiscounted, "t"));
    }

    [Fact]
    public async Task Invoice_RejectsMissingPatient()
    {
        using var h = new TestHarness();
        var invoice = new Invoice { PatientId = 0 };
        invoice.Items.Add(new InvoiceItem { Description = "X", Quantity = 1, UnitPrice = 10 });

        await Assert.ThrowsAsync<DentivaValidationException>(() => h.Billing.SaveInvoiceAsync(invoice, "t"));
    }

    [Fact]
    public async Task PartialPayment_UpdatesPaidAmountAndStatus()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var invoiceId = await h.NewInvoiceAsync(pid, ("Root canal", 1, 5000));

        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 2000, Method = "bKash" }, "t");

        var invoice = await h.Billing.GetInvoiceAsync(invoiceId);
        Assert.Equal(2000m, invoice!.PaidAmount);
        Assert.Equal(3000m, invoice.Due);
        Assert.Equal("Partial", invoice.Status);
    }

    [Fact]
    public async Task MultiplePayments_SettleTheInvoiceCompletely()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var invoiceId = await h.NewInvoiceAsync(pid, ("Crown", 1, 9000));

        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 4000, Method = "Cash" }, "t");
        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 3000, Method = "Nagad" }, "t");
        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 2000, Method = "Card" }, "t");

        var invoice = await h.Billing.GetInvoiceAsync(invoiceId);
        Assert.Equal(9000m, invoice!.PaidAmount);
        Assert.Equal(0m, invoice.Due);
        Assert.Equal("Paid", invoice.Status);
    }

    [Fact]
    public async Task Payments_AllocateUniqueReceiptNumbers()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var invoiceId = await h.NewInvoiceAsync(pid, ("Treatment", 1, 10000));

        var receipts = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var payment = await h.Billing.RecordPaymentAsync(
                new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 100, Method = "Cash" }, "t");
            receipts.Add(payment.ReceiptNumber);
        }

        Assert.Equal(10, receipts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.StartsWith("RCP-", receipts[0]);
    }

    [Fact]
    public async Task Payment_RejectsOverpaymentWhenAdvanceIsDisabled()
    {
        using var h = new TestHarness();
        h.Settings.Set(SettingsService.Keys.AllowAdvance, false);

        var pid = await h.NewPatientAsync();
        var invoiceId = await h.NewInvoiceAsync(pid, ("Filling", 1, 1000));

        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 5000, Method = "Cash" }, "t"));
    }

    [Fact]
    public async Task Payment_AcceptsAdvanceWhenExplicitlyEnabled()
    {
        using var h = new TestHarness();
        h.Settings.Set(SettingsService.Keys.AllowAdvance, true);

        var pid = await h.NewPatientAsync();
        var invoiceId = await h.NewInvoiceAsync(pid, ("Filling", 1, 1000));
        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 1500, Method = "Cash" }, "t");

        var invoice = await h.Billing.GetInvoiceAsync(invoiceId);
        Assert.Equal("Paid", invoice!.Status);
        Assert.Equal(0m, invoice.Due);
    }

    [Fact]
    public async Task Payment_RejectsZeroNegativeAndAbsurdAmounts()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();

        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Billing.RecordPaymentAsync(new Payment { PatientId = pid, Amount = 0, Method = "Cash" }, "t"));
        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Billing.RecordPaymentAsync(new Payment { PatientId = pid, Amount = -500, Method = "Cash" }, "t"));
        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Billing.RecordPaymentAsync(new Payment { PatientId = pid, Amount = 100_000_000m, Method = "Cash" }, "t"));
    }

    [Fact]
    public async Task Payment_RejectsUnlinkedPatient()
    {
        using var h = new TestHarness();
        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Billing.RecordPaymentAsync(new Payment { PatientId = 0, Amount = 100, Method = "Cash" }, "t"));
    }

    [Fact]
    public async Task Refund_ReducesTheCollectedAmount()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var invoiceId = await h.NewInvoiceAsync(pid, ("Extraction", 1, 3000));

        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 3000, Method = "Cash" }, "t");
        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 1000, Method = "Cash", IsRefund = true }, "t");

        var invoice = await h.Billing.GetInvoiceAsync(invoiceId);
        Assert.Equal(2000m, invoice!.PaidAmount);
        Assert.Equal("Partial", invoice.Status);
    }

    [Fact]
    public async Task Refund_CannotExceedWhatWasCollected()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var invoiceId = await h.NewInvoiceAsync(pid, ("Scaling", 1, 2000));
        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 500, Method = "Cash" }, "t");

        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 900, Method = "Cash", IsRefund = true }, "t"));
    }

    [Fact]
    public async Task ConcurrentPaymentSubmissions_ProduceDistinctReceiptsAndCorrectTotal()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var invoiceId = await h.NewInvoiceAsync(pid, ("Implant", 1, 40000));

        var results = await Task.WhenAll(
            h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 5000, Method = "Cash" }, "t"),
            h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 5000, Method = "Cash" }, "t"));

        Assert.NotEqual(results[0].ReceiptNumber, results[1].ReceiptNumber);
        Assert.Equal(10000m, (await h.Billing.GetInvoiceAsync(invoiceId))!.PaidAmount);
    }

    [Fact]
    public async Task CancelInvoice_IsBlockedOncePaymentsExist()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var invoiceId = await h.NewInvoiceAsync(pid, ("Consultation", 1, 500));
        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 500, Method = "Cash" }, "t");

        await Assert.ThrowsAsync<DentivaValidationException>(() => h.Billing.CancelInvoiceAsync(invoiceId, "t"));
    }

    [Fact]
    public async Task CancelInvoice_SucceedsWhenNothingHasBeenPaid()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var invoiceId = await h.NewInvoiceAsync(pid, ("Consultation", 1, 500));

        await h.Billing.CancelInvoiceAsync(invoiceId, "t");
        Assert.Equal("Cancelled", (await h.Billing.GetInvoiceAsync(invoiceId))!.Status);
    }

    [Fact]
    public async Task CancelledInvoice_RefusesFurtherPayments()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var invoiceId = await h.NewInvoiceAsync(pid, ("Consultation", 1, 500));
        await h.Billing.CancelInvoiceAsync(invoiceId, "t");

        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 100, Method = "Cash" }, "t"));
    }

    [Fact]
    public async Task DeletePatient_IsBlockedWhenBillingHistoryExists()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        await h.NewInvoiceAsync(pid, ("Consultation", 1, 500));

        await Assert.ThrowsAsync<DentivaValidationException>(() => h.Patients.DeleteAsync(pid, "t"));
    }

    [Fact]
    public async Task Outstanding_IsAggregatedPerPatient()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var i1 = await h.NewInvoiceAsync(pid, ("A", 1, 1000));
        await h.NewInvoiceAsync(pid, ("B", 1, 2000));
        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = i1, PatientId = pid, Amount = 400, Method = "Cash" }, "t");

        Assert.Equal(2600m, await h.Patients.GetOutstandingAsync(pid));
    }

    [Fact]
    public async Task InvoiceWithTax_ComputesTheTaxAmount()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();

        var invoice = new Invoice { PatientId = pid, TaxRate = 15m };
        invoice.Items.Add(new InvoiceItem { Description = "Procedure", Quantity = 1, UnitPrice = 10000 });
        var id = await h.Billing.SaveInvoiceAsync(invoice, "t");

        var saved = await h.Billing.GetInvoiceAsync(id);
        Assert.Equal(1500m, saved!.TaxAmount);
        Assert.Equal(11500m, saved.Total);
    }

    [Fact]
    public async Task Invoice_LinksTreatmentsSoTheyAreNoLongerUnbilled()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var treatmentId = await h.Clinical.SaveTreatmentAsync(new Treatment
        {
            PatientId = pid, ProcedureName = "Composite filling", Cost = 2500
        }, "t");

        Assert.Single(await h.Billing.GetUnbilledTreatmentsAsync(pid));

        var invoice = new Invoice { PatientId = pid };
        invoice.Items.Add(new InvoiceItem { Description = "Composite filling", Quantity = 1, UnitPrice = 2500, TreatmentId = treatmentId });
        await h.Billing.SaveInvoiceAsync(invoice, "t");

        Assert.Empty(await h.Billing.GetUnbilledTreatmentsAsync(pid));
    }

    [Fact]
    public async Task InvoiceEdit_ReplacesLineItemsWithoutDuplicating()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var id = await h.NewInvoiceAsync(pid, ("Original", 1, 1000));

        var invoice = await h.Billing.GetInvoiceAsync(id);
        invoice!.Items.Clear();
        invoice.Items.Add(new InvoiceItem { Description = "Replacement", Quantity = 2, UnitPrice = 750 });
        await h.Billing.SaveInvoiceAsync(invoice, "t");

        var updated = await h.Billing.GetInvoiceAsync(id);
        Assert.Single(updated!.Items);
        Assert.Equal(1500m, updated.Total);
    }

    [Fact]
    public async Task PaymentQuery_FiltersByMethodAndPatient()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var invoiceId = await h.NewInvoiceAsync(pid, ("Treatment", 1, 5000));

        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 1000, Method = "Cash" }, "t");
        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 1000, Method = "bKash" }, "t");

        Assert.Equal(2, (await h.Billing.QueryPaymentsAsync(null, null, null, null, pid)).TotalCount);
        Assert.Equal(1, (await h.Billing.QueryPaymentsAsync(null, null, null, "bKash", pid)).TotalCount);
    }

    [Fact]
    public async Task DeletePayment_RestoresTheInvoiceBalance()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var invoiceId = await h.NewInvoiceAsync(pid, ("Treatment", 1, 2000));
        var payment = await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 2000, Method = "Cash" }, "t");

        Assert.Equal("Paid", (await h.Billing.GetInvoiceAsync(invoiceId))!.Status);

        await h.Billing.DeletePaymentAsync(payment.Id, "t");

        var invoice = await h.Billing.GetInvoiceAsync(invoiceId);
        Assert.Equal(0m, invoice!.PaidAmount);
        Assert.Equal("Unpaid", invoice.Status);
    }

    [Fact]
    public async Task FinanceSummary_ReflectsIncomeExpensesAndOutstanding()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var invoiceId = await h.NewInvoiceAsync(pid, ("Treatment", 1, 5000));
        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 3000, Method = "Cash" }, "t");
        await h.Finance.SaveExpenseAsync(new Expense { Description = "Chamber rent", Amount = 1200, CategoryName = "Chamber Rent" }, "t");

        var summary = await h.Finance.GetSummaryAsync(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1));

        Assert.Equal(5000m, summary.GrossBilled);
        Assert.Equal(3000m, summary.Collected);
        Assert.Equal(1200m, summary.Expenses);
        Assert.Equal(2000m, summary.Outstanding);
        Assert.Equal(1800m, summary.NetResult);
    }

    [Fact]
    public async Task FinanceSummary_OnEmptyClinicIsAllZero()
    {
        using var h = new TestHarness();
        var summary = await h.Finance.GetSummaryAsync(DateTime.Today.AddYears(-1), DateTime.Today);

        Assert.Equal(0m, summary.GrossBilled);
        Assert.Equal(0m, summary.Collected);
        Assert.Equal(0m, summary.Expenses);
        Assert.Equal(0m, summary.NetResult);
    }

    [Fact]
    public async Task Expense_RequiresPositiveAmountAndDescription()
    {
        using var h = new TestHarness();
        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Finance.SaveExpenseAsync(new Expense { Description = "", Amount = 100 }, "t"));
        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Finance.SaveExpenseAsync(new Expense { Description = "Rent", Amount = 0 }, "t"));
        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Finance.SaveExpenseAsync(new Expense { Description = "Rent", Amount = -5 }, "t"));
    }

    [Fact]
    public async Task ExpenseAnalytics_GroupByCategoryAndPaymentMethod()
    {
        using var h = new TestHarness();
        await h.Finance.SaveExpenseAsync(new Expense { Description = "Rent", Amount = 20000, CategoryName = "Chamber Rent" }, "t");
        await h.Finance.SaveExpenseAsync(new Expense { Description = "Gloves", Amount = 3000, CategoryName = "Consumables" }, "t");

        var from = DateTime.Today.AddDays(-1);
        var to = DateTime.Today.AddDays(1);
        var byCategory = await h.Finance.GetExpenseByCategoryAsync(from, to);

        Assert.Equal(2, byCategory.Count);
        Assert.Equal(20000m, byCategory.First(c => c.Category == "Chamber Rent").Amount);

        var pid = await h.NewPatientAsync();
        var invoiceId = await h.NewInvoiceAsync(pid, ("Treatment", 1, 4000));
        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 4000, Method = "bKash" }, "t");

        var methods = await h.Finance.GetPaymentMethodDistributionAsync(from, to);
        Assert.Contains(methods, m => m.Category == "bKash" && m.Amount == 4000m);
    }

    [Fact]
    public async Task Staff_SavesWithGeneratedCodeAndSalaryHistory()
    {
        using var h = new TestHarness();
        var staffId = await h.Staff.SaveAsync(new StaffMember
        {
            FullName = "Nasrin Akter", Role = "Receptionist", Salary = 18000, Phone = "01812345678"
        }, "t");

        var all = await h.Staff.GetAllAsync();
        Assert.Single(all);
        Assert.StartsWith("STF-", all[0].StaffCode);

        await h.Staff.SaveSalaryPaymentAsync(new SalaryPayment { StaffId = staffId, Amount = 18000, PeriodLabel = "March 2026" }, "t");
        Assert.Single(await h.Staff.GetSalaryPaymentsAsync(staffId));
    }

    [Fact]
    public async Task Staff_RejectsInvalidContactDetails()
    {
        using var h = new TestHarness();

        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Staff.SaveAsync(new StaffMember { FullName = "", Role = "Nurse" }, "t"));
        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Staff.SaveAsync(new StaffMember { FullName = "No Role" }, "t"));
        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Staff.SaveAsync(new StaffMember { FullName = "Bad Phone", Role = "Nurse", Phone = "abc" }, "t"));
        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Staff.SaveAsync(new StaffMember { FullName = "Bad Email", Role = "Nurse", Email = "nope" }, "t"));
    }

    [Fact]
    public async Task SalaryPayment_RequiresStaffAndPositiveAmount()
    {
        using var h = new TestHarness();
        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Staff.SaveSalaryPaymentAsync(new SalaryPayment { StaffId = 0, Amount = 100 }, "t"));
    }
}
