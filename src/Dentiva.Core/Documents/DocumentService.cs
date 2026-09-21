using Dentiva.Core.Data;
using Dentiva.Core.Models;
using Dentiva.Core.Repositories;
using Dentiva.Core.Services;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Dentiva.Core.Documents;

/// <summary>
/// Builds clinic-branded PDFs and resolves the branding/layout configuration from settings.
/// </summary>
public sealed class DocumentService
{
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;
    private readonly BillingRepository _billing;
    private readonly PatientRepository _patients;

    static DocumentService()
    {
        // QuestPDF Community licence: free for individuals, non-profits and small businesses.
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.EnableDebugging = false;

        // Clinic data is user-entered and may mix Bangla, English and the taka sign. A missing glyph
        // must degrade the glyph, never abort the document the dentist is trying to print.
        QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;
    }

    public DocumentService(SettingsService settings, AppPaths paths, BillingRepository billing, PatientRepository patients)
    {
        _settings = settings;
        _paths = paths;
        _billing = billing;
        _patients = patients;
    }

    public ClinicProfile BuildClinicProfile() => new()
    {
        ClinicName = Fallback(_settings.Get(SettingsService.Keys.ClinicName), "Dental Clinic"),
        LogoPath = ResolveBrandingPath(_settings.Get(SettingsService.Keys.ClinicLogo)),
        Phone = _settings.Get(SettingsService.Keys.ClinicPhone),
        SecondaryPhone = _settings.Get(SettingsService.Keys.ClinicPhone2),
        Email = _settings.Get(SettingsService.Keys.ClinicEmail),
        Website = _settings.Get(SettingsService.Keys.ClinicWebsite),
        Address = _settings.Get(SettingsService.Keys.ClinicAddress),
        City = _settings.Get(SettingsService.Keys.ClinicCity),
        District = _settings.Get(SettingsService.Keys.ClinicDistrict),
        Division = _settings.Get(SettingsService.Keys.ClinicDivision),
        Country = _settings.Get(SettingsService.Keys.ClinicCountry),
        PostalCode = _settings.Get(SettingsService.Keys.ClinicPostal),
        Registration = _settings.Get(SettingsService.Keys.ClinicRegistration),
        TaxId = _settings.Get(SettingsService.Keys.ClinicTaxId),
        InvoiceFooter = _settings.Get(SettingsService.Keys.InvoiceFooter),
        ReceiptFooter = _settings.Get(SettingsService.Keys.ReceiptFooter),
        DentistName = _settings.Get(SettingsService.Keys.DentistName),
        DentistTitle = _settings.Get(SettingsService.Keys.DentistTitle),
        DentistQualification = _settings.Get(SettingsService.Keys.DentistQualification),
        DentistSpecialty = _settings.Get(SettingsService.Keys.DentistSpecialty),
        DentistRegistration = _settings.Get(SettingsService.Keys.DentistRegistration),
        SignaturePath = ResolveBrandingPath(_settings.Get(SettingsService.Keys.DentistSignature)),
        CurrencySymbol = Fallback(_settings.Get(SettingsService.Keys.CurrencySymbol), "৳"),
        CurrencyCode = Fallback(_settings.Get(SettingsService.Keys.Currency), "BDT"),
        TaxLabel = Fallback(_settings.Get(SettingsService.Keys.TaxLabel), "VAT"),
        TaxEnabled = _settings.GetBool(SettingsService.Keys.TaxEnabled)
    };

    private string? ResolveBrandingPath(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try
        {
            if (Path.IsPathRooted(stored)) return File.Exists(stored) ? stored : null;
            var combined = Path.Combine(_paths.BrandingDirectory, stored);
            return File.Exists(combined) ? combined : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Fallback(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    public PrintLayout BuildLayout(PaperFormat? overrideFormat = null)
    {
        var configured = _settings.Get(SettingsService.Keys.PrintPaper, "A4");
        var format = overrideFormat ?? configured switch
        {
            "A5" => PaperFormat.A5,
            "Letter" => PaperFormat.Letter,
            "Thermal80mm" or "80mm" => PaperFormat.Thermal80mm,
            "Thermal58mm" or "58mm" => PaperFormat.Thermal58mm,
            _ => PaperFormat.A4
        };

        return new PrintLayout
        {
            Format = format,
            MarginMm = Math.Clamp(_settings.GetInt(SettingsService.Keys.PrintMarginMm, 12), 0, 40),
            Copies = Math.Clamp(_settings.GetInt(SettingsService.Keys.PrintCopies, 1), 1, 20),
            ThermalWidthMm = Math.Clamp(_settings.GetInt(SettingsService.Keys.ReceiptWidthMm, 80), 40, 120)
        };
    }

    public string EnsureGeneratedFolder()
    {
        Directory.CreateDirectory(_paths.GeneratedDirectory);
        return _paths.GeneratedDirectory;
    }

    private string BuildOutputPath(string? requested, string defaultName)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(requested))!);
            return requested;
        }

        return Path.Combine(EnsureGeneratedFolder(), Validation.SanitiseFileName(defaultName, "document.pdf"));
    }

    public async Task<string> GenerateInvoiceAsync(long invoiceId, string? outputPath = null, PaperFormat? format = null, CancellationToken ct = default)
    {
        var invoice = await _billing.GetInvoiceAsync(invoiceId, ct).ConfigureAwait(false)
            ?? throw new DentivaValidationException("The invoice could not be found.");
        var patient = await _patients.GetAsync(invoice.PatientId, ct).ConfigureAwait(false);
        var payments = await _billing.QueryPaymentsAsync(null, null, null, null, invoice.PatientId, 1, 500, ct).ConfigureAwait(false);

        var data = new InvoiceDocumentData
        {
            Invoice = invoice,
            Patient = patient,
            Payments = payments.Items.Where(p => p.InvoiceId == invoiceId).ToList(),
            Clinic = BuildClinicProfile()
        };

        var path = BuildOutputPath(outputPath, $"Invoice-{invoice.InvoiceNumber}.pdf");
        var layout = BuildLayout(format);
        await Task.Run(() => new InvoiceDocument(data, layout).GeneratePdf(path), ct).ConfigureAwait(false);
        return path;
    }

    public async Task<string> GenerateReceiptAsync(long paymentId, string? outputPath = null, PaperFormat? format = null, CancellationToken ct = default)
    {
        var payment = await _billing.GetPaymentAsync(paymentId, ct).ConfigureAwait(false)
            ?? throw new DentivaValidationException("The payment could not be found.");
        var patient = await _patients.GetAsync(payment.PatientId, ct).ConfigureAwait(false);
        Invoice? invoice = payment.InvoiceId is { } invId
            ? await _billing.GetInvoiceAsync(invId, ct).ConfigureAwait(false)
            : null;

        var remaining = invoice?.Due ?? await _patients.GetOutstandingAsync(payment.PatientId, ct).ConfigureAwait(false);

        var data = new ReceiptDocumentData
        {
            Payment = payment,
            Invoice = invoice,
            Patient = patient,
            RemainingDue = remaining,
            Clinic = BuildClinicProfile()
        };

        var path = BuildOutputPath(outputPath, $"Receipt-{payment.ReceiptNumber}.pdf");
        var layout = BuildLayout(format);
        await Task.Run(() => new ReceiptDocument(data, layout).GeneratePdf(path), ct).ConfigureAwait(false);
        return path;
    }

    public async Task<string> GeneratePrescriptionAsync(Prescription prescription, Patient? patient, string? outputPath = null, PaperFormat? format = null, CancellationToken ct = default)
    {
        var data = new PrescriptionDocumentData
        {
            Prescription = prescription,
            Patient = patient,
            Clinic = BuildClinicProfile()
        };

        var path = BuildOutputPath(outputPath, $"Prescription-{prescription.PrescriptionNumber}.pdf");
        var layout = BuildLayout(format);
        await Task.Run(() => new PrescriptionDocument(data, layout).GeneratePdf(path), ct).ConfigureAwait(false);
        return path;
    }

    public async Task<string> GeneratePatientSummaryAsync(PatientSummaryData data, string? outputPath = null, CancellationToken ct = default)
    {
        data.Clinic = BuildClinicProfile();
        var path = BuildOutputPath(outputPath, $"Patient-{data.Patient.PatientCode}.pdf");
        await Task.Run(() => new PatientSummaryDocument(data).GeneratePdf(path), ct).ConfigureAwait(false);
        return path;
    }

    public async Task<string> GenerateReportAsync(TabularReportData data, string? outputPath = null, CancellationToken ct = default)
    {
        data.Clinic = BuildClinicProfile();
        var name = Validation.SanitiseFileName(data.Title.Replace(' ', '-'), "report") + $"-{DateTime.Now:yyyyMMdd-HHmm}.pdf";
        var path = BuildOutputPath(outputPath, name);
        await Task.Run(() => new TabularReportDocument(data).GeneratePdf(path), ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>Renders a document to an in-memory PDF for on-screen preview.</summary>
    public Task<byte[]> RenderInvoiceBytesAsync(InvoiceDocumentData data, PrintLayout layout, CancellationToken ct = default)
        => Task.Run(() => new InvoiceDocument(data, layout).GeneratePdf(), ct);

    public Task<byte[]> RenderReceiptBytesAsync(ReceiptDocumentData data, PrintLayout layout, CancellationToken ct = default)
        => Task.Run(() => new ReceiptDocument(data, layout).GeneratePdf(), ct);
}
