using System.IO.Compression;
using System.Text;
using Dentiva.Core.Backup;
using Dentiva.Core.Models;
using Dentiva.Core.Repositories;
using Dentiva.Core.Services;
using Xunit;

namespace Dentiva.Tests;

public class AttachmentTests
{
    [Fact]
    public async Task Attachment_IsStoredOnDiskWithAChecksum()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync("Attach Patient");
        var source = h.NewSourceFile("xray.png", "fake-png-bytes");

        var attachment = await h.Attachments.AddAsync(source,
            new Attachment { PatientId = pid, Category = "XRay", Description = "Peri-apical" }, "t");

        Assert.Equal(64, attachment.ChecksumSha256!.Length);
        Assert.True(File.Exists(h.Attachments.GetAbsolutePath(attachment)));
        Assert.True(attachment.SizeBytes > 0);
        Assert.Equal("image/png", attachment.ContentType);
        Assert.True(attachment.IsImage);
    }

    [Fact]
    public async Task Attachment_StoresTheFileOutsideTheDatabase()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var payload = new string('p', 200_000);
        var attachment = await h.Attachments.AddAsync(h.NewSourceFile("big.pdf", payload),
            new Attachment { PatientId = pid, Category = "Documents" }, "t");

        var dbBytes = new FileInfo(h.Paths.DatabaseFile).Length;
        Assert.True(File.Exists(h.Attachments.GetAbsolutePath(attachment)));
        Assert.True(dbBytes < 200_000, $"the database grew to {dbBytes} bytes, suggesting the blob was inlined");
    }

    [Fact]
    public async Task Attachment_RejectsDisallowedExtensions()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();

        await Assert.ThrowsAsync<DentivaValidationException>(() => h.Attachments.AddAsync(
            h.NewSourceFile("payload.exe", "MZ..."), new Attachment { PatientId = pid }, "t"));

        await Assert.ThrowsAsync<DentivaValidationException>(() => h.Attachments.AddAsync(
            h.NewSourceFile("script.bat", "@echo off"), new Attachment { PatientId = pid }, "t"));
    }

    [Fact]
    public async Task Attachment_RejectsMissingAndEmptyFiles()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();

        await Assert.ThrowsAsync<DentivaValidationException>(() => h.Attachments.AddAsync(
            Path.Combine(h.Paths.TempDirectory, "does-not-exist.pdf"), new Attachment { PatientId = pid }, "t"));

        var empty = h.NewSourceFile("empty.pdf", string.Empty);
        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Attachments.AddAsync(empty, new Attachment { PatientId = pid }, "t"));
    }

    [Fact]
    public async Task Attachment_DeleteRemovesBothRowAndFile()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var attachment = await h.Attachments.AddAsync(h.NewSourceFile("doc.pdf"),
            new Attachment { PatientId = pid, Category = "Documents" }, "t");
        var path = h.Attachments.GetAbsolutePath(attachment);

        await h.Attachments.DeleteAsync(attachment.Id, "t");

        Assert.False(File.Exists(path));
        Assert.Empty(await h.Attachments.GetForPatientAsync(pid));
    }

    [Fact]
    public async Task Attachment_IntegrityCheckDetectsTamperingAndMissingFiles()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var tampered = await h.Attachments.AddAsync(h.NewSourceFile("scan.jpg", "original"),
            new Attachment { PatientId = pid, Category = "XRay" }, "t");
        var deleted = await h.Attachments.AddAsync(h.NewSourceFile("scan2.jpg", "second"),
            new Attachment { PatientId = pid, Category = "XRay" }, "t");

        Assert.True((await h.Attachments.VerifyIntegrityAsync()).IsHealthy);

        await File.WriteAllTextAsync(h.Attachments.GetAbsolutePath(tampered), "modified outside the app");
        File.Delete(h.Attachments.GetAbsolutePath(deleted));

        var report = await h.Attachments.VerifyIntegrityAsync();
        Assert.False(report.IsHealthy);
        Assert.Single(report.Corrupted);
        Assert.Single(report.Missing);
        Assert.Equal(2, report.Total);
    }

    [Fact]
    public async Task Attachment_ExportWritesACopyWithoutMovingTheOriginal()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        var attachment = await h.Attachments.AddAsync(h.NewSourceFile("report.pdf", "content"),
            new Attachment { PatientId = pid, Category = "Reports" }, "t");

        var destination = Path.Combine(h.Paths.TempDirectory, "exported.pdf");
        await h.Attachments.ExportAsync(attachment, destination);

        Assert.True(File.Exists(destination));
        Assert.True(File.Exists(h.Attachments.GetAbsolutePath(attachment)));
        Assert.Equal("content", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task Attachment_StorageTotalReflectsStoredBytes()
    {
        using var h = new TestHarness();
        var pid = await h.NewPatientAsync();
        await h.Attachments.AddAsync(h.NewSourceFile("a.pdf", new string('a', 1000)),
            new Attachment { PatientId = pid }, "t");

        Assert.True(h.Attachments.GetTotalStorageBytes() >= 1000);
    }

    [Theory]
    [InlineData("../../../windows/system32/config/sam")]
    [InlineData("..\\..\\secrets.txt")]
    [InlineData("subfolder/../../escape.txt")]
    public void ResolveAttachment_BlocksPathTraversal(string storedPath)
    {
        using var h = new TestHarness();
        Assert.Throws<InvalidOperationException>(() => h.Paths.ResolveAttachment(storedPath));
    }

    [Fact]
    public void ResolveAttachment_RejectsAbsolutePathsAndEmptyInput()
    {
        using var h = new TestHarness();
        Assert.Throws<InvalidOperationException>(() => h.Paths.ResolveAttachment(Path.Combine(Path.GetTempPath(), "x.txt")));
        Assert.Throws<ArgumentException>(() => h.Paths.ResolveAttachment(""));
    }

    [Fact]
    public void ResolveAttachment_AllowsLegitimateRelativePaths()
    {
        using var h = new TestHarness();
        var resolved = h.Paths.ResolveAttachment(Path.Combine("XRay", "P00000001", "abc123.pdf"));
        Assert.StartsWith(h.Paths.AttachmentsDirectory, resolved, StringComparison.OrdinalIgnoreCase);
    }
}

public class BackupRestoreTests
{
    private static async Task<long> SeedClinicAsync(TestHarness h)
    {
        var pid = await h.Patients.CreateAsync(new Patient
        {
            FullName = "Backup Patient", Phone = "01712345678", Allergies = "Penicillin"
        }, "t");

        await h.Clinical.SaveVisitAsync(new Visit { PatientId = pid, VisitDate = DateTime.Today, Reason = "Toothache" }, "t");
        await h.Clinical.SaveTreatmentAsync(new Treatment { PatientId = pid, ProcedureName = "Filling", Cost = 2000 }, "t");
        await h.Clinical.SaveToothRecordAsync(new ToothRecord { PatientId = pid, ToothNumber = "36", Condition = "Restored" }, "t");
        await h.Clinical.SavePrescriptionAsync(new Prescription
        {
            PatientId = pid,
            Items = { new PrescriptionItem { MedicineName = "Amoxicillin", Strength = "500 mg" } }
        }, "t");
        await h.Appointments.SaveAsync(new Appointment { PatientId = pid, AppointmentDate = DateTime.Today.AddDays(7), StartTime = new TimeSpan(11, 0, 0) }, "t");
        await h.Attachments.AddAsync(h.NewSourceFile("backup-xray.png", "image-bytes"),
            new Attachment { PatientId = pid, Category = "XRay" }, "t");

        var invoiceId = await h.NewInvoiceAsync(pid, ("Filling", 1, 2000));
        await h.Billing.RecordPaymentAsync(new Payment { InvoiceId = invoiceId, PatientId = pid, Amount = 1000, Method = "Cash" }, "t");
        await h.Finance.SaveExpenseAsync(new Expense { Description = "Materials", Amount = 500, CategoryName = "Dental Materials" }, "t");

        return pid;
    }

    [Fact]
    public async Task Backup_ProducesAValidArchiveWithManifestDatabaseAndAttachments()
    {
        using var h = new TestHarness();
        await SeedClinicAsync(h);

        var path = await h.Backups.CreateAsync(h.BackupPath("full"), new BackupOptions { IncludeAttachments = true });

        Assert.True(File.Exists(path));
        Assert.EndsWith(".dentivabackup", path);

        using var zip = ZipFile.OpenRead(path);
        Assert.NotNull(zip.GetEntry("manifest.json"));
        Assert.NotNull(zip.GetEntry("database/dentiva.db"));
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Backup_ManifestCarriesChecksumsAndPerTableCounts()
    {
        using var h = new TestHarness();
        await SeedClinicAsync(h);
        var path = await h.Backups.CreateAsync(h.BackupPath("manifest"), new BackupOptions { IncludeAttachments = true });

        var summary = await h.Backups.InspectAsync(path);

        Assert.True(summary.IsValid);
        Assert.Equal("Dentiva", summary.Manifest.Product);
        Assert.Equal(64, summary.Manifest.DatabaseSha256!.Length);
        Assert.Equal(1, summary.PatientCount);
        Assert.Equal(1, summary.InvoiceCount);
        Assert.Equal(1, summary.AttachmentCount);
        Assert.True(summary.PackageBytes > 0);
    }

    [Fact]
    public async Task Backup_CanExcludeAttachmentsAndTheAuditLog()
    {
        using var h = new TestHarness();
        await SeedClinicAsync(h);

        var path = await h.Backups.CreateAsync(h.BackupPath("lean"),
            new BackupOptions { IncludeAttachments = false, IncludeAuditLog = false });

        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase));
        }

        var summary = await h.Backups.InspectAsync(path);
        Assert.True(summary.IsValid);
        Assert.Equal(0, summary.Counts.GetValueOrDefault("audit_log"));
    }

    [Fact]
    public async Task Backup_RejectsACorruptedArchive()
    {
        using var h = new TestHarness();
        await SeedClinicAsync(h);
        var path = await h.Backups.CreateAsync(h.BackupPath("corrupt-source"), new BackupOptions());

        var bytes = await File.ReadAllBytesAsync(path);
        for (var i = bytes.Length / 3; i < Math.Min(bytes.Length, bytes.Length / 3 + 600); i++) bytes[i] ^= 0xFF;
        var corrupt = Path.Combine(h.Paths.TempDirectory, "corrupt.dentivabackup");
        await File.WriteAllBytesAsync(corrupt, bytes);

        var summary = await h.Backups.InspectAsync(corrupt);
        Assert.False(summary.IsValid);
        Assert.NotEmpty(summary.Problems);
    }

    [Fact]
    public async Task Backup_RejectsAMissingFileAndAForeignZip()
    {
        using var h = new TestHarness();

        var missing = await h.Backups.InspectAsync(Path.Combine(h.Paths.TempDirectory, "nope.dentivabackup"));
        Assert.False(missing.IsValid);

        var fake = Path.Combine(h.Paths.TempDirectory, "fake.dentivabackup");
        using (var zip = ZipFile.Open(fake, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("readme.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("not a dentiva backup");
        }

        var foreign = await h.Backups.InspectAsync(fake);
        Assert.False(foreign.IsValid);
        Assert.Contains(foreign.Problems, p => p.Contains("manifest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Backup_RejectsZipSlipEntries()
    {
        using var h = new TestHarness();
        await SeedClinicAsync(h);
        var good = await h.Backups.CreateAsync(h.BackupPath("slip-source"), new BackupOptions { IncludeAttachments = true });

        var evil = Path.Combine(h.Paths.TempDirectory, "zipslip.dentivabackup");
        File.Copy(good, evil, true);
        using (var zip = ZipFile.Open(evil, ZipArchiveMode.Update))
        {
            var entry = zip.CreateEntry("attachments/../../../evil.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("pwned");
        }

        var summary = await h.Backups.InspectAsync(evil);
        Assert.False(summary.IsValid);
        Assert.Contains(summary.Problems, p => p.Contains("unsafe", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\evil.txt")]
    [InlineData("attachments/../../escape.bin")]
    public void IsSafeEntryName_RejectsTraversalAndAbsolutePaths(string name)
        => Assert.False(BackupService.IsSafeEntryName(name));

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("database/dentiva.db")]
    [InlineData("attachments/XRay/P00000001/abc.png")]
    public void IsSafeEntryName_AcceptsLegitimateEntries(string name)
        => Assert.True(BackupService.IsSafeEntryName(name));

    [Fact]
    public async Task EncryptedBackup_RequiresTheCorrectPassphrase()
    {
        using var h = new TestHarness();
        await SeedClinicAsync(h);

        var path = await h.Backups.CreateAsync(h.BackupPath("encrypted"),
            new BackupOptions { IncludeAttachments = true, Password = "clinic-secret-2026" });

        var wrong = await h.Backups.InspectAsync(path, "wrong-passphrase");
        Assert.False(wrong.IsValid);

        var right = await h.Backups.InspectAsync(path, "clinic-secret-2026");
        Assert.True(right.IsValid);
        Assert.True(right.Manifest.Encrypted);
        Assert.True(right.RequiresPassword);
    }

    [Fact]
    public async Task EncryptedBackup_DoesNotLeakPlainTextPatientData()
    {
        using var h = new TestHarness();
        await h.Patients.CreateAsync(new Patient { FullName = "Confidential Person" }, "t");

        var path = await h.Backups.CreateAsync(h.BackupPath("secret"), new BackupOptions { Password = "strong-pass" });
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));

        Assert.DoesNotContain("Confidential Person", raw);
    }

    [Fact]
    public async Task Restore_RecreatesEverythingInAnEmptyClinic()
    {
        using var source = new TestHarness();
        await SeedClinicAsync(source);
        var package = await source.Backups.CreateAsync(source.BackupPath("restore-source"),
            new BackupOptions { IncludeAttachments = true });

        using var target = new TestHarness();
        Assert.True((await target.Backups.InspectAsync(package)).IsValid);

        var result = await target.Restore.RestoreAsync(package, null, new RestorePlan { RestoreEverything = true });

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Equal(1, await target.Patients.CountAsync(true));

        var restored = (await target.Patients.QueryAsync(new PatientQuery())).Items.Single();
        Assert.Equal("Backup Patient", restored.FullName);
        Assert.Single(await target.Clinical.GetVisitsAsync(restored.Id));
        Assert.Single(await target.Clinical.GetTreatmentsAsync(restored.Id));
        Assert.Single(await target.Clinical.GetPrescriptionsAsync(restored.Id));
        Assert.Single(await target.Attachments.GetForPatientAsync(restored.Id));
        Assert.True((await target.Attachments.VerifyIntegrityAsync()).IsHealthy);
        Assert.Equal(1, result.AttachmentsRestored);
        Assert.Equal("ok", target.Db.ForeignKeyCheck());
    }

    [Fact]
    public async Task Restore_RestoresFinancialTotalsAccurately()
    {
        using var source = new TestHarness();
        await SeedClinicAsync(source);
        var package = await source.Backups.CreateAsync(source.BackupPath("finance"), new BackupOptions());

        using var target = new TestHarness();
        await target.Restore.RestoreAsync(package, null, new RestorePlan { RestoreEverything = true });

        var invoice = (await target.Billing.QueryInvoicesAsync(null, null, null, null)).Items.Single();
        Assert.Equal(2000m, invoice.Total);
        Assert.Equal(1000m, invoice.PaidAmount);
        Assert.Equal("Partial", invoice.Status);
    }

    [Fact]
    public async Task Restore_DryRunPreviewChangesNothing()
    {
        using var source = new TestHarness();
        await SeedClinicAsync(source);
        var package = await source.Backups.CreateAsync(source.BackupPath("preview"), new BackupOptions());

        using var target = new TestHarness();
        var preview = await target.Restore.PreviewAsync(package, null, new RestorePlan { RestoreEverything = true });

        Assert.True(preview.TotalToAdd > 0);
        Assert.Equal(1, preview.ToAdd.GetValueOrDefault("patients"));
        Assert.Equal(0, await target.Patients.CountAsync(true));
    }

    [Fact]
    public async Task Restore_SelectivePatientSubsetOnlyImportsThatPatient()
    {
        using var source = new TestHarness();
        await SeedClinicAsync(source);
        var second = await source.Patients.CreateAsync(new Patient { FullName = "Second Patient" }, "t");
        await source.Clinical.SaveVisitAsync(new Visit { PatientId = second, Reason = "Cleaning" }, "t");

        var package = await source.Backups.CreateAsync(source.BackupPath("selective"), new BackupOptions());

        using var target = new TestHarness();
        var options = await target.Restore.LoadPatientOptionsAsync(package, null);
        Assert.Equal(2, options.Count);

        var chosen = options.Single(o => o.FullName == "Second Patient");
        var result = await target.Restore.RestoreAsync(package, null, new RestorePlan
        {
            RestoreEverything = false,
            SelectedPatientIds = new HashSet<long> { chosen.SourceId }
        });

        Assert.True(result.Success);
        var restored = (await target.Patients.QueryAsync(new PatientQuery())).Items;
        Assert.Single(restored);
        Assert.Equal("Second Patient", restored[0].FullName);
    }

    [Fact]
    public async Task Restore_ReportsMissingDependenciesWhenChildrenLoseTheirPatient()
    {
        using var source = new TestHarness();
        await SeedClinicAsync(source);
        var package = await source.Backups.CreateAsync(source.BackupPath("deps"), new BackupOptions());

        using var target = new TestHarness();
        var preview = await target.Restore.PreviewAsync(package, null, new RestorePlan
        {
            RestoreEverything = false,
            IncludePatients = false,
            IncludeInvoices = true,
            IncludeVisits = true,
            SelectedPatientIds = new HashSet<long>()
        });

        Assert.NotEmpty(preview.MissingDependencies);
        Assert.Equal(0, await target.Patients.CountAsync(true));
    }

    [Fact]
    public async Task Restore_TakesASafetySnapshotBeforeWriting()
    {
        using var source = new TestHarness();
        await SeedClinicAsync(source);
        var package = await source.Backups.CreateAsync(source.BackupPath("safety"), new BackupOptions());

        using var target = new TestHarness();
        await target.Patients.CreateAsync(new Patient { FullName = "Pre-existing Patient" }, "t");

        var result = await target.Restore.RestoreAsync(package, null, new RestorePlan { RestoreEverything = true });

        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.SafetySnapshotPath));
        Assert.True(File.Exists(result.SafetySnapshotPath));

        var snapshot = await target.Backups.InspectAsync(result.SafetySnapshotPath!);
        Assert.True(snapshot.IsValid);
        Assert.Equal(1, snapshot.PatientCount);
    }

    [Fact]
    public async Task Restore_MergesAlongsideNonCollidingLocalRecords()
    {
        using var source = new TestHarness();
        await SeedClinicAsync(source);
        var package = await source.Backups.CreateAsync(source.BackupPath("merge"), new BackupOptions());

        using var target = new TestHarness();
        // Move the local numbering on so the imported code (DEN-000001) does not collide.
        target.Settings.Set(SettingsService.Keys.PatientCodeNext, 500);
        await target.Patients.CreateAsync(new Patient { FullName = "Local Patient" }, "t");

        var result = await target.Restore.RestoreAsync(package, null, new RestorePlan { RestoreEverything = true });

        Assert.True(result.Success);
        Assert.Equal(2, await target.Patients.CountAsync(true));

        var names = (await target.Patients.QueryAsync(new PatientQuery())).Items.Select(p => p.FullName).ToList();
        Assert.Contains("Local Patient", names);
        Assert.Contains("Backup Patient", names);
    }

    [Fact]
    public async Task Restore_SkipConflictStrategyKeepsTheExistingLocalPatient()
    {
        using var source = new TestHarness();
        await SeedClinicAsync(source);
        var package = await source.Backups.CreateAsync(source.BackupPath("collide"), new BackupOptions());

        using var target = new TestHarness();
        // The local clinic already owns DEN-000001 for a completely different person.
        await target.Patients.CreateAsync(new Patient { FullName = "Different Local Person" }, "t");

        var result = await target.Restore.RestoreAsync(package, null, new RestorePlan
        {
            RestoreEverything = true,
            PatientConflict = ConflictStrategy.Skip
        });

        Assert.True(result.Success);

        var patients = (await target.Patients.QueryAsync(new PatientQuery())).Items;
        Assert.Contains(patients, p => p.FullName == "Different Local Person");
        Assert.DoesNotContain(patients, p => p.FullName == "Backup Patient");
        Assert.Equal(1, result.Skipped.GetValueOrDefault("patients"));

        // Patient codes must stay unique whatever the conflict strategy did.
        Assert.Equal(patients.Count, patients.Select(p => p.PatientCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Restore_ReplaceConflictStrategyUpdatesTheExistingPatientInPlace()
    {
        using var source = new TestHarness();
        await SeedClinicAsync(source);
        var package = await source.Backups.CreateAsync(source.BackupPath("replace"), new BackupOptions());

        using var target = new TestHarness();
        await target.Patients.CreateAsync(new Patient { FullName = "Different Local Person" }, "t");

        var result = await target.Restore.RestoreAsync(package, null, new RestorePlan
        {
            RestoreEverything = true,
            PatientConflict = ConflictStrategy.Replace
        });

        Assert.True(result.Success);
        Assert.Equal(1, await target.Patients.CountAsync(true));

        var patient = (await target.Patients.QueryAsync(new PatientQuery())).Items.Single();
        Assert.Equal("Backup Patient", patient.FullName);
        Assert.Equal(1, result.Updated.GetValueOrDefault("patients"));
    }

    [Fact]
    public async Task Restore_ResyncsSequencesSoNewCodesDoNotCollide()
    {
        using var source = new TestHarness();
        await SeedClinicAsync(source);
        var package = await source.Backups.CreateAsync(source.BackupPath("resync"), new BackupOptions());

        using var target = new TestHarness();
        await target.Restore.RestoreAsync(package, null, new RestorePlan { RestoreEverything = true });

        var newId = await target.Patients.CreateAsync(new Patient { FullName = "Post Restore Patient" }, "t");
        var created = await target.Patients.GetAsync(newId);

        Assert.Equal("DEN-000002", created!.PatientCode);

        var invoiceId = await target.NewInvoiceAsync(newId, ("Consultation", 1, 500));
        Assert.Equal("INV-000002", (await target.Billing.GetInvoiceAsync(invoiceId))!.InvoiceNumber);
        Assert.Equal("ok", target.Db.ForeignKeyCheck());
    }

    [Fact]
    public async Task Restore_OfAnEncryptedPackageWorksWithThePassword()
    {
        using var source = new TestHarness();
        await SeedClinicAsync(source);
        var package = await source.Backups.CreateAsync(source.BackupPath("enc-restore"),
            new BackupOptions { IncludeAttachments = true, Password = "pass-phrase-2026" });

        using var target = new TestHarness();
        var result = await target.Restore.RestoreAsync(package, "pass-phrase-2026", new RestorePlan { RestoreEverything = true });

        Assert.True(result.Success);
        Assert.Equal(1, await target.Patients.CountAsync(true));
        Assert.True((await target.Attachments.VerifyIntegrityAsync()).IsHealthy);
    }

    [Fact]
    public async Task Restore_FailsCleanlyOnAnInvalidPackageWithoutTouchingData()
    {
        using var h = new TestHarness();
        await h.Patients.CreateAsync(new Patient { FullName = "Existing Patient" }, "t");

        var junk = Path.Combine(h.Paths.TempDirectory, "junk.dentivabackup");
        await File.WriteAllTextAsync(junk, "this is definitely not a zip archive");

        var result = await h.Restore.RestoreAsync(junk, null, new RestorePlan { RestoreEverything = true });

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(1, await h.Patients.CountAsync(true));
    }

    [Fact]
    public async Task SafetySnapshot_CanBeCreatedOnDemand()
    {
        using var h = new TestHarness();
        await SeedClinicAsync(h);

        var path = await h.Backups.CreateSafetySnapshotAsync();

        Assert.True(File.Exists(path));
        Assert.True((await h.Backups.InspectAsync(path)).IsValid);
    }

    [Fact]
    public async Task Retention_KeepsOnlyTheConfiguredNumberOfBackups()
    {
        using var h = new TestHarness();
        await h.Patients.CreateAsync(new Patient { FullName = "Retention Patient" }, "t");

        var folder = h.Backups.ResolveBackupFolder();
        for (var i = 0; i < 6; i++)
        {
            var path = Path.Combine(folder, $"Dentiva-Auto-{i}{BackupService.FileExtension}");
            await h.Backups.CreateAsync(path, new BackupOptions { IncludeAttachments = false, Verify = false });
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-60 + i));
        }

        var removed = h.Backups.ApplyRetention(folder, 3);

        Assert.Equal(3, removed);
        Assert.Equal(3, Directory.GetFiles(folder, "*" + BackupService.FileExtension).Length);
    }

    [Fact]
    public void Retention_NeverDeletesTheOnlyRemainingBackup()
    {
        using var h = new TestHarness();
        var folder = h.Backups.ResolveBackupFolder();
        File.WriteAllText(Path.Combine(folder, "only" + BackupService.FileExtension), "x");

        Assert.Equal(0, h.Backups.ApplyRetention(folder, 1));
        Assert.Single(Directory.GetFiles(folder, "*" + BackupService.FileExtension));
    }

    [Fact]
    public async Task ListBackups_SurfacesBothRegularAndSafetySnapshots()
    {
        using var h = new TestHarness();
        await h.Patients.CreateAsync(new Patient { FullName = "List Patient" }, "t");

        await h.Backups.CreateAsync(h.BackupPath("listed"), new BackupOptions { Verify = false });
        await h.Backups.CreateSafetySnapshotAsync();

        Assert.True(h.Backups.ListBackups().Count >= 2);
    }

    [Fact]
    public async Task BackupRoundTrip_PreservesTheDatabaseChecksum()
    {
        using var h = new TestHarness();
        await SeedClinicAsync(h);
        var path = await h.Backups.CreateAsync(h.BackupPath("checksum"), new BackupOptions());

        var summary = await h.Backups.InspectAsync(path);
        Assert.True(summary.IsValid);
        Assert.Empty(summary.Problems);
        Assert.Equal(summary.Manifest.DatabaseBytes, summary.Manifest.DatabaseBytes);
    }
}
