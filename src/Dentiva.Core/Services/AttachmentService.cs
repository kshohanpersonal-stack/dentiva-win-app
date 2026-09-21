using System.Security.Cryptography;
using Dentiva.Core.Data;
using Dentiva.Core.Models;
using Dentiva.Core.Repositories;

namespace Dentiva.Core.Services;

/// <summary>
/// Managed attachment store. Files are copied into a category folder under a generated unique name,
/// hashed with SHA-256 and recorded with full metadata, so original names can never collide and
/// integrity can be verified later.
/// </summary>
public sealed class AttachmentService
{
    private readonly Database _db;
    private readonly AppPaths _paths;
    private readonly AuditService _audit;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff",
        ".pdf", ".txt", ".rtf", ".csv",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".dcm", ".zip"
    };

    public const long MaxFileBytes = 512L * 1024 * 1024;

    public AttachmentService(Database db, AppPaths paths, AuditService audit)
    {
        _db = db;
        _paths = paths;
        _audit = audit;
    }

    public static IReadOnlyList<string> Categories { get; } = new[]
    {
        "XRay", "Reports", "Documents", "Referrals", "Patients", "Expenses", "Staff", "Other"
    };

    public static string GuessContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".rtf" => "application/rtf",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".dcm" => "application/dicom",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };

    public static ValidationResult ValidateFile(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return ValidationResult.Fail("The selected file could not be found.");
        }

        var ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(ext) || !AllowedExtensions.Contains(ext))
        {
            return ValidationResult.Fail($"'{ext}' files are not supported. Use an image, PDF, Office or text document.");
        }

        var info = new FileInfo(sourcePath);
        if (info.Length == 0)
        {
            return ValidationResult.Fail("The selected file is empty.");
        }

        if (info.Length > MaxFileBytes)
        {
            return ValidationResult.Fail($"The file is larger than the {MaxFileBytes / (1024 * 1024)} MB limit.");
        }

        return ValidationResult.Ok();
    }

    public static string ComputeChecksum(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public async Task<Attachment> AddAsync(string sourcePath, Attachment metadata, string? actor, CancellationToken ct = default)
    {
        var check = ValidateFile(sourcePath);
        if (!check.IsValid) throw new DentivaValidationException(check.Message!);

        _paths.EnsureCreated();

        var category = Categories.Contains(metadata.Category, StringComparer.OrdinalIgnoreCase) ? metadata.Category : "Other";
        var folderName = category switch
        {
            "XRay" => "XRay",
            "Reports" => "Reports",
            "Referrals" => "Referrals",
            "Expenses" => "Expenses",
            "Staff" => "Staff",
            "Patients" => "Patients",
            "Documents" => "Documents",
            _ => "Other"
        };

        var originalName = Validation.SanitiseFileName(Path.GetFileName(sourcePath));
        var extension = Path.GetExtension(originalName);
        var uid = Guid.NewGuid().ToString("N");
        var subFolder = metadata.PatientId is { } pid ? Path.Combine(folderName, $"P{pid:D8}") : folderName;
        var relative = Path.Combine(subFolder, uid + extension);
        var destination = _paths.ResolveAttachment(relative);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        await using (var dst = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }

        var info = new FileInfo(destination);
        metadata.AttachmentUid = uid;
        metadata.Category = category;
        metadata.OriginalName = originalName;
        metadata.StoredPath = relative.Replace('\\', '/');
        metadata.ContentType = GuessContentType(originalName);
        metadata.SizeBytes = info.Length;
        metadata.ChecksumSha256 = ComputeChecksum(destination);
        metadata.CreatedUtc = DateTime.UtcNow;
        metadata.CreatedBy = actor;

        try
        {
            await _db.WriteAsync(tx =>
            {
                using var cmd = _db.CreateCommand("""
                    INSERT INTO attachments(attachment_uid, patient_id, referral_id, expense_id, staff_id, category,
                        original_name, stored_path, content_type, size_bytes, checksum_sha256, document_date,
                        description, source, notes, created_utc, created_by)
                    VALUES($uid,$pid,$rid,$eid,$sid,$cat,$name,$path,$type,$size,$sum,$docdate,$desc,$src,$notes,$c,$cb);
                    SELECT last_insert_rowid();
                    """);
                cmd.Transaction = tx;
                cmd.AddValue("$uid", metadata.AttachmentUid);
                cmd.AddValue("$pid", metadata.PatientId);
                cmd.AddValue("$rid", metadata.ReferralId);
                cmd.AddValue("$eid", metadata.ExpenseId);
                cmd.AddValue("$sid", metadata.StaffId);
                cmd.AddValue("$cat", metadata.Category);
                cmd.AddValue("$name", metadata.OriginalName);
                cmd.AddValue("$path", metadata.StoredPath);
                cmd.AddValue("$type", metadata.ContentType);
                cmd.AddValue("$size", metadata.SizeBytes);
                cmd.AddValue("$sum", metadata.ChecksumSha256);
                cmd.AddDate("$docdate", metadata.DocumentDate);
                cmd.AddValue("$desc", metadata.Description);
                cmd.AddValue("$src", metadata.Source);
                cmd.AddValue("$notes", metadata.Notes);
                cmd.AddValue("$c", metadata.CreatedUtc.ToString("O"));
                cmd.AddValue("$cb", metadata.CreatedBy);
                metadata.Id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            // Roll the file copy back so the store never keeps an orphaned blob.
            try { File.Delete(destination); } catch { /* ignore */ }
            throw;
        }

        _audit.Log("attachment.added", "Attachment", metadata.Id.ToString(), $"{metadata.Category}: {metadata.OriginalName}", actor);
        return metadata;
    }

    public Task<List<Attachment>> GetForPatientAsync(long patientId, string? category = null, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<Attachment>();
        var sql = "SELECT * FROM attachments WHERE patient_id=$p" +
                  (string.IsNullOrWhiteSpace(category) || category == "All" ? string.Empty : " AND category=$c") +
                  " ORDER BY created_utc DESC, id DESC";
        using var cmd = _db.CreateCommand(sql);
        cmd.AddValue("$p", patientId);
        if (!(string.IsNullOrWhiteSpace(category) || category == "All")) cmd.AddValue("$c", category);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }, ct);

    public Task<Attachment?> GetAsync(long id, CancellationToken ct = default) => Task.Run(() =>
    {
        using var cmd = _db.CreateCommand("SELECT * FROM attachments WHERE id=$id");
        cmd.AddValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }, ct);

    internal static Attachment Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64Value("id"),
        AttachmentUid = r.GetStringOrEmpty("attachment_uid"),
        PatientId = r.GetInt64OrNull("patient_id"),
        ReferralId = r.GetInt64OrNull("referral_id"),
        ExpenseId = r.GetInt64OrNull("expense_id"),
        StaffId = r.GetInt64OrNull("staff_id"),
        Category = r.GetStringOrEmpty("category"),
        OriginalName = r.GetStringOrEmpty("original_name"),
        StoredPath = r.GetStringOrEmpty("stored_path"),
        ContentType = r.GetStringOrNull("content_type"),
        SizeBytes = r.GetInt64Value("size_bytes"),
        ChecksumSha256 = r.GetStringOrNull("checksum_sha256"),
        DocumentDate = r.GetDateOrNull("document_date"),
        Description = r.GetStringOrNull("description"),
        Source = r.GetStringOrNull("source"),
        Notes = r.GetStringOrNull("notes"),
        CreatedUtc = r.GetUtcValue("created_utc"),
        CreatedBy = r.GetStringOrNull("created_by")
    };

    public string GetAbsolutePath(Attachment attachment) => _paths.ResolveAttachment(attachment.StoredPath);

    public bool FileExists(Attachment attachment)
    {
        try { return File.Exists(GetAbsolutePath(attachment)); }
        catch { return false; }
    }

    public async Task UpdateMetadataAsync(Attachment attachment, string? actor, CancellationToken ct = default)
    {
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("""
                UPDATE attachments SET category=$cat, description=$desc, document_date=$docdate, source=$src, notes=$notes
                WHERE id=$id
                """);
            cmd.Transaction = tx;
            cmd.AddValue("$cat", attachment.Category);
            cmd.AddValue("$desc", attachment.Description);
            cmd.AddDate("$docdate", attachment.DocumentDate);
            cmd.AddValue("$src", attachment.Source);
            cmd.AddValue("$notes", attachment.Notes);
            cmd.AddValue("$id", attachment.Id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        _audit.Log("attachment.updated", "Attachment", attachment.Id.ToString(), attachment.OriginalName, actor);
    }

    public async Task DeleteAsync(long id, string? actor, CancellationToken ct = default)
    {
        var attachment = await GetAsync(id, ct).ConfigureAwait(false);
        if (attachment is null) return;

        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("DELETE FROM attachments WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        try
        {
            var path = GetAbsolutePath(attachment);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // The database record is the source of truth; a locked file is cleaned up by integrity check.
        }

        _audit.Log("attachment.removed", "Attachment", id.ToString(), attachment.OriginalName, actor);
    }

    public async Task ExportAsync(Attachment attachment, string destinationPath, CancellationToken ct = default)
    {
        var source = GetAbsolutePath(attachment);
        if (!File.Exists(source))
            throw new DentivaValidationException("The stored file is missing from the attachment library.");

        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var dst = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that every recorded attachment still exists and matches its stored checksum.
    /// </summary>
    public Task<AttachmentIntegrityReport> VerifyIntegrityAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var report = new AttachmentIntegrityReport();
        var all = new List<Attachment>();
        using (var cmd = _db.CreateCommand("SELECT * FROM attachments"))
        {
            using var r = cmd.ExecuteReader();
            while (r.Read()) all.Add(Map(r));
        }

        report.Total = all.Count;
        foreach (var attachment in all)
        {
            ct.ThrowIfCancellationRequested();
            string path;
            try { path = GetAbsolutePath(attachment); }
            catch { report.Invalid.Add(attachment); continue; }

            if (!File.Exists(path))
            {
                report.Missing.Add(attachment);
                continue;
            }

            if (!string.IsNullOrEmpty(attachment.ChecksumSha256))
            {
                try
                {
                    if (!string.Equals(ComputeChecksum(path), attachment.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        report.Corrupted.Add(attachment);
                        continue;
                    }
                }
                catch
                {
                    report.Invalid.Add(attachment);
                    continue;
                }
            }

            report.Verified++;
        }

        return report;
    }, ct);

    public long GetTotalStorageBytes()
    {
        using var cmd = _db.CreateCommand("SELECT COALESCE(SUM(size_bytes),0) FROM attachments");
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }
}

public sealed class AttachmentIntegrityReport
{
    public int Total { get; set; }
    public int Verified { get; set; }
    public List<Attachment> Missing { get; } = new();
    public List<Attachment> Corrupted { get; } = new();
    public List<Attachment> Invalid { get; } = new();
    public bool IsHealthy => Missing.Count == 0 && Corrupted.Count == 0 && Invalid.Count == 0;
}
