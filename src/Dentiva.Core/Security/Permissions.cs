namespace Dentiva.Core.Security;

/// <summary>
/// Granular permission catalogue. Roles map to permission sets; users may be granted overrides.
/// </summary>
public static class Permissions
{
    public const string PatientView = "patient.view";
    public const string PatientCreate = "patient.create";
    public const string PatientEdit = "patient.edit";
    public const string PatientDelete = "patient.delete";
    public const string PatientArchive = "patient.archive";
    public const string MedicalView = "medical.view";
    public const string MedicalEdit = "medical.edit";
    public const string AppointmentView = "appointment.view";
    public const string AppointmentManage = "appointment.manage";
    public const string PrescriptionView = "prescription.view";
    public const string PrescriptionCreate = "prescription.create";
    public const string BillingView = "billing.view";
    public const string BillingCreate = "billing.create";
    public const string PaymentRecord = "payment.record";
    public const string PaymentRefund = "payment.refund";
    public const string FinanceView = "finance.view";
    public const string FinanceEdit = "finance.edit";
    public const string StaffView = "staff.view";
    public const string StaffManage = "staff.manage";
    public const string ReportView = "report.view";
    public const string DataExport = "data.export";
    public const string DataImport = "data.import";
    public const string BackupCreate = "backup.create";
    public const string BackupRestore = "backup.restore";
    public const string SettingsManage = "settings.manage";
    public const string UserManage = "user.manage";
    public const string AuditView = "audit.view";

    public static IReadOnlyList<PermissionDescriptor> All { get; } = new List<PermissionDescriptor>
    {
        new(PatientView, "View patients", "Patients"),
        new(PatientCreate, "Create patients", "Patients"),
        new(PatientEdit, "Edit patients", "Patients"),
        new(PatientDelete, "Delete patients", "Patients"),
        new(PatientArchive, "Archive patients", "Patients"),
        new(MedicalView, "View clinical records", "Clinical"),
        new(MedicalEdit, "Edit clinical records", "Clinical"),
        new(AppointmentView, "View appointments", "Appointments"),
        new(AppointmentManage, "Manage appointments", "Appointments"),
        new(PrescriptionView, "View prescriptions", "Clinical"),
        new(PrescriptionCreate, "Create prescriptions", "Clinical"),
        new(BillingView, "View invoices", "Billing"),
        new(BillingCreate, "Create invoices", "Billing"),
        new(PaymentRecord, "Record payments", "Billing"),
        new(PaymentRefund, "Record refunds", "Billing"),
        new(FinanceView, "View finance", "Finance"),
        new(FinanceEdit, "Manage finance", "Finance"),
        new(StaffView, "View staff", "Staff"),
        new(StaffManage, "Manage staff", "Staff"),
        new(ReportView, "View reports", "Reports"),
        new(DataExport, "Export data", "Data"),
        new(DataImport, "Import data", "Data"),
        new(BackupCreate, "Create backups", "Data"),
        new(BackupRestore, "Restore backups", "Data"),
        new(SettingsManage, "Manage settings", "System"),
        new(UserManage, "Manage users", "System"),
        new(AuditView, "View audit log", "System")
    };

    public static readonly string[] Roles = { "Owner", "Dentist", "Administrator", "Receptionist", "Accountant", "Assistant" };

    public static IReadOnlyCollection<string> ForRole(string role) => role switch
    {
        "Owner" => All.Select(p => p.Key).ToArray(),
        "Administrator" => All.Select(p => p.Key).Where(k => k != PaymentRefund).ToArray(),
        "Dentist" => new[]
        {
            PatientView, PatientCreate, PatientEdit, PatientArchive, MedicalView, MedicalEdit,
            AppointmentView, AppointmentManage, PrescriptionView, PrescriptionCreate,
            BillingView, BillingCreate, PaymentRecord, ReportView, DataExport, BackupCreate
        },
        "Receptionist" => new[]
        {
            PatientView, PatientCreate, PatientEdit, AppointmentView, AppointmentManage,
            BillingView, BillingCreate, PaymentRecord, ReportView
        },
        "Accountant" => new[]
        {
            PatientView, BillingView, BillingCreate, PaymentRecord, PaymentRefund,
            FinanceView, FinanceEdit, StaffView, ReportView, DataExport
        },
        "Assistant" => new[] { PatientView, AppointmentView, MedicalView, PrescriptionView },
        _ => Array.Empty<string>()
    };
}

public sealed record PermissionDescriptor(string Key, string Description, string Group);
