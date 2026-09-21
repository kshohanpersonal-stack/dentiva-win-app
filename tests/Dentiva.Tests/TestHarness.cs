using Dentiva.Core.Backup;
using Dentiva.Core.Data;
using Dentiva.Core.Documents;
using Dentiva.Core.Models;
using Dentiva.Core.Repositories;
using Dentiva.Core.Services;

namespace Dentiva.Tests;

/// <summary>
/// Spins up a fully wired Dentiva stack against a throwaway data directory, so the tests exercise
/// the real database, real migrations and real services rather than mocks.
/// </summary>
public sealed class TestHarness : IDisposable
{
    public TestHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "dentiva-tests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Root);
        Paths.EnsureCreated();

        Db = new Database(Paths);
        Db.Open();

        Settings = new SettingsService(Db);
        Settings.EnsureDefaults();
        Settings.Set(SettingsService.Keys.BackupFolder, Paths.BackupsDirectory);

        Audit = new AuditService(Db) { CurrentUser = "tester" };
        Sequences = new SequenceService(Db, Settings);
        new ReferenceDataSeeder(Db, Settings).Seed();

        Patients = new PatientRepository(Db, Sequences, Audit);
        Clinical = new ClinicalRepository(Db, Sequences, Audit);
        Billing = new BillingRepository(Db, Sequences, Audit, Settings);
        Appointments = new AppointmentRepository(Db, Audit, Settings);
        Finance = new FinanceRepository(Db, Audit);
        Staff = new StaffRepository(Db, Sequences, Audit);
        Attachments = new AttachmentService(Db, Paths, Audit);
        Users = new UserService(Db, Audit);
        Notifications = new NotificationService(Db, Paths);
        Dashboard = new DashboardService(Db);
        Export = new ExportService(Db, Paths, Audit);
        Documents = new DocumentService(Settings, Paths, Billing, Patients);
        Backups = new BackupService(Db, Paths, Settings, Audit);
        Restore = new RestoreService(Db, Paths, Backups, Settings, Sequences, Audit);
    }

    public string Root { get; }
    public AppPaths Paths { get; }
    public Database Db { get; }
    public SettingsService Settings { get; }
    public AuditService Audit { get; }
    public SequenceService Sequences { get; }
    public PatientRepository Patients { get; }
    public ClinicalRepository Clinical { get; }
    public BillingRepository Billing { get; }
    public AppointmentRepository Appointments { get; }
    public FinanceRepository Finance { get; }
    public StaffRepository Staff { get; }
    public AttachmentService Attachments { get; }
    public UserService Users { get; }
    public NotificationService Notifications { get; }
    public DashboardService Dashboard { get; }
    public ExportService Export { get; }
    public DocumentService Documents { get; }
    public BackupService Backups { get; }
    public RestoreService Restore { get; }

    // ---- convenience builders ------------------------------------------------

    public Task<long> NewPatientAsync(string name = "Rafiqul Islam", string? phone = "01712345678")
        => Patients.CreateAsync(new Patient { FullName = name, Phone = phone }, "tester");

    public async Task<long> NewInvoiceAsync(long patientId, params (string Description, decimal Qty, decimal Price)[] lines)
    {
        var invoice = new Invoice { PatientId = patientId, InvoiceDate = DateTime.Today };
        foreach (var (description, qty, price) in lines)
        {
            invoice.Items.Add(new InvoiceItem { Description = description, Quantity = qty, UnitPrice = price });
        }

        if (invoice.Items.Count == 0)
        {
            invoice.Items.Add(new InvoiceItem { Description = "Consultation", Quantity = 1, UnitPrice = 1000 });
        }

        return await Billing.SaveInvoiceAsync(invoice, "tester");
    }

    public string NewSourceFile(string name, string content = "dentiva test payload")
    {
        var path = Path.Combine(Paths.TempDirectory, Guid.NewGuid().ToString("N") + "-" + name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string BackupPath(string name) => Path.Combine(Paths.BackupsDirectory, name + BackupService.FileExtension);

    public void Dispose()
    {
        try { Db.Dispose(); } catch { /* the temp folder is removed regardless */ }
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { /* best effort on Windows file locks */ }
    }
}
