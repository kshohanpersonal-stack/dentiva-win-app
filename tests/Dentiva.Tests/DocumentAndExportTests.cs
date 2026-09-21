using System.Text;
using Dentiva.Core.Documents;
using Dentiva.Core.Models;
using Dentiva.Core.Services;
using Xunit;

namespace Dentiva.Tests;

public class DocumentTests
{
    private static void ConfigureClinic(TestHarness h)
    {
        h.Settings.Set(SettingsService.Keys.ClinicName, "Dentiva Dental Care");
        h.Settings.Set(SettingsService.Keys.ClinicPhone, "01712345678");
        h.Settings.Set(SettingsService.Keys.ClinicAddress, "House 12, Road 5, Dhanmondi");
        h.Settings.Set(SettingsService.Keys.ClinicCity, "Dhaka");
        h.Settings.Set(SettingsService.Keys.DentistName, "Dr. Shohan Khan");
        h.Settings.Set(SettingsService.Keys.DentistQualification, "BDS, FCPS");
        h.Settings.Set(SettingsService.Keys.DentistRegistration, "BMDC-12345");
    }

    private static void AssertIsPdf(string path)
    {
        Assert.True(File.Exists(path), $"expected a PDF at {path}");
        var info = new FileInfo(path);
        Assert.True(info.Length > 800, $"the PDF at {path} is suspiciously small ({info.Length} bytes)");

        using var stream = File.OpenRead(path);
        var header = new byte[5];
        Assert.Equal(5, stream.Read(header, 0, 5));
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(header));
    }

    [Fact]
    public void ClinicProfile_FallsBackToSafeDefaultsOnAFreshInstall()
    {
        using var h = new TestHarness();
        var profile = h.Documents.BuildClinicProfile();

        Assert.False(string.IsNullOrWhiteSpace(profile.ClinicName));
        Assert.Equal("৳", profile.CurrencySymbol);
        Assert.Equal("BDT", profile.CurrencyCode);
        Assert.Null(profile.LogoPath);
    }

    [Fact]
    public void ClinicProfile_ComposesAddressAndContactLines()
    {
        using var h = new TestHarness();
        ConfigureClinic(h);
        h.Settings.Set(SettingsService.Keys.ClinicEmail, "care@dentiva.example");

        var profile = h.Documents.BuildClinicProfile();

        Assert.Equal("Dentiva Dental Care", profile.ClinicName);
        Assert.Contains("Dhanmondi", profile.FullAddress);
        Assert.Contains("Dhaka", profile.FullAddress);
        Assert.Contains("01712345678", profile.ContactLine);
        Assert.Contains("care@dentiva.example", profile.ContactLine);
        Assert.Contains("Dr. Shohan Khan", profile.DentistLine);
    }

    [Fact]
    public void ClinicProfile_IgnoresBrandingPathsThatDoNotExist()
    {
        using var h = new TestHarness();
        h.Settings.Set(SettingsService.Keys.ClinicLogo, "no-such-logo.png");
        Assert.Null(h.Documents.BuildClinicProfile().LogoPath);
    }

    [Theory]
    [InlineData("A4", PaperFormat.A4)]
    [InlineData("A5", PaperFormat.A5)]
    [InlineData("Letter", PaperFormat.Letter)]
    [InlineData("Thermal80mm", PaperFormat.Thermal80mm)]
    [InlineData("58mm", PaperFormat.Thermal58mm)]
    [InlineData("nonsense", PaperFormat.A4)]
    public void PrintLayout_IsResolvedFromSettings(string configured, PaperFormat expected)
    {
        using var h = new TestHarness();
        h.Settings.Set(SettingsService.Keys.PrintPaper, configured);
        Assert.Equal(expected, h.Documents.BuildLayout().Format);
    }

    [Fact]
    public void PrintLayout_ClampsOutOfRangeMarginsAndCopies()
    {
        using var h = new TestHarness();
        h.Settings.Set(SettingsService.Keys.PrintMarginMm, 500);
        h.Settings.Set(SettingsService.Keys.PrintCopies, -3);
        h.Settings.Set(SettingsService.Keys.ReceiptWidthMm, 5);

        var layout = h.Documents.BuildLayout();

        Assert.InRange(layout.MarginMm, 0f, 40f);
        Assert.InRange(layout.Copies, 1, 20);
        Assert.InRange(layout.ThermalWidthMm, 40f, 120f);
    }

    [Fact]
    public void PrintLayout_ExplicitFormatOverridesTheSetting()
    {
        using var h = new TestHarness();
        h.Settings.Set(SettingsService.Keys.PrintPaper, "A4");
        Assert.Equal(PaperFormat.Thermal80mm, h.Documents.BuildLayout(PaperFormat.Thermal80mm).Format);
    }

    [Fact]
    public async Task InvoicePdf_IsGeneratedOnA4()
    {
        using var h = new TestHarness();
        ConfigureClinic(h);
        var pid = await h.NewPatientAsync("Invoice Patient");
        var invoiceId = await h.NewInvoiceAsync(pid, ("Root canal treatment", 1, 8000), ("Post and core", 1, 3500));

        AssertIsPdf(await h.Documents.GenerateInvoiceAsync(invoiceId));
    }

    [Fact]
    public async Task InvoicePdf_IsGeneratedForEveryPaperFormat()
    {
        using var h = new TestHarness();
        ConfigureClinic(h);
        var pid = await h.NewPatientAsync("Format Patient");
        var invoiceId = await h.NewInvoiceAsync(pid, ("Scaling", 1, 1500));

        foreach (var format in Enum.GetValues<PaperFormat>())
        {
            var path = Path.Combine(h.Paths.GeneratedDirectory, $"invoice-{format}.pdf");
            AssertIsPdf(await h.Documents.GenerateInvoiceAsync(invoiceId, path, format));
        }
    }

    [Fact]
    public async Task InvoicePdf_HandlesAPaidInvoiceWithPaymentHistory()
    {
        using var h = new TestHarness();
        ConfigureClinic(h);
        var pid = await h.NewPatientAsync("Paid Patient");
        var invoiceId = await h.NewInvoiceAsync(pid, ("Crown", 1, 12000));
        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 7000, Method = "bKash" }, "t");
        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 5000, Method = "Cash" }, "t");

        AssertIsPdf(await h.Documents.GenerateInvoiceAsync(invoiceId));
    }

    [Fact]
    public async Task InvoicePdf_HandlesManyLineItemsAcrossPages()
    {
        using var h = new TestHarness();
        ConfigureClinic(h);
        var pid = await h.NewPatientAsync("Long Invoice Patient");

        var invoice = new Invoice { PatientId = pid };
        for (var i = 1; i <= 80; i++)
        {
            invoice.Items.Add(new InvoiceItem { Description = $"Procedure line {i} with a fairly long descriptive label", Quantity = 1, UnitPrice = 250 + i });
        }

        var invoiceId = await h.Billing.SaveInvoiceAsync(invoice, "t");
        AssertIsPdf(await h.Documents.GenerateInvoiceAsync(invoiceId));
    }

    [Fact]
    public async Task InvoicePdf_RendersBanglaPatientDataWithoutFailing()
    {
        using var h = new TestHarness();
        ConfigureClinic(h);
        h.Settings.Set(SettingsService.Keys.ClinicName, "ডেন্টিভা ডেন্টাল কেয়ার");
        var pid = await h.Patients.CreateAsync(new Patient
        {
            FullName = "মোহাম্মদ রফিকুল ইসলাম", Phone = "01712345678", Address = "ধানমন্ডি, ঢাকা"
        }, "t");
        var invoiceId = await h.NewInvoiceAsync(pid, ("দাঁত তোলা", 1, 1200));

        AssertIsPdf(await h.Documents.GenerateInvoiceAsync(invoiceId));
    }

    [Fact]
    public async Task InvoicePdf_ThrowsForAnUnknownInvoice()
    {
        using var h = new TestHarness();
        await Assert.ThrowsAsync<Dentiva.Core.Repositories.DentivaValidationException>(
            () => h.Documents.GenerateInvoiceAsync(999_999));
    }

    [Fact]
    public async Task ReceiptPdf_IsGeneratedOnPageAndThermalStock()
    {
        using var h = new TestHarness();
        ConfigureClinic(h);
        var pid = await h.NewPatientAsync("Receipt Patient");
        var invoiceId = await h.NewInvoiceAsync(pid, ("Filling", 1, 2500));
        var payment = await h.Billing.RecordPaymentAsync(
            new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 1500, Method = "Nagad", Reference = "TRX123" }, "t");

        AssertIsPdf(await h.Documents.GenerateReceiptAsync(payment.Id));
        AssertIsPdf(await h.Documents.GenerateReceiptAsync(payment.Id,
            Path.Combine(h.Paths.GeneratedDirectory, "receipt-thermal.pdf"), PaperFormat.Thermal80mm));
    }

    [Fact]
    public async Task ReceiptPdf_WorksForAPaymentWithoutAnInvoice()
    {
        using var h = new TestHarness();
        ConfigureClinic(h);
        var pid = await h.NewPatientAsync("Advance Patient");
        var payment = await h.Billing.RecordPaymentAsync(
            new Payment { PatientId = pid, Amount = 5000, Method = "Cash", Notes = "Advance deposit" }, "t");

        AssertIsPdf(await h.Documents.GenerateReceiptAsync(payment.Id));
    }

    [Fact]
    public async Task ReceiptPdf_ThrowsForAnUnknownPayment()
    {
        using var h = new TestHarness();
        await Assert.ThrowsAsync<Dentiva.Core.Repositories.DentivaValidationException>(
            () => h.Documents.GenerateReceiptAsync(424_242));
    }

    [Fact]
    public async Task PrescriptionPdf_IsGeneratedFromRealEnteredData()
    {
        using var h = new TestHarness();
        ConfigureClinic(h);
        var pid = await h.NewPatientAsync("Rx Patient");
        var rxId = await h.Clinical.SavePrescriptionAsync(new Prescription
        {
            PatientId = pid,
            Diagnosis = "Irreversible pulpitis, tooth 46",
            Advice = "Avoid chewing on the right side until the next visit.",
            FollowUpDate = DateTime.Today.AddDays(7),
            Items =
            {
                new PrescriptionItem { MedicineName = "Amoxicillin", Strength = "500 mg", Dosage = "1 capsule", Frequency = "8 hourly", Duration = "7 days", MealRelation = "After meals" },
                new PrescriptionItem { MedicineName = "Ibuprofen", Strength = "400 mg", Dosage = "1 tablet", Frequency = "12 hourly", Duration = "5 days" },
                new PrescriptionItem { MedicineName = "Chlorhexidine mouthwash", Dosage = "10 ml", Frequency = "Twice daily", Duration = "14 days" }
            }
        }, "t");

        var prescription = await h.Clinical.GetPrescriptionAsync(rxId);
        var patient = await h.Patients.GetAsync(pid);

        AssertIsPdf(await h.Documents.GeneratePrescriptionAsync(prescription!, patient));
    }

    [Fact]
    public async Task PatientSummaryPdf_IsGeneratedWithFullHistory()
    {
        using var h = new TestHarness();
        ConfigureClinic(h);
        var pid = await h.NewPatientAsync("Summary Patient");
        await h.Clinical.SaveVisitAsync(new Visit { PatientId = pid, Reason = "Pain", Diagnosis = "Caries" }, "t");
        await h.Clinical.SaveTreatmentAsync(new Treatment { PatientId = pid, ProcedureName = "Filling", Cost = 2000 }, "t");

        var data = new PatientSummaryData
        {
            Patient = (await h.Patients.GetAsync(pid))!,
            Visits = await h.Clinical.GetVisitsAsync(pid),
            Treatments = await h.Clinical.GetTreatmentsAsync(pid),
            Prescriptions = await h.Clinical.GetPrescriptionsAsync(pid),
            TotalBilled = 2000,
            TotalPaid = 500,
            Outstanding = 1500
        };

        AssertIsPdf(await h.Documents.GeneratePatientSummaryAsync(data));
    }

    [Fact]
    public async Task PatientSummaryPdf_WorksForABrandNewPatientWithNoHistory()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync("Empty History Patient");

        var data = new PatientSummaryData { Patient = (await h.Patients.GetAsync(pid))! };
        AssertIsPdf(await h.Documents.GeneratePatientSummaryAsync(data));
    }

    [Fact]
    public async Task ReportPdf_IsGeneratedInPortraitAndLandscape()
    {
        using var h = new TestHarness();
        ConfigureClinic(h);

        var data = new TabularReportData
        {
            Title = "Daily Collection Report",
            Subtitle = "All payment methods",
            From = DateTime.Today.AddDays(-30),
            To = DateTime.Today,
            Columns = { "Date", "Receipt", "Patient", "Method", "Amount" },
            Totals = { ("Total collected", "12,500.00") }
        };

        for (var i = 0; i < 120; i++)
        {
            data.Rows.Add(new[] { DateTime.Today.AddDays(-i).ToString("dd MMM yyyy"), $"RCP-{i:D6}", $"Patient {i}", "Cash", (100 + i).ToString("N2") });
        }

        AssertIsPdf(await h.Documents.GenerateReportAsync(data));

        data.Landscape = true;
        AssertIsPdf(await h.Documents.GenerateReportAsync(data, Path.Combine(h.Paths.GeneratedDirectory, "report-landscape.pdf")));
    }

    [Fact]
    public async Task ReportPdf_HandlesAnEmptyResultSet()
    {
        using var h = new TestHarness();
        var data = new TabularReportData
        {
            Title = "Empty Report",
            Columns = { "Date", "Value" }
        };

        AssertIsPdf(await h.Documents.GenerateReportAsync(data));
    }

    [Fact]
    public async Task RenderBytes_ProducesAnInMemoryPdfForPreview()
    {
        using var h = new TestHarness();
        ConfigureClinic(h);
        var pid = await h.NewPatientAsync("Preview Patient");
        var invoiceId = await h.NewInvoiceAsync(pid, ("Consultation", 1, 900));
        var invoice = await h.Billing.GetInvoiceAsync(invoiceId);

        var bytes = await h.Documents.RenderInvoiceBytesAsync(new InvoiceDocumentData
        {
            Invoice = invoice!,
            Patient = await h.Patients.GetAsync(pid),
            Clinic = h.Documents.BuildClinicProfile()
        }, PrintLayout.Default);

        Assert.True(bytes.Length > 800);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [Fact]
    public async Task GeneratedPdfFileNames_AreSanitised()
    {
        using var h = new TestHarness();
        var data = new TabularReportData { Title = "Report: 2026/03 <unsafe>", Columns = { "A" } };

        var path = await h.Documents.GenerateReportAsync(data);
        var name = Path.GetFileName(path);

        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('<', name);
        AssertIsPdf(path);
    }
}

public class ExportTests
{
    private static async Task<long> SeedAsync(TestHarness h)
    {
        var pid = await h.Patients.CreateAsync(new Patient
        {
            FullName = "Export Patient", Phone = "01712345678", Email = "patient@example.com", Gender = "Female"
        }, "t");

        await h.Clinical.SaveTreatmentAsync(new Treatment { PatientId = pid, ProcedureName = "Scaling", Cost = 1500 }, "t");
        await h.Appointments.SaveAsync(new Appointment { PatientId = pid, AppointmentDate = DateTime.Today, StartTime = new TimeSpan(10, 0, 0) }, "t");
        await h.Clinical.SavePrescriptionAsync(new Prescription
        {
            PatientId = pid, Items = { new PrescriptionItem { MedicineName = "Paracetamol" } }
        }, "t");

        var invoiceId = await h.NewInvoiceAsync(pid, ("Scaling", 1, 1500));
        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 500, Method = "Cash" }, "t");
        await h.Finance.SaveExpenseAsync(new Expense { Description = "Gloves", Amount = 800, CategoryName = "Consumables" }, "t");
        await h.Staff.SaveAsync(new StaffMember { FullName = "Assistant One", Role = "Assistant", Salary = 12000 }, "t");

        return pid;
    }

    private static string[] ReadLines(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }

    [Fact]
    public void EscapeCsv_QuotesSeparatorsAndDoublesQuotes()
    {
        Assert.Equal("\"a,b\"", ExportService.EscapeCsv("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", ExportService.EscapeCsv("say \"hi\""));
        Assert.Equal("\"line1\nline2\"", ExportService.EscapeCsv("line1\nline2"));
        Assert.Equal(string.Empty, ExportService.EscapeCsv(null));
    }

    [Theory]
    [InlineData("=cmd|'/c calc'!A1")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1:A9)")]
    public void EscapeCsv_NeutralisesFormulaInjection(string payload)
    {
        var escaped = ExportService.EscapeCsv(payload);
        var unquoted = escaped.Trim('"');

        Assert.False(unquoted.StartsWith('='), escaped);
        Assert.False(unquoted.StartsWith('+'), escaped);
        Assert.False(unquoted.StartsWith('-'), escaped);
        Assert.False(unquoted.StartsWith('@'), escaped);
    }

    [Fact]
    public async Task ExportPatients_WritesUtf8CsvWithBomAndHeader()
    {
        using var h = new TestHarness();
        await SeedAsync(h);

        var path = await h.Export.ExportPatientsAsync(false);

        Assert.True(File.Exists(path));
        Assert.EndsWith(".csv", path);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());

        var lines = ReadLines(path);
        Assert.True(lines.Length >= 2);
        Assert.Contains("Patient Code", lines[0]);
        Assert.Contains("Export Patient", string.Join('\n', lines));
    }

    [Fact]
    public async Task ExportPatients_ProducesHeaderOnlyFileForAnEmptyClinic()
    {
        using var h = new TestHarness();
        var lines = ReadLines(await h.Export.ExportPatientsAsync(false));
        Assert.Single(lines);
    }

    [Fact]
    public async Task ExportPatients_PreservesBanglaText()
    {
        using var h = new TestHarness();
        await h.Patients.CreateAsync(new Patient { FullName = "মোহাম্মদ রফিকুল ইসলাম" }, "t");

        var content = await File.ReadAllTextAsync(await h.Export.ExportPatientsAsync(false), Encoding.UTF8);
        Assert.Contains("মোহাম্মদ রফিকুল ইসলাম", content);
    }

    [Fact]
    public async Task ExportPatients_NeutralisesAFormulaInjectionAttemptStoredAsAName()
    {
        using var h = new TestHarness();
        await h.Patients.CreateAsync(new Patient { FullName = "=HYPERLINK(\"http://evil\",\"click\")" }, "t");

        var content = await File.ReadAllTextAsync(await h.Export.ExportPatientsAsync(false), Encoding.UTF8);
        Assert.DoesNotContain(",=HYPERLINK", content);
    }

    [Fact]
    public async Task EveryExporter_ProducesAReadableCsv()
    {
        using var h = new TestHarness();
        await SeedAsync(h);
        var from = DateTime.Today.AddDays(-7);
        var to = DateTime.Today.AddDays(7);

        var paths = new[]
        {
            await h.Export.ExportPatientsAsync(true),
            await h.Export.ExportAppointmentsAsync(from, to),
            await h.Export.ExportInvoicesAsync(from, to),
            await h.Export.ExportPaymentsAsync(from, to),
            await h.Export.ExportExpensesAsync(from, to),
            await h.Export.ExportTreatmentsAsync(from, to),
            await h.Export.ExportStaffAsync(),
            await h.Export.ExportPrescriptionsAsync(from, to),
            await h.Export.ExportAuditLogAsync(from, to)
        };

        foreach (var path in paths)
        {
            Assert.True(File.Exists(path), path);
            var lines = ReadLines(path);
            Assert.True(lines.Length >= 2, $"{Path.GetFileName(path)} produced no data rows");
            Assert.Contains(',', lines[0]);
        }

        Assert.Equal(paths.Length, paths.Distinct().Count());
    }

    [Fact]
    public async Task Exports_AreWrittenIntoTheManagedExportsFolderByDefault()
    {
        using var h = new TestHarness();
        await SeedAsync(h);
        var path = await h.Export.ExportPatientsAsync(false);

        Assert.StartsWith(h.Paths.ExportsDirectory, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exports_HonourAnExplicitDestination()
    {
        using var h = new TestHarness();
        await SeedAsync(h);
        var destination = Path.Combine(h.Paths.TempDirectory, "custom-export.csv");

        Assert.Equal(destination, await h.Export.ExportPatientsAsync(false, destination));
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task Exports_AreRecordedInTheAuditLog()
    {
        using var h = new TestHarness();
        await SeedAsync(h);
        await h.Export.ExportPatientsAsync(false);

        var log = await h.Audit.QueryAsync("export", null, null);
        Assert.NotEmpty(log.Items);
    }
}
