using Dentiva.Core.Models;

namespace Dentiva.Core.Documents;

/// <summary>Clinic branding and defaults applied to every generated document.</summary>
public sealed class ClinicProfile
{
    public string ClinicName { get; set; } = "Dental Clinic";
    public string? LogoPath { get; set; }
    public string? Phone { get; set; }
    public string? SecondaryPhone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? District { get; set; }
    public string? Division { get; set; }
    public string? Country { get; set; }
    public string? PostalCode { get; set; }
    public string? Registration { get; set; }
    public string? TaxId { get; set; }
    public string? InvoiceFooter { get; set; }
    public string? ReceiptFooter { get; set; }

    public string? DentistName { get; set; }
    public string? DentistTitle { get; set; }
    public string? DentistQualification { get; set; }
    public string? DentistSpecialty { get; set; }
    public string? DentistRegistration { get; set; }
    public string? SignaturePath { get; set; }

    public string CurrencySymbol { get; set; } = "৳";
    public string CurrencyCode { get; set; } = "BDT";
    public string TaxLabel { get; set; } = "VAT";
    public bool TaxEnabled { get; set; }

    public string FullAddress
    {
        get
        {
            var parts = new[] { Address, City, District, PostalCode, Country }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            return string.Join(", ", parts);
        }
    }

    public string ContactLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Phone)) parts.Add(Phone!);
            if (!string.IsNullOrWhiteSpace(SecondaryPhone)) parts.Add(SecondaryPhone!);
            if (!string.IsNullOrWhiteSpace(Email)) parts.Add(Email!);
            if (!string.IsNullOrWhiteSpace(Website)) parts.Add(Website!);
            return string.Join("  •  ", parts);
        }
    }

    public string DentistLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(DentistName)) parts.Add(DentistName!);
            if (!string.IsNullOrWhiteSpace(DentistQualification)) parts.Add(DentistQualification!);
            return string.Join(", ", parts);
        }
    }
}

public enum PaperFormat
{
    A4,
    A5,
    Letter,
    Thermal80mm,
    Thermal58mm
}

public sealed class PrintLayout
{
    public PaperFormat Format { get; set; } = PaperFormat.A4;
    public float MarginMm { get; set; } = 12f;
    public int Copies { get; set; } = 1;
    public float ThermalWidthMm { get; set; } = 80f;
    public bool ShowLogo { get; set; } = true;
    public bool ShowSignature { get; set; } = true;

    public static PrintLayout Default => new();
}

public sealed class InvoiceDocumentData
{
    public Invoice Invoice { get; set; } = new();
    public Patient? Patient { get; set; }
    public List<Payment> Payments { get; set; } = new();
    public ClinicProfile Clinic { get; set; } = new();
}

public sealed class ReceiptDocumentData
{
    public Payment Payment { get; set; } = new();
    public Invoice? Invoice { get; set; }
    public Patient? Patient { get; set; }
    public decimal RemainingDue { get; set; }
    public ClinicProfile Clinic { get; set; } = new();
}

public sealed class PrescriptionDocumentData
{
    public Prescription Prescription { get; set; } = new();
    public Patient? Patient { get; set; }
    public ClinicProfile Clinic { get; set; } = new();
}

public sealed class PatientSummaryData
{
    public Patient Patient { get; set; } = new();
    public List<Visit> Visits { get; set; } = new();
    public List<Treatment> Treatments { get; set; } = new();
    public List<Prescription> Prescriptions { get; set; } = new();
    public List<Appointment> Appointments { get; set; } = new();
    public decimal TotalBilled { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal Outstanding { get; set; }
    public ClinicProfile Clinic { get; set; } = new();
}

/// <summary>Generic tabular report used by the reports module.</summary>
public sealed class TabularReportData
{
    public string Title { get; set; } = "Report";
    public string? Subtitle { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public List<string> Columns { get; set; } = new();
    public List<float> ColumnWidths { get; set; } = new();
    public List<string[]> Rows { get; set; } = new();
    public List<(string Label, string Value)> Totals { get; set; } = new();
    public ClinicProfile Clinic { get; set; } = new();
    public bool Landscape { get; set; }
}
