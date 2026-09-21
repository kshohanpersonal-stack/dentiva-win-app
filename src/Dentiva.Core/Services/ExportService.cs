using System.Globalization;
using System.Text;
using Dentiva.Core.Data;

namespace Dentiva.Core.Services;

/// <summary>
/// CSV/Excel-compatible export. Values are written RFC-4180 style and defended against CSV
/// formula injection, which matters because exports are routinely opened in Excel.
/// </summary>
public sealed class ExportService
{
    private readonly Database _db;
    private readonly AppPaths _paths;
    private readonly AuditService _audit;

    public ExportService(Database db, AppPaths paths, AuditService audit)
    {
        _db = db;
        _paths = paths;
        _audit = audit;
    }

    public string EnsureExportFolder()
    {
        Directory.CreateDirectory(_paths.ExportsDirectory);
        return _paths.ExportsDirectory;
    }

    public static string EscapeCsv(string? value)
    {
        value ??= string.Empty;

        // Neutralise spreadsheet formula injection.
        if (value.Length > 0 && (value[0] == '=' || value[0] == '+' || value[0] == '-' || value[0] == '@'))
        {
            value = "'" + value;
        }

        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            value = "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    public async Task<string> WriteCsvAsync(string filePath, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);

        // UTF-8 BOM so Excel renders Bangla text correctly.
        await using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
        await writer.WriteLineAsync(string.Join(",", headers.Select(EscapeCsv))).ConfigureAwait(false);

        var count = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Join(",", row.Select(EscapeCsv))).ConfigureAwait(false);
            count++;
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
        _audit.Log("data.exported", "Export", null, $"{Path.GetFileName(filePath)} ({count} rows)");
        return filePath;
    }

    private string Path_(string name) => System.IO.Path.Combine(EnsureExportFolder(), name);

    public Task<string> ExportPatientsAsync(bool includeArchived, string? outputPath = null, CancellationToken ct = default)
    {
        var headers = new[]
        {
            "Patient Code", "Full Name", "Preferred Name", "Phone", "Alternate Phone", "Email",
            "Date of Birth", "Age", "Gender", "Blood Group", "Address", "City", "District",
            "Emergency Contact", "Emergency Phone", "Allergies", "Medical Conditions",
            "Archived", "Registered On"
        };

        var rows = new List<IReadOnlyList<string?>>();
        using (var cmd = _db.CreateCommand(includeArchived
            ? "SELECT * FROM patients ORDER BY patient_code"
            : "SELECT * FROM patients WHERE is_archived=0 ORDER BY patient_code"))
        {
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var dob = r.GetDateOrNull("date_of_birth");
                rows.Add(new[]
                {
                    r.GetStringOrNull("patient_code"),
                    r.GetStringOrNull("full_name"),
                    r.GetStringOrNull("preferred_name"),
                    r.GetStringOrNull("phone"),
                    r.GetStringOrNull("alternate_phone"),
                    r.GetStringOrNull("email"),
                    dob?.ToString("yyyy-MM-dd"),
                    Repositories.PatientRepository.CalculateAge(dob)?.ToString(),
                    r.GetStringOrNull("gender"),
                    r.GetStringOrNull("blood_group"),
                    r.GetStringOrNull("address"),
                    r.GetStringOrNull("city"),
                    r.GetStringOrNull("district"),
                    r.GetStringOrNull("emergency_contact_name"),
                    r.GetStringOrNull("emergency_phone"),
                    r.GetStringOrNull("allergies"),
                    r.GetStringOrNull("medical_conditions"),
                    r.GetBoolValue("is_archived") ? "Yes" : "No",
                    r.GetUtcValue("created_utc").ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                });
            }
        }

        return WriteCsvAsync(outputPath ?? Path_($"Patients-{DateTime.Now:yyyyMMdd-HHmm}.csv"), headers, rows, ct);
    }

    public Task<string> ExportAppointmentsAsync(DateTime from, DateTime to, string? outputPath = null, CancellationToken ct = default)
    {
        var headers = new[] { "Date", "Serial", "Time", "Patient Code", "Patient", "Phone", "Type", "Reason", "Status", "Priority", "Dentist", "Notes" };
        var rows = new List<IReadOnlyList<string?>>();

        using (var cmd = _db.CreateCommand("""
            SELECT a.*, p.full_name, p.patient_code, p.phone FROM appointments a
            JOIN patients p ON p.id=a.patient_id
            WHERE a.appointment_date BETWEEN $f AND $t
            ORDER BY a.appointment_date, a.start_time
            """))
        {
            cmd.AddDate("$f", from);
            cmd.AddDate("$t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new[]
                {
                    r.GetDateValue("appointment_date").ToString("yyyy-MM-dd"),
                    r.GetIntOrNull("serial_number")?.ToString(),
                    r.GetStringOrNull("start_time"),
                    r.GetStringOrNull("patient_code"),
                    r.GetStringOrNull("full_name"),
                    r.GetStringOrNull("phone"),
                    r.GetStringOrNull("appointment_type"),
                    r.GetStringOrNull("reason"),
                    r.GetStringOrNull("status"),
                    r.GetStringOrNull("priority"),
                    r.GetStringOrNull("dentist_name"),
                    r.GetStringOrNull("notes")
                });
            }
        }

        return WriteCsvAsync(outputPath ?? Path_($"Appointments-{DateTime.Now:yyyyMMdd-HHmm}.csv"), headers, rows, ct);
    }

    public Task<string> ExportInvoicesAsync(DateTime from, DateTime to, string? outputPath = null, CancellationToken ct = default)
    {
        var headers = new[] { "Invoice No", "Date", "Patient Code", "Patient", "Subtotal", "Discount", "Tax", "Total", "Paid", "Due", "Status" };
        var rows = new List<IReadOnlyList<string?>>();

        using (var cmd = _db.CreateCommand("""
            SELECT i.*, p.full_name, p.patient_code FROM invoices i
            JOIN patients p ON p.id=i.patient_id
            WHERE i.invoice_date BETWEEN $f AND $t ORDER BY i.invoice_date, i.id
            """))
        {
            cmd.AddDate("$f", from);
            cmd.AddDate("$t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var total = r.GetDecimalValue("total");
                var paid = r.GetDecimalValue("paid_amount");
                rows.Add(new[]
                {
                    r.GetStringOrNull("invoice_number"),
                    r.GetDateValue("invoice_date").ToString("yyyy-MM-dd"),
                    r.GetStringOrNull("patient_code"),
                    r.GetStringOrNull("full_name"),
                    Num(r.GetDecimalValue("subtotal")),
                    Num(r.GetDecimalValue("discount")),
                    Num(r.GetDecimalValue("tax_amount")),
                    Num(total),
                    Num(paid),
                    Num(Math.Max(0, total - paid)),
                    r.GetStringOrNull("status")
                });
            }
        }

        return WriteCsvAsync(outputPath ?? Path_($"Invoices-{DateTime.Now:yyyyMMdd-HHmm}.csv"), headers, rows, ct);
    }

    public Task<string> ExportPaymentsAsync(DateTime from, DateTime to, string? outputPath = null, CancellationToken ct = default)
    {
        var headers = new[] { "Receipt No", "Date", "Patient Code", "Patient", "Invoice", "Amount", "Type", "Method", "Reference", "Received By", "Notes" };
        var rows = new List<IReadOnlyList<string?>>();

        using (var cmd = _db.CreateCommand("""
            SELECT pay.*, p.full_name, p.patient_code, i.invoice_number FROM payments pay
            JOIN patients p ON p.id=pay.patient_id
            LEFT JOIN invoices i ON i.id=pay.invoice_id
            WHERE date(pay.payment_date) BETWEEN $f AND $t ORDER BY pay.payment_date
            """))
        {
            cmd.AddDate("$f", from);
            cmd.AddDate("$t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new[]
                {
                    r.GetStringOrNull("receipt_number"),
                    r.GetDateValue("payment_date").ToString("yyyy-MM-dd HH:mm"),
                    r.GetStringOrNull("patient_code"),
                    r.GetStringOrNull("full_name"),
                    r.GetStringOrNull("invoice_number"),
                    Num(r.GetDecimalValue("amount")),
                    r.GetBoolValue("is_refund") ? "Refund" : "Payment",
                    r.GetStringOrNull("method"),
                    r.GetStringOrNull("reference"),
                    r.GetStringOrNull("received_by"),
                    r.GetStringOrNull("notes")
                });
            }
        }

        return WriteCsvAsync(outputPath ?? Path_($"Payments-{DateTime.Now:yyyyMMdd-HHmm}.csv"), headers, rows, ct);
    }

    public Task<string> ExportExpensesAsync(DateTime from, DateTime to, string? outputPath = null, CancellationToken ct = default)
    {
        var headers = new[] { "Date", "Category", "Description", "Amount", "Payment Method", "Vendor", "Reference", "Notes" };
        var rows = new List<IReadOnlyList<string?>>();

        using (var cmd = _db.CreateCommand("SELECT * FROM expenses WHERE expense_date BETWEEN $f AND $t ORDER BY expense_date"))
        {
            cmd.AddDate("$f", from);
            cmd.AddDate("$t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new[]
                {
                    r.GetDateValue("expense_date").ToString("yyyy-MM-dd"),
                    r.GetStringOrNull("category_name"),
                    r.GetStringOrNull("description"),
                    Num(r.GetDecimalValue("amount")),
                    r.GetStringOrNull("payment_method"),
                    r.GetStringOrNull("vendor"),
                    r.GetStringOrNull("reference"),
                    r.GetStringOrNull("notes")
                });
            }
        }

        return WriteCsvAsync(outputPath ?? Path_($"Expenses-{DateTime.Now:yyyyMMdd-HHmm}.csv"), headers, rows, ct);
    }

    public Task<string> ExportTreatmentsAsync(DateTime from, DateTime to, string? outputPath = null, CancellationToken ct = default)
    {
        var headers = new[] { "Date", "Patient Code", "Patient", "Category", "Procedure", "Tooth", "Status", "Cost", "Discount", "Net", "Dentist", "Notes" };
        var rows = new List<IReadOnlyList<string?>>();

        using (var cmd = _db.CreateCommand("""
            SELECT t.*, p.full_name, p.patient_code FROM treatments t
            JOIN patients p ON p.id=t.patient_id
            WHERE t.treatment_date BETWEEN $f AND $t ORDER BY t.treatment_date
            """))
        {
            cmd.AddDate("$f", from);
            cmd.AddDate("$t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var cost = r.GetDecimalValue("cost");
                var disc = r.GetDecimalValue("discount");
                rows.Add(new[]
                {
                    r.GetDateValue("treatment_date").ToString("yyyy-MM-dd"),
                    r.GetStringOrNull("patient_code"),
                    r.GetStringOrNull("full_name"),
                    r.GetStringOrNull("category_name"),
                    r.GetStringOrNull("procedure_name"),
                    r.GetStringOrNull("teeth"),
                    r.GetStringOrNull("status"),
                    Num(cost), Num(disc), Num(Math.Max(0, cost - disc)),
                    r.GetStringOrNull("dentist_name"),
                    r.GetStringOrNull("notes")
                });
            }
        }

        return WriteCsvAsync(outputPath ?? Path_($"Treatments-{DateTime.Now:yyyyMMdd-HHmm}.csv"), headers, rows, ct);
    }

    public Task<string> ExportStaffAsync(string? outputPath = null, CancellationToken ct = default)
    {
        var headers = new[] { "Staff Code", "Name", "Role", "Department", "Phone", "Email", "Joining Date", "Salary", "Salary Type", "Status" };
        var rows = new List<IReadOnlyList<string?>>();

        using (var cmd = _db.CreateCommand("SELECT * FROM staff ORDER BY full_name"))
        {
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new[]
                {
                    r.GetStringOrNull("staff_code"),
                    r.GetStringOrNull("full_name"),
                    r.GetStringOrNull("role"),
                    r.GetStringOrNull("department"),
                    r.GetStringOrNull("phone"),
                    r.GetStringOrNull("email"),
                    r.GetDateOrNull("joining_date")?.ToString("yyyy-MM-dd"),
                    Num(r.GetDecimalValue("salary")),
                    r.GetStringOrNull("salary_type"),
                    r.GetStringOrNull("status")
                });
            }
        }

        return WriteCsvAsync(outputPath ?? Path_($"Staff-{DateTime.Now:yyyyMMdd-HHmm}.csv"), headers, rows, ct);
    }

    public Task<string> ExportPrescriptionsAsync(DateTime from, DateTime to, string? outputPath = null, CancellationToken ct = default)
    {
        var headers = new[] { "Prescription No", "Date", "Patient Code", "Patient", "Medicine", "Strength", "Dosage", "Frequency", "Duration", "Instructions" };
        var rows = new List<IReadOnlyList<string?>>();

        using (var cmd = _db.CreateCommand("""
            SELECT rx.prescription_no, rx.issued_date, p.patient_code, p.full_name,
                   i.medicine_name, i.strength, i.dosage, i.frequency, i.duration, i.instructions
            FROM prescriptions rx
            JOIN patients p ON p.id = rx.patient_id
            LEFT JOIN prescription_items i ON i.prescription_id = rx.id
            WHERE rx.issued_date BETWEEN $f AND $t
            ORDER BY rx.issued_date, rx.id, i.sort_order
            """))
        {
            cmd.AddDate("$f", from);
            cmd.AddDate("$t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new[]
                {
                    r.GetStringOrNull("prescription_no"),
                    r.GetDateValue("issued_date").ToString("yyyy-MM-dd"),
                    r.GetStringOrNull("patient_code"),
                    r.GetStringOrNull("full_name"),
                    r.GetStringOrNull("medicine_name"),
                    r.GetStringOrNull("strength"),
                    r.GetStringOrNull("dosage"),
                    r.GetStringOrNull("frequency"),
                    r.GetStringOrNull("duration"),
                    r.GetStringOrNull("instructions")
                });
            }
        }

        return WriteCsvAsync(outputPath ?? Path_($"Prescriptions-{DateTime.Now:yyyyMMdd-HHmm}.csv"), headers, rows, ct);
    }

    public Task<string> ExportAuditLogAsync(DateTime from, DateTime to, string? outputPath = null, CancellationToken ct = default)
    {
        var headers = new[] { "Timestamp", "User", "Action", "Entity", "Entity Id", "Summary" };
        var rows = new List<IReadOnlyList<string?>>();

        using (var cmd = _db.CreateCommand("SELECT * FROM audit_log WHERE timestamp_utc BETWEEN $f AND $t ORDER BY timestamp_utc DESC"))
        {
            cmd.AddValue("$f", from.ToUniversalTime().ToString("O"));
            cmd.AddValue("$t", to.Date.AddDays(1).ToUniversalTime().ToString("O"));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new[]
                {
                    r.GetUtcValue("timestamp_utc").ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    r.GetStringOrNull("username"),
                    r.GetStringOrNull("action"),
                    r.GetStringOrNull("entity"),
                    r.GetStringOrNull("entity_id"),
                    r.GetStringOrNull("summary")
                });
            }
        }

        return WriteCsvAsync(outputPath ?? Path_($"AuditLog-{DateTime.Now:yyyyMMdd-HHmm}.csv"), headers, rows, ct);
    }

    private static string Num(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
