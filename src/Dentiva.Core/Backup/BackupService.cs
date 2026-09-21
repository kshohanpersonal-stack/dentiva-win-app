using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dentiva.Core.Data;
using Dentiva.Core.Repositories;
using Dentiva.Core.Services;
using Microsoft.Data.Sqlite;

namespace Dentiva.Core.Backup;

/// <summary>
/// Creates and validates .dentivabackup packages.
///
/// Package layout (a ZIP container):
///   manifest.json          metadata, counts, checksums
///   database/dentiva.db    consistent snapshot produced with VACUUM INTO
///   attachments/...        the managed attachment tree, paths mirrored from the database
///
/// When a password is supplied the database snapshot and each attachment are encrypted with
/// AES-256-GCM using a PBKDF2-SHA256 derived key (no custom cryptography).
/// </summary>
public sealed class BackupService
{
    private const string ManifestEntry = "manifest.json";
    private const string DatabaseEntry = "database/dentiva.db";
    private const string AttachmentPrefix = "attachments/";
    private const int KdfIterations = 210_000;

    private readonly Database _db;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;

    public BackupService(Database db, AppPaths paths, SettingsService settings, AuditService audit)
    {
        _db = db;
        _paths = paths;
        _settings = settings;
        _audit = audit;
    }

    public static string FileExtension => ".dentivabackup";

    public string BuildDefaultFileName()
        => $"Dentiva-Backup-{DateTime.Now:yyyy-MM-dd-HHmm}{FileExtension}";

    public string ResolveBackupFolder()
    {
        var configured = _settings.Get(SettingsService.Keys.BackupFolder);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                Directory.CreateDirectory(configured);
                return configured;
            }
            catch
            {
                // Fall through to the managed default folder.
            }
        }

        Directory.CreateDirectory(_paths.BackupsDirectory);
        return _paths.BackupsDirectory;
    }

    // ------------------------------------------------------------------ create

    public async Task<string> CreateAsync(string destinationPath, BackupOptions options,
        IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new BackupOptions();
        progress?.Report(new BackupProgress("Preparing", 0.02));

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var workDir = Path.Combine(_paths.TempDirectory, "backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            // 1. Consistent database snapshot (VACUUM INTO also compacts and validates pages).
            progress?.Report(new BackupProgress("Creating database snapshot", 0.1));
            var snapshot = Path.Combine(workDir, "dentiva.db");
            await Task.Run(() =>
            {
                _db.Checkpoint();
                using var cmd = _db.CreateCommand("VACUUM INTO $path");
                cmd.AddValue("$path", snapshot);
                cmd.ExecuteNonQuery();
            }, ct).ConfigureAwait(false);

            var manifest = new BackupManifest
            {
                SchemaVersion = _db.SchemaVersion,
                CreatedBy = _audit.CurrentUser,
                ClinicName = _settings.Get(SettingsService.Keys.ClinicName),
                MachineName = SafeMachineName(),
                AppVersion = typeof(BackupService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                IncludesAttachments = options.IncludeAttachments,
                Encrypted = !string.IsNullOrEmpty(options.Password),
                KdfIterations = string.IsNullOrEmpty(options.Password) ? 0 : KdfIterations,
                Notes = options.Notes
            };

            progress?.Report(new BackupProgress("Reading record counts", 0.2));
            manifest.Counts = await Task.Run(() => CollectCounts(), ct).ConfigureAwait(false);

            if (!options.IncludeAuditLog)
            {
                await Task.Run(() => StripAuditLog(snapshot), ct).ConfigureAwait(false);
                manifest.Counts["audit_log"] = 0;
            }

            manifest.DatabaseBytes = new FileInfo(snapshot).Length;
            manifest.DatabaseSha256 = await Task.Run(() => Sha256File(snapshot), ct).ConfigureAwait(false);

            // 2. Attachment inventory.
            var attachments = new List<(string Relative, string Absolute)>();
            if (options.IncludeAttachments)
            {
                progress?.Report(new BackupProgress("Collecting attachments", 0.3));
                attachments = await Task.Run(() => CollectAttachmentFiles(), ct).ConfigureAwait(false);
                manifest.AttachmentCount = attachments.Count;
                manifest.AttachmentBytes = attachments.Sum(a => new FileInfo(a.Absolute).Length);
            }

            byte[]? salt = null;
            if (!string.IsNullOrEmpty(options.Password))
            {
                salt = RandomNumberGenerator.GetBytes(16);
                manifest.Notes = manifest.Notes;
            }

            // 3. Write the package.
            progress?.Report(new BackupProgress("Writing package", 0.4));
            var tempPackage = destinationPath + ".tmp";
            if (File.Exists(tempPackage)) File.Delete(tempPackage);

            await using (var zipStream = new FileStream(tempPackage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
                if (salt is not null)
                {
                    manifestJson = JsonSerializer.Serialize(new EncryptedManifestEnvelope
                    {
                        Encrypted = true,
                        KdfIterations = KdfIterations,
                        Salt = Convert.ToBase64String(salt),
                        Manifest = manifest
                    }, JsonOptions);
                }

                await WriteTextEntryAsync(zip, ManifestEntry, manifestJson, ct).ConfigureAwait(false);

                await WriteFileEntryAsync(zip, DatabaseEntry, snapshot, options.Password, salt, ct).ConfigureAwait(false);

                var done = 0;
                foreach (var (relative, absolute) in attachments)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!File.Exists(absolute)) continue;
                    await WriteFileEntryAsync(zip, AttachmentPrefix + relative.Replace('\\', '/'), absolute, options.Password, salt, ct).ConfigureAwait(false);
                    done++;
                    if (attachments.Count > 0 && done % 25 == 0)
                    {
                        progress?.Report(new BackupProgress("Writing attachments", 0.4 + 0.45 * done / attachments.Count, $"{done}/{attachments.Count}"));
                    }
                }
            }

            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(tempPackage, destinationPath);

            // 4. Verify what was just written.
            if (options.Verify)
            {
                progress?.Report(new BackupProgress("Verifying package", 0.92));
                var summary = await InspectAsync(destinationPath, options.Password, ct).ConfigureAwait(false);
                if (!summary.IsValid)
                {
                    throw new InvalidOperationException("The backup package failed verification: " + string.Join("; ", summary.Problems));
                }
            }

            _settings.Set(SettingsService.Keys.BackupLastUtc, DateTime.UtcNow.ToString("O"));
            _audit.Log("backup.created", "Backup", null, Path.GetFileName(destinationPath));
            progress?.Report(new BackupProgress("Completed", 1.0));
            return destinationPath;
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    private static string SafeMachineName()
    {
        try { return Environment.MachineName; } catch { return "unknown"; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private Dictionary<string, int> CollectCounts()
    {
        var tables = new[]
        {
            "patients", "visits", "treatments", "tooth_records", "prescriptions", "prescription_items",
            "referrals", "appointments", "invoices", "invoice_items", "payments", "expenses",
            "expense_categories", "treatment_categories", "staff", "salary_payments", "attachments",
            "audit_log", "users", "settings", "notifications", "payment_methods", "appointment_types"
        };

        var counts = new Dictionary<string, int>();
        foreach (var table in tables)
        {
            try
            {
                using var cmd = _db.CreateCommand($"SELECT COUNT(*) FROM {table}");
                counts[table] = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
            catch
            {
                counts[table] = 0;
            }
        }

        return counts;
    }

    private static void StripAuditLog(string snapshotPath)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshotPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM audit_log; VACUUM;";
        cmd.ExecuteNonQuery();
    }

    private List<(string Relative, string Absolute)> CollectAttachmentFiles()
    {
        var list = new List<(string, string)>();
        using var cmd = _db.CreateCommand("SELECT stored_path FROM attachments");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var relative = reader.GetString(0);
            try
            {
                var absolute = _paths.ResolveAttachment(relative);
                if (File.Exists(absolute))
                {
                    list.Add((relative, absolute));
                }
            }
            catch
            {
                // Skip unsafe/unknown paths rather than aborting the whole backup.
            }
        }

        return list;
    }

    private static async Task WriteTextEntryAsync(ZipArchive zip, string name, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct).ConfigureAwait(false);
    }

    private static async Task WriteFileEntryAsync(ZipArchive zip, string name, string sourcePath, string? password, byte[]? salt, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();

        if (string.IsNullOrEmpty(password) || salt is null)
        {
            await using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await src.CopyToAsync(entryStream, ct).ConfigureAwait(false);
            return;
        }

        var plaintext = await File.ReadAllBytesAsync(sourcePath, ct).ConfigureAwait(false);
        var encrypted = Encrypt(plaintext, password, salt);
        await entryStream.WriteAsync(encrypted, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ crypto

    private static byte[] DeriveKey(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, KdfIterations, HashAlgorithmName.SHA256, 32);

    private static byte[] Encrypt(byte[] plaintext, string password, byte[] salt)
    {
        var key = DeriveKey(password, salt);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Encrypt(nonce, plaintext, cipher, tag);
        }

        var result = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, result, nonce.Length + tag.Length, cipher.Length);
        CryptographicOperations.ZeroMemory(key);
        return result;
    }

    private static byte[] Decrypt(byte[] payload, string password, byte[] salt)
    {
        var nonceLength = AesGcm.NonceByteSizes.MaxSize;
        var tagLength = AesGcm.TagByteSizes.MaxSize;
        if (payload.Length < nonceLength + tagLength)
        {
            throw new InvalidDataException("The encrypted entry is truncated.");
        }

        var key = DeriveKey(password, salt);
        var nonce = payload.AsSpan(0, nonceLength).ToArray();
        var tag = payload.AsSpan(nonceLength, tagLength).ToArray();
        var cipher = payload.AsSpan(nonceLength + tagLength).ToArray();
        var plain = new byte[cipher.Length];

        using (var aes = new AesGcm(key, tagLength))
        {
            aes.Decrypt(nonce, cipher, tag, plain);
        }

        CryptographicOperations.ZeroMemory(key);
        return plain;
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ inspect

    /// <summary>
    /// Validates a package without modifying anything. Safe against ZIP path traversal (zip-slip)
    /// because entry names are checked before they are ever used to build a path.
    /// </summary>
    public Task<BackupSummary> InspectAsync(string packagePath, string? password = null, CancellationToken ct = default) => Task.Run(() =>
    {
        var summary = new BackupSummary { PackagePath = packagePath };

        if (!File.Exists(packagePath))
        {
            summary.Problems.Add("The backup file could not be found.");
            return summary;
        }

        summary.PackageBytes = new FileInfo(packagePath).Length;

        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(packagePath);
        }
        catch (InvalidDataException)
        {
            summary.Problems.Add("This file is not a readable Dentiva backup package. It may be corrupted or incomplete.");
            return summary;
        }

        using (zip)
        {
            var manifestEntry = zip.GetEntry(ManifestEntry);
            if (manifestEntry is null)
            {
                summary.Problems.Add("The package does not contain a manifest and cannot be trusted.");
                return summary;
            }

            BackupManifest? manifest;
            byte[]? salt = null;
            try
            {
                using var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8);
                var json = reader.ReadToEnd();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("encrypted", out var enc) && enc.ValueKind == JsonValueKind.True)
                {
                    var envelope = JsonSerializer.Deserialize<EncryptedManifestEnvelope>(json);
                    manifest = envelope?.Manifest;
                    salt = envelope?.Salt is { } s ? Convert.FromBase64String(s) : null;
                    summary.RequiresPassword = true;
                }
                else
                {
                    manifest = JsonSerializer.Deserialize<BackupManifest>(json);
                }
            }
            catch (JsonException)
            {
                summary.Problems.Add("The backup manifest is malformed.");
                return summary;
            }

            if (manifest is null)
            {
                summary.Problems.Add("The backup manifest could not be read.");
                return summary;
            }

            summary.Manifest = manifest;

            if (!string.Equals(manifest.Product, "Dentiva", StringComparison.OrdinalIgnoreCase))
            {
                summary.Problems.Add("This package was not produced by Dentiva.");
            }

            if (manifest.FormatVersion > BackupManifest.CurrentFormatVersion)
            {
                summary.Problems.Add($"The backup was created by a newer version of Dentiva (format {manifest.FormatVersion}). Update Dentiva to restore it.");
            }

            if (manifest.SchemaVersion > DatabaseSchema.CurrentVersion)
            {
                summary.Problems.Add($"The backup uses database schema {manifest.SchemaVersion}, which this version cannot read.");
            }

            var dbEntry = zip.GetEntry(DatabaseEntry);
            if (dbEntry is null)
            {
                summary.Problems.Add("The package does not contain a database snapshot.");
                return summary;
            }

            foreach (var entry in zip.Entries)
            {
                if (!IsSafeEntryName(entry.FullName))
                {
                    summary.Problems.Add($"The package contains an unsafe entry path and was rejected: {entry.FullName}");
                    return summary;
                }
            }

            if (summary.RequiresPassword && string.IsNullOrEmpty(password))
            {
                summary.Warnings.Add("This backup is password protected. Enter the password to validate and restore it.");
                summary.IsValid = summary.Problems.Count == 0;
                return summary;
            }

            // Verify the database snapshot checksum and that it actually opens.
            try
            {
                var temp = Path.Combine(_paths.TempDirectory, "inspect-" + Guid.NewGuid().ToString("N") + ".db");
                Directory.CreateDirectory(_paths.TempDirectory);
                ExtractEntry(dbEntry, temp, password, salt);

                try
                {
                    if (!string.IsNullOrEmpty(manifest.DatabaseSha256))
                    {
                        var actual = Sha256File(temp);
                        if (!string.Equals(actual, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            summary.Problems.Add("The database snapshot checksum does not match the manifest. The backup may be corrupted.");
                        }
                    }

                    using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Mode = SqliteOpenMode.ReadOnly }.ToString());
                    conn.Open();
                    using var check = conn.CreateCommand();
                    check.CommandText = "PRAGMA integrity_check;";
                    var integrity = check.ExecuteScalar()?.ToString();
                    if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        summary.Problems.Add("The database snapshot failed its integrity check.");
                    }

                    if (manifest.Counts.Count == 0)
                    {
                        manifest.Counts = CountsFrom(conn);
                    }
                }
                finally
                {
                    try { File.Delete(temp); } catch { /* ignore */ }
                }
            }
            catch (CryptographicException)
            {
                summary.Problems.Add("The backup could not be decrypted. Check the password.");
            }
            catch (Exception ex)
            {
                summary.Problems.Add("The database snapshot could not be read: " + ex.Message);
            }

            var attachmentEntries = zip.Entries.Count(e => e.FullName.StartsWith(AttachmentPrefix, StringComparison.OrdinalIgnoreCase) && e.Length > 0);
            if (manifest.IncludesAttachments && attachmentEntries < manifest.AttachmentCount)
            {
                summary.Warnings.Add($"The manifest lists {manifest.AttachmentCount} attachment(s) but the package contains {attachmentEntries}. Missing files will be reported during restore.");
            }
        }

        summary.IsValid = summary.Problems.Count == 0;
        return summary;
    }, ct);

    internal static Dictionary<string, int> CountsFrom(SqliteConnection conn)
    {
        var tables = new[]
        {
            "patients", "visits", "treatments", "tooth_records", "prescriptions", "referrals",
            "appointments", "invoices", "payments", "expenses", "staff", "salary_payments", "attachments", "users"
        };

        var counts = new Dictionary<string, int>();
        foreach (var table in tables)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
                counts[table] = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
            catch
            {
                counts[table] = 0;
            }
        }

        return counts;
    }

    /// <summary>Rejects absolute paths, drive letters and any traversal segment.</summary>
    public static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains("..", StringComparison.Ordinal)) return false;
        if (name.StartsWith('/') || name.StartsWith('\\')) return false;
        if (name.Length > 1 && name[1] == ':') return false;
        if (Path.IsPathRooted(name)) return false;
        return true;
    }

    internal static void ExtractEntry(ZipArchiveEntry entry, string destinationPath, string? password, byte[]? salt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        if (string.IsNullOrEmpty(password) || salt is null)
        {
            using var src = entry.Open();
            using var dst = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            src.CopyTo(dst);
            return;
        }

        using var ms = new MemoryStream();
        using (var src = entry.Open())
        {
            src.CopyTo(ms);
        }

        var plain = Decrypt(ms.ToArray(), password, salt);
        File.WriteAllBytes(destinationPath, plain);
    }

    // ------------------------------------------------------------------ safety snapshot

    /// <summary>
    /// Always called before a restore: captures the current database so the user can recover even if
    /// they chose to replace everything.
    /// </summary>
    public async Task<string> CreateSafetySnapshotAsync(CancellationToken ct = default)
    {
        var folder = Path.Combine(_paths.BackupsDirectory, "Safety");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Dentiva-SafetySnapshot-{DateTime.Now:yyyy-MM-dd-HHmmss}{FileExtension}");
        await CreateAsync(path, new BackupOptions { IncludeAttachments = true, IncludeAuditLog = true, Verify = false, Notes = "Automatic safety snapshot taken before a restore." }, null, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>Applies the configured retention policy, never deleting the last remaining backup.</summary>
    public int ApplyRetention(string folder, int keep)
    {
        if (keep <= 0) return 0;

        try
        {
            var files = new DirectoryInfo(folder)
                .GetFiles("*" + FileExtension)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            if (files.Count <= Math.Max(1, keep)) return 0;

            var removed = 0;
            foreach (var file in files.Skip(Math.Max(1, keep)))
            {
                try { file.Delete(); removed++; } catch { /* keep going */ }
            }

            return removed;
        }
        catch
        {
            return 0;
        }
    }

    public List<FileInfo> ListBackups()
    {
        var results = new List<FileInfo>();
        foreach (var folder in new[] { ResolveBackupFolder(), Path.Combine(_paths.BackupsDirectory, "Safety") })
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    results.AddRange(new DirectoryInfo(folder).GetFiles("*" + FileExtension));
                }
            }
            catch
            {
                // ignore unreadable folders
            }
        }

        return results.OrderByDescending(f => f.LastWriteTimeUtc).ToList();
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }
}

internal sealed class EncryptedManifestEnvelope
{
    public bool Encrypted { get; set; } = true;
    public int KdfIterations { get; set; }
    public string? Salt { get; set; }
    public BackupManifest? Manifest { get; set; }
}
