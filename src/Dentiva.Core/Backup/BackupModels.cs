using System.Text.Json.Serialization;

namespace Dentiva.Core.Backup;

/// <summary>
/// Manifest stored inside every .dentivabackup package. Version and checksums make it possible to
/// reject a tampered or truncated archive before any data is touched.
/// </summary>
public sealed class BackupManifest
{
    public const int CurrentFormatVersion = 1;

    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = CurrentFormatVersion;
    [JsonPropertyName("product")] public string Product { get; set; } = "Dentiva";
    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = "1.0.0";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("createdBy")] public string? CreatedBy { get; set; }
    [JsonPropertyName("clinicName")] public string? ClinicName { get; set; }
    [JsonPropertyName("machineName")] public string? MachineName { get; set; }
    [JsonPropertyName("databaseSha256")] public string? DatabaseSha256 { get; set; }
    [JsonPropertyName("databaseBytes")] public long DatabaseBytes { get; set; }
    [JsonPropertyName("attachmentCount")] public int AttachmentCount { get; set; }
    [JsonPropertyName("attachmentBytes")] public long AttachmentBytes { get; set; }
    [JsonPropertyName("includesAttachments")] public bool IncludesAttachments { get; set; } = true;
    [JsonPropertyName("encrypted")] public bool Encrypted { get; set; }
    [JsonPropertyName("kdfIterations")] public int KdfIterations { get; set; }
    [JsonPropertyName("counts")] public Dictionary<string, int> Counts { get; set; } = new();
    [JsonPropertyName("notes")] public string? Notes { get; set; }
}

/// <summary>Human-readable summary shown in step 2 of the restore wizard.</summary>
public sealed class BackupSummary
{
    public BackupManifest Manifest { get; set; } = new();
    public string PackagePath { get; set; } = string.Empty;
    public long PackageBytes { get; set; }
    public bool IsValid { get; set; }
    public List<string> Problems { get; } = new();
    public List<string> Warnings { get; } = new();
    public Dictionary<string, int> Counts => Manifest.Counts;
    public bool RequiresPassword { get; set; }

    public int PatientCount => Counts.GetValueOrDefault("patients");
    public int VisitCount => Counts.GetValueOrDefault("visits");
    public int TreatmentCount => Counts.GetValueOrDefault("treatments");
    public int PrescriptionCount => Counts.GetValueOrDefault("prescriptions");
    public int AppointmentCount => Counts.GetValueOrDefault("appointments");
    public int InvoiceCount => Counts.GetValueOrDefault("invoices");
    public int PaymentCount => Counts.GetValueOrDefault("payments");
    public int ExpenseCount => Counts.GetValueOrDefault("expenses");
    public int StaffCount => Counts.GetValueOrDefault("staff");
    public int AttachmentCount => Counts.GetValueOrDefault("attachments");
}

/// <summary>A patient entry offered for selection in the selective restore wizard.</summary>
public sealed class RestorePatientOption
{
    public long SourceId { get; set; }
    public string PatientCode { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public int VisitCount { get; set; }
    public int TreatmentCount { get; set; }
    public int InvoiceCount { get; set; }
    public int AttachmentCount { get; set; }
    public DateTime? LastVisit { get; set; }
    public bool ExistsLocally { get; set; }
    public string? ConflictReason { get; set; }
}

public enum ConflictStrategy
{
    Skip,
    KeepExisting,
    Replace,
    Merge
}

/// <summary>User selections captured by the restore wizard.</summary>
public sealed class RestorePlan
{
    public bool RestoreEverything { get; set; } = true;
    public bool IncludePatients { get; set; } = true;
    public bool IncludeVisits { get; set; } = true;
    public bool IncludeTreatments { get; set; } = true;
    public bool IncludeToothChart { get; set; } = true;
    public bool IncludePrescriptions { get; set; } = true;
    public bool IncludeReferrals { get; set; } = true;
    public bool IncludeAppointments { get; set; } = true;
    public bool IncludeInvoices { get; set; } = true;
    public bool IncludePayments { get; set; } = true;
    public bool IncludeExpenses { get; set; } = true;
    public bool IncludeStaff { get; set; } = true;
    public bool IncludeAttachments { get; set; } = true;
    public bool IncludeSettings { get; set; }
    public bool IncludeAuditLog { get; set; }
    public bool IncludeUsers { get; set; }

    /// <summary>Null means every patient; otherwise only these source patient ids are imported.</summary>
    public HashSet<long>? SelectedPatientIds { get; set; }

    public DateTime? FinanceFrom { get; set; }
    public DateTime? FinanceTo { get; set; }
    public HashSet<string>? ExpenseCategories { get; set; }

    public ConflictStrategy PatientConflict { get; set; } = ConflictStrategy.Skip;
    public bool AutoIncludeDependencies { get; set; } = true;
}

/// <summary>Dry-run result presented before anything is written.</summary>
public sealed class RestorePreview
{
    public Dictionary<string, int> ToAdd { get; } = new();
    public Dictionary<string, int> ToUpdate { get; } = new();
    public Dictionary<string, int> ToSkip { get; } = new();
    public List<string> Conflicts { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> MissingDependencies { get; } = new();
    public List<string> MissingAttachments { get; } = new();
    public List<string> InvalidRecords { get; } = new();
    public int TotalToAdd => ToAdd.Values.Sum();
    public int TotalToUpdate => ToUpdate.Values.Sum();
    public int TotalToSkip => ToSkip.Values.Sum();

    public void Add(string key, int count = 1) => ToAdd[key] = ToAdd.GetValueOrDefault(key) + count;
    public void Update(string key, int count = 1) => ToUpdate[key] = ToUpdate.GetValueOrDefault(key) + count;
    public void Skip(string key, int count = 1) => ToSkip[key] = ToSkip.GetValueOrDefault(key) + count;
}

/// <summary>Outcome of an executed restore.</summary>
public sealed class RestoreResult
{
    public bool Success { get; set; }
    public Dictionary<string, int> Inserted { get; } = new();
    public Dictionary<string, int> Updated { get; } = new();
    public Dictionary<string, int> Skipped { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? SafetySnapshotPath { get; set; }
    public int AttachmentsRestored { get; set; }
    public int AttachmentsMissing { get; set; }
    public TimeSpan Duration { get; set; }

    public void Count(Dictionary<string, int> bucket, string key, int n = 1) => bucket[key] = bucket.GetValueOrDefault(key) + n;
    public int TotalInserted => Inserted.Values.Sum();
}

public sealed class BackupOptions
{
    public bool IncludeAttachments { get; set; } = true;
    public bool IncludeAuditLog { get; set; } = true;
    public string? Password { get; set; }
    public string? Notes { get; set; }
    public bool Verify { get; set; } = true;
}

public sealed record BackupProgress(string Stage, double Fraction, string? Detail = null);
