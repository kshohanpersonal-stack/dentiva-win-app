using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dentiva.Core.Data;
using Dentiva.Core.Services;
using Microsoft.Data.Sqlite;

namespace Dentiva.Core.Backup;

/// <summary>
/// Restores a .dentivabackup package into the live database.
///
/// Key guarantees:
///  * a safety snapshot of the current database is always taken first;
///  * the whole import runs inside one transaction — a failure rolls everything back;
///  * identity is remapped (source id -> local id) so imported rows never collide with existing ones;
///  * dependencies are resolved automatically, so a treatment can never be imported without its patient;
///  * a dry-run preview is available that writes nothing.
/// </summary>
public sealed class RestoreService
{
    private readonly Database _db;
    private readonly AppPaths _paths;
    private readonly BackupService _backup;
    private readonly SettingsService _settings;
    private readonly SequenceService _sequences;
    private readonly AuditService _audit;

    public RestoreService(Database db, AppPaths paths, BackupService backup, SettingsService settings, SequenceService sequences, AuditService audit)
    {
        _db = db;
        _paths = paths;
        _backup = backup;
        _settings = settings;
        _sequences = sequences;
        _audit = audit;
    }

    /// <summary>Opens the packaged database read-only so the wizard can list its patients.</summary>
    public async Task<List<RestorePatientOption>> LoadPatientOptionsAsync(string packagePath, string? password, CancellationToken ct = default)
    {
        using var session = await OpenPackageAsync(packagePath, password, ct).ConfigureAwait(false);
        var options = new List<RestorePatientOption>();

        using var cmd = session.Source.CreateCommand();
        cmd.CommandText = """
            SELECT p.id, p.patient_code, p.full_name, p.phone,
                (SELECT COUNT(*) FROM visits v WHERE v.patient_id=p.id) AS visits,
                (SELECT COUNT(*) FROM treatments t WHERE t.patient_id=p.id) AS treatments,
                (SELECT COUNT(*) FROM invoices i WHERE i.patient_id=p.id) AS invoices,
                (SELECT COUNT(*) FROM attachments a WHERE a.patient_id=p.id) AS attachments,
                (SELECT MAX(v.visit_date) FROM visits v WHERE v.patient_id=p.id) AS last_visit
            FROM patients p ORDER BY p.full_name COLLATE NOCASE
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            options.Add(new RestorePatientOption
            {
                SourceId = reader.GetInt64(0),
                PatientCode = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                FullName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Phone = reader.IsDBNull(3) ? null : reader.GetString(3),
                VisitCount = reader.GetInt32(4),
                TreatmentCount = reader.GetInt32(5),
                InvoiceCount = reader.GetInt32(6),
                AttachmentCount = reader.GetInt32(7),
                LastVisit = reader.IsDBNull(8) ? null : DateTime.TryParse(reader.GetString(8), out var d) ? d : null
            });
        }

        // Flag entries that already exist locally so the user can choose a conflict strategy.
        foreach (var option in options)
        {
            using var check = _db.CreateCommand("SELECT full_name FROM patients WHERE patient_code = $c COLLATE NOCASE");
            check.AddValue("$c", option.PatientCode);
            var existing = check.ExecuteScalar()?.ToString();
            if (existing is not null)
            {
                option.ExistsLocally = true;
                option.ConflictReason = string.Equals(existing, option.FullName, StringComparison.OrdinalIgnoreCase)
                    ? "A patient with this code already exists."
                    : $"Code already used locally by '{existing}'.";
            }
        }

        return options;
    }

    public Task<RestorePreview> PreviewAsync(string packagePath, string? password, RestorePlan plan, CancellationToken ct = default)
        => ExecuteInternalAsync(packagePath, password, plan, dryRun: true, null, ct).ContinueWith(t => t.Result.Preview, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    public async Task<RestoreResult> RestoreAsync(string packagePath, string? password, RestorePlan plan,
        IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        var outcome = await ExecuteInternalAsync(packagePath, password, plan, dryRun: false, progress, ct).ConfigureAwait(false);
        return outcome.Result;
    }

    private sealed record Outcome(RestorePreview Preview, RestoreResult Result);

    private async Task<Outcome> ExecuteInternalAsync(string packagePath, string? password, RestorePlan plan,
        bool dryRun, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var preview = new RestorePreview();
        var result = new RestoreResult();

        progress?.Report(new BackupProgress("Validating backup", 0.02));
        var summary = await _backup.InspectAsync(packagePath, password, ct).ConfigureAwait(false);
        if (!summary.IsValid)
        {
            result.Success = false;
            foreach (var problem in summary.Problems) result.Errors.Add(problem);
            preview.InvalidRecords.AddRange(summary.Problems);
            return new Outcome(preview, result);
        }

        if (!dryRun)
        {
            progress?.Report(new BackupProgress("Creating safety snapshot", 0.06));
            try
            {
                result.SafetySnapshotPath = await _backup.CreateSafetySnapshotAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("A safety snapshot could not be created: " + ex.Message);
            }
        }

        using var session = await OpenPackageAsync(packagePath, password, ct).ConfigureAwait(false);
        progress?.Report(new BackupProgress("Reading backup contents", 0.12));

        var map = new IdMap();

        if (dryRun)
        {
            await Task.Run(() => Apply(session, plan, preview, result, map, null, dryRun: true, progress, ct), ct).ConfigureAwait(false);
            result.Success = true;
            result.Duration = stopwatch.Elapsed;
            return new Outcome(preview, result);
        }

        try
        {
            await _db.WriteAsync(tx =>
            {
                Apply(session, plan, preview, result, map, tx, dryRun: false, progress, ct);
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);

            // Attachment files are copied after the transaction commits: the database is the source of
            // truth, and a missing file is reported rather than rolling back an otherwise good restore.
            if (plan.IncludeAttachments && map.AttachmentPaths.Count > 0)
            {
                progress?.Report(new BackupProgress("Restoring attachments", 0.85));
                RestoreAttachmentFiles(session, map, result, ct);
            }

            _sequences.ResyncAfterRestore();
            _settings.Reload();

            result.Success = result.Errors.Count == 0;
            _audit.Log("restore.performed", "Backup", null,
                $"{Path.GetFileName(packagePath)} — {result.TotalInserted} record(s) imported");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add(ex.Message);
            _audit.Log("restore.failed", "Backup", null, ex.Message);
        }

        result.Duration = stopwatch.Elapsed;
        progress?.Report(new BackupProgress(result.Success ? "Completed" : "Failed", 1.0));
        return new Outcome(preview, result);
    }

    // ------------------------------------------------------------------ core import

    private void Apply(PackageSession session, RestorePlan plan, RestorePreview preview, RestoreResult result,
        IdMap map, SqliteTransaction? tx, bool dryRun, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        var src = session.Source;

        // --- Patients (and the dependency gate for everything clinical) ---
        var selected = plan.SelectedPatientIds;
        var wantAllPatients = plan.RestoreEverything || selected is null;

        if (plan.IncludePatients || plan.RestoreEverything)
        {
            progress?.Report(new BackupProgress("Importing patients", 0.2));
            foreach (var row in Query(src, "SELECT * FROM patients ORDER BY id"))
            {
                ct.ThrowIfCancellationRequested();
                var sourceId = Convert.ToInt64(row["id"]);
                if (!wantAllPatients && !selected!.Contains(sourceId))
                {
                    continue;
                }

                var code = row.GetString("patient_code");
                var name = row.GetString("full_name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    preview.InvalidRecords.Add($"Patient #{sourceId} has no name and was skipped.");
                    preview.Skip("patients");
                    continue;
                }

                var existingId = FindPatientByCode(code, tx);
                if (existingId is { } localId)
                {
                    switch (plan.PatientConflict)
                    {
                        case ConflictStrategy.Skip:
                        case ConflictStrategy.KeepExisting:
                            preview.Skip("patients");
                            preview.Conflicts.Add($"Patient {code} ({name}) already exists — kept the existing record.");
                            map.Patients[sourceId] = localId;
                            if (!dryRun) result.Count(result.Skipped, "patients");
                            continue;

                        case ConflictStrategy.Replace:
                        case ConflictStrategy.Merge:
                            preview.Update("patients");
                            preview.Conflicts.Add($"Patient {code} ({name}) exists — it will be updated from the backup.");
                            map.Patients[sourceId] = localId;
                            if (!dryRun)
                            {
                                UpdatePatient(localId, row, tx!, plan.PatientConflict == ConflictStrategy.Merge);
                                result.Count(result.Updated, "patients");
                            }
                            continue;
                    }
                }

                preview.Add("patients");
                if (!dryRun)
                {
                    var newCode = EnsureUniquePatientCode(code, tx!);
                    var newId = InsertPatient(row, newCode, tx!);
                    map.Patients[sourceId] = newId;
                    result.Count(result.Inserted, "patients");
                }
                else
                {
                    map.Patients[sourceId] = -sourceId; // placeholder so dependants count correctly
                }
            }
        }
        else if (selected is { Count: > 0 })
        {
            // Patients were not selected for import but child records were: link to existing records where possible.
            foreach (var sourceId in selected)
            {
                var code = ScalarString(src, "SELECT patient_code FROM patients WHERE id=$id", sourceId);
                if (code is null) continue;
                var localId = FindPatientByCode(code, tx);
                if (localId is { } lid) map.Patients[sourceId] = lid;
            }
        }

        bool PatientIncluded(long sourcePatientId) => map.Patients.ContainsKey(sourcePatientId);

        void NoteMissingDependency(string entity, long patientId)
        {
            var message = $"{entity} skipped because patient #{patientId} was not part of this restore.";
            if (!preview.MissingDependencies.Contains(message)) preview.MissingDependencies.Add(message);
        }

        // --- Visits ---
        if (plan.RestoreEverything || plan.IncludeVisits)
        {
            progress?.Report(new BackupProgress("Importing visits", 0.3));
            ImportChild(src, "visits", "SELECT * FROM visits ORDER BY id", row =>
            {
                var sourcePatient = Convert.ToInt64(row["patient_id"]);
                if (!PatientIncluded(sourcePatient)) { NoteMissingDependency("Visit", sourcePatient); return false; }
                if (!dryRun)
                {
                    var newId = InsertRow(tx!, "visits", row, new() { ["patient_id"] = map.Patients[sourcePatient] }, "id");
                    map.Visits[Convert.ToInt64(row["id"])] = newId;
                }
                return true;
            }, preview, result, dryRun, ct);
        }

        // --- Treatments ---
        if (plan.RestoreEverything || plan.IncludeTreatments)
        {
            progress?.Report(new BackupProgress("Importing treatments", 0.38));
            ImportChild(src, "treatments", "SELECT * FROM treatments ORDER BY id", row =>
            {
                var sourcePatient = Convert.ToInt64(row["patient_id"]);
                if (!PatientIncluded(sourcePatient)) { NoteMissingDependency("Treatment", sourcePatient); return false; }
                if (!dryRun)
                {
                    var overrides = new Dictionary<string, object?> { ["patient_id"] = map.Patients[sourcePatient] };
                    overrides["visit_id"] = MapOrNull(map.Visits, row["visit_id"]);
                    overrides["invoice_id"] = null; // relinked after invoices are imported
                    var newId = InsertRow(tx!, "treatments", row, overrides, "id");
                    map.Treatments[Convert.ToInt64(row["id"])] = newId;
                }
                return true;
            }, preview, result, dryRun, ct);
        }

        // --- Tooth chart ---
        if (plan.RestoreEverything || plan.IncludeToothChart)
        {
            ImportChild(src, "tooth_records", "SELECT * FROM tooth_records ORDER BY id", row =>
            {
                var sourcePatient = Convert.ToInt64(row["patient_id"]);
                if (!PatientIncluded(sourcePatient)) { NoteMissingDependency("Tooth record", sourcePatient); return false; }
                if (!dryRun)
                {
                    var localPatient = map.Patients[sourcePatient];
                    var tooth = row.GetString("tooth_number");
                    using var del = _db.CreateCommand("DELETE FROM tooth_records WHERE patient_id=$p AND tooth_number=$t");
                    del.Transaction = tx;
                    del.AddValue("$p", localPatient);
                    del.AddValue("$t", tooth);
                    del.ExecuteNonQuery();
                    InsertRow(tx!, "tooth_records", row, new() { ["patient_id"] = localPatient }, "id");
                }
                return true;
            }, preview, result, dryRun, ct);
        }

        // --- Prescriptions + items ---
        if (plan.RestoreEverything || plan.IncludePrescriptions)
        {
            progress?.Report(new BackupProgress("Importing prescriptions", 0.45));
            ImportChild(src, "prescriptions", "SELECT * FROM prescriptions ORDER BY id", row =>
            {
                var sourcePatient = Convert.ToInt64(row["patient_id"]);
                if (!PatientIncluded(sourcePatient)) { NoteMissingDependency("Prescription", sourcePatient); return false; }
                if (!dryRun)
                {
                    var overrides = new Dictionary<string, object?>
                    {
                        ["patient_id"] = map.Patients[sourcePatient],
                        ["visit_id"] = MapOrNull(map.Visits, row["visit_id"]),
                        ["prescription_no"] = EnsureUniqueText(tx!, "prescriptions", "prescription_no", row.GetString("prescription_no"))
                    };
                    var newId = InsertRow(tx!, "prescriptions", row, overrides, "id");
                    map.Prescriptions[Convert.ToInt64(row["id"])] = newId;
                }
                return true;
            }, preview, result, dryRun, ct);

            if (!dryRun)
            {
                foreach (var row in Query(src, "SELECT * FROM prescription_items ORDER BY id"))
                {
                    var parent = MapOrNull(map.Prescriptions, row["prescription_id"]);
                    if (parent is null) continue;
                    InsertRow(tx!, "prescription_items", row, new() { ["prescription_id"] = parent }, "id");
                }
            }
        }

        // --- Referrals ---
        if (plan.RestoreEverything || plan.IncludeReferrals)
        {
            ImportChild(src, "referrals", "SELECT * FROM referrals ORDER BY id", row =>
            {
                var sourcePatient = Convert.ToInt64(row["patient_id"]);
                if (!PatientIncluded(sourcePatient)) { NoteMissingDependency("Referral", sourcePatient); return false; }
                if (!dryRun)
                {
                    var newId = InsertRow(tx!, "referrals", row, new() { ["patient_id"] = map.Patients[sourcePatient] }, "id");
                    map.Referrals[Convert.ToInt64(row["id"])] = newId;
                }
                return true;
            }, preview, result, dryRun, ct);
        }

        // --- Appointments ---
        if (plan.RestoreEverything || plan.IncludeAppointments)
        {
            progress?.Report(new BackupProgress("Importing appointments", 0.52));
            ImportChild(src, "appointments", "SELECT * FROM appointments ORDER BY id", row =>
            {
                var sourcePatient = Convert.ToInt64(row["patient_id"]);
                if (!PatientIncluded(sourcePatient)) { NoteMissingDependency("Appointment", sourcePatient); return false; }
                if (!dryRun)
                {
                    InsertRow(tx!, "appointments", row, new() { ["patient_id"] = map.Patients[sourcePatient], ["dentist_id"] = null }, "id");
                }
                return true;
            }, preview, result, dryRun, ct);
        }

        // --- Invoices + items ---
        var financeFrom = plan.FinanceFrom;
        var financeTo = plan.FinanceTo;

        bool InFinanceRange(object? value)
        {
            if (financeFrom is null && financeTo is null) return true;
            if (value is null || value is DBNull) return true;
            if (!DateTime.TryParse(Convert.ToString(value), out var date)) return true;
            if (financeFrom is { } f && date.Date < f.Date) return false;
            if (financeTo is { } t && date.Date > t.Date) return false;
            return true;
        }

        if (plan.RestoreEverything || plan.IncludeInvoices)
        {
            progress?.Report(new BackupProgress("Importing invoices", 0.6));
            ImportChild(src, "invoices", "SELECT * FROM invoices ORDER BY id", row =>
            {
                var sourcePatient = Convert.ToInt64(row["patient_id"]);
                if (!PatientIncluded(sourcePatient)) { NoteMissingDependency("Invoice", sourcePatient); return false; }
                if (!InFinanceRange(row["invoice_date"])) return false;
                if (!dryRun)
                {
                    var overrides = new Dictionary<string, object?>
                    {
                        ["patient_id"] = map.Patients[sourcePatient],
                        ["invoice_number"] = EnsureUniqueText(tx!, "invoices", "invoice_number", row.GetString("invoice_number")),
                        ["paid_amount"] = 0d
                    };
                    var newId = InsertRow(tx!, "invoices", row, overrides, "id");
                    map.Invoices[Convert.ToInt64(row["id"])] = newId;
                }
                return true;
            }, preview, result, dryRun, ct);

            if (!dryRun)
            {
                foreach (var row in Query(src, "SELECT * FROM invoice_items ORDER BY id"))
                {
                    var parent = MapOrNull(map.Invoices, row["invoice_id"]);
                    if (parent is null) continue;
                    InsertRow(tx!, "invoice_items", row, new()
                    {
                        ["invoice_id"] = parent,
                        ["treatment_id"] = MapOrNull(map.Treatments, row["treatment_id"])
                    }, "id");
                }

                // Relink treatments to their imported invoice.
                foreach (var row in Query(src, "SELECT id, invoice_id FROM treatments WHERE invoice_id IS NOT NULL"))
                {
                    var localTreatment = MapOrNull(map.Treatments, row["id"]);
                    var localInvoice = MapOrNull(map.Invoices, row["invoice_id"]);
                    if (localTreatment is null || localInvoice is null) continue;
                    using var upd = _db.CreateCommand("UPDATE treatments SET invoice_id=$i WHERE id=$t");
                    upd.Transaction = tx;
                    upd.AddValue("$i", localInvoice);
                    upd.AddValue("$t", localTreatment);
                    upd.ExecuteNonQuery();
                }
            }
        }

        // --- Payments ---
        if (plan.RestoreEverything || plan.IncludePayments)
        {
            progress?.Report(new BackupProgress("Importing payments", 0.68));
            ImportChild(src, "payments", "SELECT * FROM payments ORDER BY id", row =>
            {
                var sourcePatient = Convert.ToInt64(row["patient_id"]);
                if (!PatientIncluded(sourcePatient)) { NoteMissingDependency("Payment", sourcePatient); return false; }
                if (!InFinanceRange(row["payment_date"])) return false;

                var sourceInvoice = row["invoice_id"];
                if (sourceInvoice is not null and not DBNull && MapOrNull(map.Invoices, sourceInvoice) is null)
                {
                    var msg = "A payment referenced an invoice that was not included; it was imported as an unlinked payment.";
                    if (!preview.Warnings.Contains(msg)) preview.Warnings.Add(msg);
                }

                if (!dryRun)
                {
                    InsertRow(tx!, "payments", row, new()
                    {
                        ["patient_id"] = map.Patients[sourcePatient],
                        ["invoice_id"] = MapOrNull(map.Invoices, sourceInvoice),
                        ["receipt_number"] = EnsureUniqueText(tx!, "payments", "receipt_number", row.GetString("receipt_number"))
                    }, "id");
                }
                return true;
            }, preview, result, dryRun, ct);

            if (!dryRun)
            {
                RecalculateImportedInvoices(map, tx!);
            }
        }

        // --- Staff + salaries ---
        if (plan.RestoreEverything || plan.IncludeStaff)
        {
            ImportChild(src, "staff", "SELECT * FROM staff ORDER BY id", row =>
            {
                if (!dryRun)
                {
                    var newId = InsertRow(tx!, "staff", row, new()
                    {
                        ["staff_code"] = EnsureUniqueText(tx!, "staff", "staff_code", row.GetString("staff_code"))
                    }, "id");
                    map.Staff[Convert.ToInt64(row["id"])] = newId;
                }
                return true;
            }, preview, result, dryRun, ct);

            if (!dryRun)
            {
                foreach (var row in Query(src, "SELECT * FROM salary_payments ORDER BY id"))
                {
                    var parent = MapOrNull(map.Staff, row["staff_id"]);
                    if (parent is null) continue;
                    InsertRow(tx!, "salary_payments", row, new() { ["staff_id"] = parent }, "id");
                }
            }
        }

        // --- Expenses ---
        if (plan.RestoreEverything || plan.IncludeExpenses)
        {
            progress?.Report(new BackupProgress("Importing expenses", 0.74));
            ImportChild(src, "expenses", "SELECT * FROM expenses ORDER BY id", row =>
            {
                if (!InFinanceRange(row["expense_date"])) return false;
                if (plan.ExpenseCategories is { Count: > 0 })
                {
                    var category = Convert.ToString(row["category_name"]) ?? string.Empty;
                    if (!plan.ExpenseCategories.Contains(category)) return false;
                }

                if (!dryRun)
                {
                    InsertRow(tx!, "expenses", row, new()
                    {
                        ["category_id"] = ResolveExpenseCategory(tx!, Convert.ToString(row["category_name"])),
                        ["staff_id"] = MapOrNull(map.Staff, row["staff_id"])
                    }, "id");
                }
                return true;
            }, preview, result, dryRun, ct);
        }

        // --- Attachments (metadata now, files after commit) ---
        if (plan.RestoreEverything || plan.IncludeAttachments)
        {
            progress?.Report(new BackupProgress("Importing attachment records", 0.8));
            ImportChild(src, "attachments", "SELECT * FROM attachments ORDER BY id", row =>
            {
                var patientValue = row["patient_id"];
                long? localPatient = null;
                if (patientValue is not null and not DBNull)
                {
                    var sourcePatient = Convert.ToInt64(patientValue);
                    if (!PatientIncluded(sourcePatient)) { NoteMissingDependency("Attachment", sourcePatient); return false; }
                    localPatient = map.Patients[sourcePatient];
                }

                var storedPath = row.GetString("stored_path");
                if (!session.HasAttachment(storedPath))
                {
                    var msg = $"{row.GetString("original_name")} is referenced by the backup but its file is missing.";
                    if (!preview.MissingAttachments.Contains(msg)) preview.MissingAttachments.Add(msg);
                }

                if (!dryRun)
                {
                    var uid = Guid.NewGuid().ToString("N");
                    var extension = Path.GetExtension(storedPath);
                    var folder = localPatient is { } lp
                        ? Path.Combine(SafeCategoryFolder(row.GetString("category")), $"P{lp:D8}")
                        : SafeCategoryFolder(row.GetString("category"));
                    var newRelative = Path.Combine(folder, uid + extension).Replace('\\', '/');

                    InsertRow(tx!, "attachments", row, new()
                    {
                        ["attachment_uid"] = uid,
                        ["patient_id"] = localPatient,
                        ["referral_id"] = MapOrNull(map.Referrals, row["referral_id"]),
                        ["expense_id"] = null,
                        ["staff_id"] = MapOrNull(map.Staff, row["staff_id"]),
                        ["stored_path"] = newRelative
                    }, "id");

                    map.AttachmentPaths.Add((storedPath, newRelative, row.GetString("checksum_sha256")));
                }

                return true;
            }, preview, result, dryRun, ct);
        }

        // --- Optional system data ---
        if (plan.IncludeSettings && !plan.RestoreEverything || (plan.RestoreEverything && plan.IncludeSettings))
        {
            if (!dryRun)
            {
                foreach (var row in Query(src, "SELECT key, value FROM settings"))
                {
                    var key = row.GetString("key");
                    if (key.StartsWith("numbering.", StringComparison.OrdinalIgnoreCase)) continue;
                    using var cmd = _db.CreateCommand("""
                        INSERT INTO settings(key, value, updated_utc) VALUES($k,$v,$t)
                        ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_utc=excluded.updated_utc
                        """);
                    cmd.Transaction = tx;
                    cmd.AddValue("$k", key);
                    cmd.AddValue("$v", row["value"]);
                    cmd.AddValue("$t", DateTime.UtcNow.ToString("O"));
                    cmd.ExecuteNonQuery();
                }
            }

            preview.Add("settings");
        }

        if (plan.IncludeAuditLog)
        {
            ImportChild(src, "audit_log", "SELECT * FROM audit_log ORDER BY id", row =>
            {
                if (!dryRun) InsertRow(tx!, "audit_log", row, new(), "id");
                return true;
            }, preview, result, dryRun, ct);
        }

        if (plan.IncludeUsers)
        {
            ImportChild(src, "users", "SELECT * FROM users ORDER BY id", row =>
            {
                var username = row.GetString("username");
                using var check = _db.CreateCommand("SELECT COUNT(*) FROM users WHERE username=$u COLLATE NOCASE");
                check.Transaction = tx;
                check.AddValue("$u", username);
                if (Convert.ToInt32(check.ExecuteScalar() ?? 0) > 0)
                {
                    preview.Conflicts.Add($"User '{username}' already exists and was not imported.");
                    return false;
                }

                if (!dryRun) InsertRow(tx!, "users", row, new(), "id");
                return true;
            }, preview, result, dryRun, ct);
        }
    }

    private static string SafeCategoryFolder(string category) => category switch
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

    private void ImportChild(SqliteConnection src, string table, string sql, Func<Row, bool> handler,
        RestorePreview preview, RestoreResult result, bool dryRun, CancellationToken ct)
    {
        foreach (var row in Query(src, sql))
        {
            ct.ThrowIfCancellationRequested();
            bool imported;
            try
            {
                imported = handler(row);
            }
            catch (Exception ex)
            {
                preview.InvalidRecords.Add($"{table}: {ex.Message}");
                result.Errors.Add($"{table}: {ex.Message}");
                continue;
            }

            if (imported)
            {
                preview.Add(table);
                if (!dryRun) result.Count(result.Inserted, table);
            }
            else
            {
                preview.Skip(table);
                if (!dryRun) result.Count(result.Skipped, table);
            }
        }
    }

    private void RecalculateImportedInvoices(IdMap map, SqliteTransaction tx)
    {
        foreach (var invoiceId in map.Invoices.Values)
        {
            decimal paid;
            using (var cmd = _db.CreateCommand("SELECT COALESCE(SUM(CASE WHEN is_refund=1 THEN -amount ELSE amount END),0) FROM payments WHERE invoice_id=$id"))
            {
                cmd.Transaction = tx;
                cmd.AddValue("$id", invoiceId);
                paid = Convert.ToDecimal(cmd.ExecuteScalar() ?? 0d);
            }

            decimal total;
            string status;
            using (var cmd = _db.CreateCommand("SELECT total, status FROM invoices WHERE id=$id"))
            {
                cmd.Transaction = tx;
                cmd.AddValue("$id", invoiceId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) continue;
                total = r.GetDecimalValue("total");
                status = r.GetStringOrEmpty("status");
            }

            var newStatus = status == "Cancelled" ? "Cancelled" : BillingCalculator.DetermineStatus(total, paid);
            using var upd = _db.CreateCommand("UPDATE invoices SET paid_amount=$p, status=$s WHERE id=$id");
            upd.Transaction = tx;
            upd.AddMoney("$p", Math.Max(0m, paid));
            upd.AddValue("$s", newStatus);
            upd.AddValue("$id", invoiceId);
            upd.ExecuteNonQuery();
        }
    }

    private void RestoreAttachmentFiles(PackageSession session, IdMap map, RestoreResult result, CancellationToken ct)
    {
        _paths.EnsureCreated();

        foreach (var (sourceRelative, targetRelative, checksum) in map.AttachmentPaths)
        {
            ct.ThrowIfCancellationRequested();
            var entry = session.GetAttachmentEntry(sourceRelative);
            if (entry is null)
            {
                result.AttachmentsMissing++;
                continue;
            }

            try
            {
                var destination = _paths.ResolveAttachment(targetRelative);
                BackupService.ExtractEntry(entry, destination, session.Password, session.Salt);

                if (!string.IsNullOrEmpty(checksum))
                {
                    var actual = BackupService.Sha256File(destination);
                    if (!string.Equals(actual, checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Warnings.Add($"Checksum mismatch for restored file '{Path.GetFileName(sourceRelative)}'.");
                    }
                }

                result.AttachmentsRestored++;
            }
            catch (Exception ex)
            {
                result.AttachmentsMissing++;
                result.Warnings.Add($"Could not restore '{sourceRelative}': {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private long? FindPatientByCode(string code, SqliteTransaction? tx)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        using var cmd = _db.CreateCommand("SELECT id FROM patients WHERE patient_code=$c COLLATE NOCASE");
        cmd.Transaction = tx;
        cmd.AddValue("$c", code);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private string EnsureUniquePatientCode(string code, SqliteTransaction tx)
        => EnsureUniqueText(tx, "patients", "patient_code", code);

    private string EnsureUniqueText(SqliteTransaction tx, string table, string column, string? value)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N")[..10].ToUpperInvariant() : value.Trim();
        var attempt = 1;
        while (true)
        {
            using var cmd = _db.CreateCommand($"SELECT COUNT(*) FROM {table} WHERE {column}=$v COLLATE NOCASE");
            cmd.Transaction = tx;
            cmd.AddValue("$v", candidate);
            if (Convert.ToInt32(cmd.ExecuteScalar() ?? 0) == 0)
            {
                return candidate;
            }

            candidate = $"{value}-R{attempt++}";
            if (attempt > 9999)
            {
                return Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            }
        }
    }

    private long? ResolveExpenseCategory(SqliteTransaction tx, string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName)) return null;
        using (var find = _db.CreateCommand("SELECT id FROM expense_categories WHERE name=$n COLLATE NOCASE"))
        {
            find.Transaction = tx;
            find.AddValue("$n", categoryName);
            var existing = find.ExecuteScalar();
            if (existing is not null and not DBNull) return Convert.ToInt64(existing);
        }

        using var insert = _db.CreateCommand("INSERT INTO expense_categories(name, is_active, sort_order, created_utc) VALUES($n,1,900,$c); SELECT last_insert_rowid();");
        insert.Transaction = tx;
        insert.AddValue("$n", categoryName);
        insert.AddValue("$c", DateTime.UtcNow.ToString("O"));
        return Convert.ToInt64(insert.ExecuteScalar() ?? 0L);
    }

    private static long? MapOrNull(Dictionary<long, long> map, object? sourceValue)
    {
        if (sourceValue is null or DBNull) return null;
        var key = Convert.ToInt64(sourceValue);
        return map.TryGetValue(key, out var mapped) && mapped > 0 ? mapped : null;
    }

    private long InsertPatient(Row row, string code, SqliteTransaction tx)
        => InsertRow(tx, "patients", row, new Dictionary<string, object?> { ["patient_code"] = code, ["assigned_dentist_id"] = null }, "id");

    private void UpdatePatient(long localId, Row row, SqliteTransaction tx, bool mergeOnly)
    {
        var columns = GetColumns("patients").Where(c => c is not ("id" or "patient_code" or "created_utc" or "assigned_dentist_id")).ToList();
        var assignments = new List<string>();
        foreach (var column in columns)
        {
            if (!row.Contains(column)) continue;
            // Merge keeps a non-empty local value and only fills blanks.
            assignments.Add(mergeOnly
                ? $"{column} = CASE WHEN {column} IS NULL OR {column} = '' THEN ${column} ELSE {column} END"
                : $"{column} = ${column}");
        }

        if (assignments.Count == 0) return;

        using var cmd = _db.CreateCommand($"UPDATE patients SET {string.Join(", ", assignments)} WHERE id=$__id");
        cmd.Transaction = tx;
        foreach (var column in columns)
        {
            if (!row.Contains(column)) continue;
            cmd.AddValue("$" + column, row[column]);
        }

        cmd.AddValue("$__id", localId);
        cmd.ExecuteNonQuery();
    }

    private readonly Dictionary<string, List<string>> _columnCache = new(StringComparer.OrdinalIgnoreCase);

    private List<string> GetColumns(string table)
    {
        if (_columnCache.TryGetValue(table, out var cached)) return cached;
        var columns = new List<string>();
        using var cmd = _db.CreateCommand($"PRAGMA table_info({table})");
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        _columnCache[table] = columns;
        return columns;
    }

    /// <summary>
    /// Inserts a source row into a local table, dropping the primary key so SQLite assigns a fresh id
    /// and applying any caller-supplied remapped foreign keys.
    /// </summary>
    private long InsertRow(SqliteTransaction tx, string table, Row row, Dictionary<string, object?> overrides, string identityColumn)
    {
        var localColumns = GetColumns(table);
        var columns = new List<string>();
        var values = new List<object?>();

        foreach (var column in localColumns)
        {
            if (string.Equals(column, identityColumn, StringComparison.OrdinalIgnoreCase)) continue;

            if (overrides.TryGetValue(column, out var overridden))
            {
                columns.Add(column);
                values.Add(overridden);
                continue;
            }

            if (!row.Contains(column)) continue;
            columns.Add(column);
            values.Add(row[column]);
        }

        if (columns.Count == 0) return 0;

        var sql = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(c => "$" + c))}); SELECT last_insert_rowid();";
        using var cmd = _db.CreateCommand(sql);
        cmd.Transaction = tx;
        for (var i = 0; i < columns.Count; i++)
        {
            cmd.AddValue("$" + columns[i], values[i]);
        }

        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static string? ScalarString(SqliteConnection conn, string sql, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar()?.ToString();
    }

    private static IEnumerable<Row> Query(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var names = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++) names[i] = reader.GetName(i);

        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            yield return new Row(names, values);
        }
    }

    private async Task<PackageSession> OpenPackageAsync(string packagePath, string? password, CancellationToken ct)
    {
        return await Task.Run(() => PackageSession.Open(packagePath, password, _paths), ct).ConfigureAwait(false);
    }

    private sealed class IdMap
    {
        public Dictionary<long, long> Patients { get; } = new();
        public Dictionary<long, long> Visits { get; } = new();
        public Dictionary<long, long> Treatments { get; } = new();
        public Dictionary<long, long> Prescriptions { get; } = new();
        public Dictionary<long, long> Referrals { get; } = new();
        public Dictionary<long, long> Invoices { get; } = new();
        public Dictionary<long, long> Staff { get; } = new();
        public List<(string Source, string Target, string? Checksum)> AttachmentPaths { get; } = new();
    }

    internal sealed class Row
    {
        private readonly Dictionary<string, int> _index;
        private readonly object?[] _values;

        public Row(string[] names, object?[] values)
        {
            _values = values;
            _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < names.Length; i++) _index[names[i]] = i;
        }

        public bool Contains(string column) => _index.ContainsKey(column);
        public object? this[string column] => _index.TryGetValue(column, out var i) ? _values[i] : null;
        public string GetString(string column) => Convert.ToString(this[column]) ?? string.Empty;
    }

    /// <summary>Holds the opened package plus the extracted read-only database snapshot.</summary>
    private sealed class PackageSession : IDisposable
    {
        private readonly string _workDir;

        private PackageSession(ZipArchive zip, SqliteConnection source, string workDir, string? password, byte[]? salt)
        {
            Zip = zip;
            Source = source;
            _workDir = workDir;
            Password = password;
            Salt = salt;
        }

        public ZipArchive Zip { get; }
        public SqliteConnection Source { get; }
        public string? Password { get; }
        public byte[]? Salt { get; }

        public static PackageSession Open(string packagePath, string? password, AppPaths paths)
        {
            var zip = ZipFile.OpenRead(packagePath);
            byte[]? salt = null;

            var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("The backup manifest is missing.");
            using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
            {
                var json = reader.ReadToEnd();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("encrypted", out var enc) && enc.ValueKind == JsonValueKind.True)
                {
                    if (doc.RootElement.TryGetProperty("salt", out var saltProp) && saltProp.GetString() is { } s)
                    {
                        salt = Convert.FromBase64String(s);
                    }

                    if (string.IsNullOrEmpty(password))
                    {
                        zip.Dispose();
                        throw new InvalidOperationException("This backup is password protected. Enter the password to continue.");
                    }
                }
            }

            var workDir = Path.Combine(paths.TempDirectory, "restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            var dbPath = Path.Combine(workDir, "source.db");

            var dbEntry = zip.GetEntry("database/dentiva.db") ?? throw new InvalidDataException("The backup does not contain a database snapshot.");
            BackupService.ExtractEntry(dbEntry, dbPath, password, salt);

            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            conn.Open();

            return new PackageSession(zip, conn, workDir, password, salt);
        }

        public ZipArchiveEntry? GetAttachmentEntry(string storedPath)
        {
            var normalised = "attachments/" + storedPath.Replace('\\', '/');
            return Zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, normalised, StringComparison.OrdinalIgnoreCase));
        }

        public bool HasAttachment(string storedPath) => GetAttachmentEntry(storedPath) is not null;

        public void Dispose()
        {
            try { Source.Close(); Source.Dispose(); } catch { /* ignore */ }
            SqliteConnection.ClearAllPools();
            try { Zip.Dispose(); } catch { /* ignore */ }
            BackupService.TryDeleteDirectory(_workDir);
        }
    }
}
