namespace Dentiva.Core.Models;

public class AuditableEntity
{
    public long Id { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}

public sealed class Patient : AuditableEntity
{
    public string PatientCode { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? PreferredName { get; set; }
    public string? Phone { get; set; }
    public string? AlternatePhone { get; set; }
    public string? Email { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? BloodGroup { get; set; }
    public string? Nationality { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? District { get; set; }
    public string? Division { get; set; }
    public string? PostalCode { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyRelationship { get; set; }
    public string? EmergencyPhone { get; set; }
    public string? Allergies { get; set; }
    public string? CurrentMedications { get; set; }
    public string? MedicalConditions { get; set; }
    public string? DentalHistory { get; set; }
    public string? MedicalHistory { get; set; }
    public string? SurgicalHistory { get; set; }
    public string? FamilyHistory { get; set; }
    public string? PregnancyStatus { get; set; }
    public string? TobaccoStatus { get; set; }
    public string? ClinicianNotes { get; set; }
    public string? ReferralSource { get; set; }
    public long? AssignedDentistId { get; set; }
    public bool IsArchived { get; set; }

    public int? Age
    {
        get
        {
            if (DateOfBirth is not { } dob)
            {
                return null;
            }

            var today = DateTime.Today;
            var age = today.Year - dob.Year;
            if (dob.Date > today.AddYears(-age))
            {
                age--;
            }

            return age < 0 ? null : age;
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(PreferredName) ? FullName : $"{FullName} ({PreferredName})";
}

public sealed class Visit : AuditableEntity
{
    public long PatientId { get; set; }
    public DateTime VisitDate { get; set; } = DateTime.Today;
    public string? Reason { get; set; }
    public string? Complaint { get; set; }
    public string? ClinicalNotes { get; set; }
    public string? Diagnosis { get; set; }
    public string? Examination { get; set; }
    public string? TreatmentPerformed { get; set; }
    public string? MedicationSummary { get; set; }
    public long? DentistId { get; set; }
    public string? DentistName { get; set; }
    public DateTime? FollowUpDate { get; set; }
    public string? AdditionalNotes { get; set; }
}

public sealed class TreatmentCategory
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameBn { get; set; }
    public decimal DefaultFee { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

public sealed class Treatment : AuditableEntity
{
    public long PatientId { get; set; }
    public long? VisitId { get; set; }
    public DateTime TreatmentDate { get; set; } = DateTime.Today;
    public long? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string ProcedureName { get; set; } = string.Empty;
    public string? Teeth { get; set; }
    public string? Surfaces { get; set; }
    public string? Diagnosis { get; set; }
    public string? Notes { get; set; }
    public string? DentistName { get; set; }
    public decimal Cost { get; set; }
    public decimal Discount { get; set; }
    public string Status { get; set; } = "Completed";
    public DateTime? FollowUpDate { get; set; }
    public long? InvoiceId { get; set; }
    public decimal NetCost => Math.Max(0m, Cost - Discount);
}

public sealed class ToothRecord
{
    public long Id { get; set; }
    public long PatientId { get; set; }
    public string ToothNumber { get; set; } = string.Empty;
    public string Dentition { get; set; } = "Permanent";
    public string? Surface { get; set; }
    public string Condition { get; set; } = "Healthy";
    public string? PlannedTreatment { get; set; }
    public string? Notes { get; set; }
    public DateTime RecordedDate { get; set; } = DateTime.Today;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Prescription : AuditableEntity
{
    public long PatientId { get; set; }
    public long? VisitId { get; set; }
    public string PrescriptionNumber { get; set; } = string.Empty;
    public DateTime IssuedDate { get; set; } = DateTime.Today;
    public string? DentistName { get; set; }
    public string? Diagnosis { get; set; }
    public string? Advice { get; set; }
    public DateTime? FollowUpDate { get; set; }
    public string? Notes { get; set; }
    public List<PrescriptionItem> Items { get; set; } = new();
}

public sealed class PrescriptionItem
{
    public long Id { get; set; }
    public long PrescriptionId { get; set; }
    public string MedicineName { get; set; } = string.Empty;
    public string? GenericName { get; set; }
    public string? Strength { get; set; }
    public string? Dosage { get; set; }
    public string? Frequency { get; set; }
    public string? Duration { get; set; }
    public string? Route { get; set; }
    public string? MealRelation { get; set; }
    public string? Quantity { get; set; }
    public string? Instructions { get; set; }
    public int SortOrder { get; set; }
}

public sealed class Referral : AuditableEntity
{
    public long PatientId { get; set; }
    public DateTime ReferralDate { get; set; } = DateTime.Today;
    public string ProfessionalName { get; set; } = string.Empty;
    public string? Specialty { get; set; }
    public string? Organization { get; set; }
    public string? Reason { get; set; }
    public string? ClinicalSummary { get; set; }
    public string? Instructions { get; set; }
    public string? Outcome { get; set; }
    public string Status { get; set; } = "Sent";
    public string? Notes { get; set; }
}

public sealed class Appointment : AuditableEntity
{
    public long PatientId { get; set; }
    public DateTime AppointmentDate { get; set; } = DateTime.Today;
    public TimeSpan StartTime { get; set; }
    public TimeSpan? EndTime { get; set; }
    public int DurationMinutes { get; set; } = 30;
    public int? SerialNumber { get; set; }
    public string? AppointmentType { get; set; }
    public string? Reason { get; set; }
    public string Status { get; set; } = AppointmentStatuses.Scheduled;
    public string Priority { get; set; } = "Normal";
    public long? DentistId { get; set; }
    public string? DentistName { get; set; }
    public string? Notes { get; set; }
    public DateTime? CheckedInUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    // Projected
    public string PatientName { get; set; } = string.Empty;
    public string PatientCode { get; set; } = string.Empty;
    public string? PatientPhone { get; set; }
}

public static class AppointmentStatuses
{
    public const string Scheduled = "Scheduled";
    public const string Confirmed = "Confirmed";
    public const string CheckedIn = "Checked In";
    public const string Waiting = "Waiting";
    public const string InTreatment = "In Treatment";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string NoShow = "No Show";

    public static readonly string[] All = { Scheduled, Confirmed, CheckedIn, Waiting, InTreatment, Completed, Cancelled, NoShow };
    public static readonly string[] Active = { Scheduled, Confirmed, CheckedIn, Waiting, InTreatment };
}

public sealed class Invoice : AuditableEntity
{
    public string InvoiceNumber { get; set; } = string.Empty;
    public long PatientId { get; set; }
    public DateTime InvoiceDate { get; set; } = DateTime.Today;
    public DateTime? DueDate { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Discount { get; set; }
    public string DiscountType { get; set; } = "Amount";
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    public decimal PaidAmount { get; set; }
    public string Status { get; set; } = "Unpaid";
    public string? Notes { get; set; }
    public List<InvoiceItem> Items { get; set; } = new();

    public decimal Due => Math.Round(Math.Max(0m, Total - PaidAmount), 2, MidpointRounding.AwayFromZero);

    // Projected
    public string PatientName { get; set; } = string.Empty;
    public string PatientCode { get; set; } = string.Empty;
}

public sealed class InvoiceItem
{
    public long Id { get; set; }
    public long InvoiceId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? ItemType { get; set; }
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal LineTotal { get; set; }
    public long? TreatmentId { get; set; }
    public int SortOrder { get; set; }
}

public sealed class Payment
{
    public long Id { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;
    public long? InvoiceId { get; set; }
    public long PatientId { get; set; }
    public decimal Amount { get; set; }
    public DateTime PaymentDate { get; set; } = DateTime.Now;
    public string Method { get; set; } = "Cash";
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public string? ReceivedBy { get; set; }
    public bool IsRefund { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // Projected
    public string PatientName { get; set; } = string.Empty;
    public string PatientCode { get; set; } = string.Empty;
    public string? InvoiceNumber { get; set; }
}

public sealed class ExpenseCategory
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameBn { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

public sealed class Expense : AuditableEntity
{
    public DateTime ExpenseDate { get; set; } = DateTime.Today;
    public long? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? PaymentMethod { get; set; }
    public string? Reference { get; set; }
    public string? Vendor { get; set; }
    public string? Notes { get; set; }
    public long? StaffId { get; set; }
}

public sealed class StaffMember : AuditableEntity
{
    public string StaffCode { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? Department { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public DateTime? JoiningDate { get; set; }
    public decimal Salary { get; set; }
    public string SalaryType { get; set; } = "Monthly";
    public string? PaymentSchedule { get; set; }
    public string Status { get; set; } = "Active";
    public string? EmergencyContact { get; set; }
    public string? Notes { get; set; }
}

public sealed class SalaryPayment
{
    public long Id { get; set; }
    public long StaffId { get; set; }
    public DateTime PaymentDate { get; set; } = DateTime.Today;
    public string? PeriodLabel { get; set; }
    public decimal Amount { get; set; }
    public string? Method { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public string StaffName { get; set; } = string.Empty;
}

public sealed class Attachment
{
    public long Id { get; set; }
    public string AttachmentUid { get; set; } = Guid.NewGuid().ToString("N");
    public long? PatientId { get; set; }
    public long? ReferralId { get; set; }
    public long? ExpenseId { get; set; }
    public long? StaffId { get; set; }
    public string Category { get; set; } = "Documents";
    public string OriginalName { get; set; } = string.Empty;
    public string StoredPath { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public string? ChecksumSha256 { get; set; }
    public DateTime? DocumentDate { get; set; }
    public string? Description { get; set; }
    public string? Source { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }

    public bool IsImage => ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsPdf => string.Equals(ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    public string SizeDisplay => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024):0.#} MB",
        _ => $"{SizeBytes / (1024.0 * 1024 * 1024):0.##} GB"
    };
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string? Username { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Entity { get; set; }
    public string? EntityId { get; set; }
    public string? Summary { get; set; }
    public string? Metadata { get; set; }
    public DateTime TimestampLocal => TimestampUtc.ToLocalTime();
}

public sealed class AppNotification
{
    public long Id { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Severity { get; set; } = "Information";
    public string Title { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? Category { get; set; }
    public bool IsRead { get; set; }
    public string? LinkEntity { get; set; }
    public string? LinkId { get; set; }
    public DateTime CreatedLocal => CreatedUtc.ToLocalTime();
}

public sealed class AppUser
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = "Owner";
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginUtc { get; set; }
    public HashSet<string> Permissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Can(string permission) => Permissions.Contains(permission);
}
