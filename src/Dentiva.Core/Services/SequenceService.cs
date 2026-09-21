using Dentiva.Core.Data;
using Microsoft.Data.Sqlite;

namespace Dentiva.Core.Services;

/// <summary>
/// Allocates unique, gap-tolerant document numbers (patient codes, invoices, receipts, prescriptions).
/// The counter is advanced atomically and collisions are resolved by probing forward, so a manually
/// created record can never silently duplicate an existing number.
/// </summary>
public sealed class SequenceService
{
    private readonly Database _db;
    private readonly SettingsService _settings;
    private readonly object _sync = new();

    public SequenceService(Database db, SettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    public string NextPatientCode() => Next(
        SettingsService.Keys.PatientCodePrefix, "DEN-",
        SettingsService.Keys.PatientCodePadding, 6,
        SettingsService.Keys.PatientCodeNext,
        "patients", "patient_code");

    public string NextInvoiceNumber() => Next(
        SettingsService.Keys.InvoicePrefix, "INV-",
        SettingsService.Keys.InvoicePadding, 6,
        SettingsService.Keys.InvoiceNext,
        "invoices", "invoice_number");

    public string NextReceiptNumber() => Next(
        SettingsService.Keys.ReceiptPrefix, "RCP-",
        SettingsService.Keys.ReceiptPadding, 6,
        SettingsService.Keys.ReceiptNext,
        "payments", "receipt_number");

    public string NextPrescriptionNumber() => Next(
        SettingsService.Keys.PrescriptionPrefix, "RX-",
        SettingsService.Keys.PrescriptionPadding, 6,
        SettingsService.Keys.PrescriptionNext,
        "prescriptions", "prescription_no");

    public string NextStaffCode() => Next(
        SettingsService.Keys.StaffPrefix, "STF-",
        SettingsService.Keys.StaffPadding, 4,
        SettingsService.Keys.StaffNext,
        "staff", "staff_code");

    private string Next(string prefixKey, string prefixFallback, string padKey, int padFallback, string counterKey, string table, string column)
    {
        lock (_sync)
        {
            var prefix = _settings.Get(prefixKey, prefixFallback);
            var padding = Math.Clamp(_settings.GetInt(padKey, padFallback), 1, 12);
            var counter = Math.Max(1, _settings.GetInt(counterKey, 1));

            // Probe forward until the candidate is genuinely unused.
            for (var attempt = 0; attempt < 10_000; attempt++)
            {
                var candidate = prefix + counter.ToString().PadLeft(padding, '0');
                if (!Exists(table, column, candidate))
                {
                    _settings.Set(counterKey, counter + 1);
                    return candidate;
                }

                counter++;
            }

            throw new InvalidOperationException($"Unable to allocate a unique number for {table}.{column} after 10,000 attempts.");
        }
    }

    private bool Exists(string table, string column, string value)
    {
        // Table/column names are compile-time constants from this class only; the value is parameterised.
        using var cmd = _db.CreateCommand($"SELECT 1 FROM {table} WHERE {column} = $v LIMIT 1");
        cmd.Parameters.AddWithValue("$v", value);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    /// <summary>
    /// Re-synchronises counters after a restore so future numbers continue past the imported data.
    /// </summary>
    public void ResyncAfterRestore()
    {
        lock (_sync)
        {
            Resync("patients", "patient_code", SettingsService.Keys.PatientCodePrefix, "DEN-", SettingsService.Keys.PatientCodeNext);
            Resync("invoices", "invoice_number", SettingsService.Keys.InvoicePrefix, "INV-", SettingsService.Keys.InvoiceNext);
            Resync("payments", "receipt_number", SettingsService.Keys.ReceiptPrefix, "RCP-", SettingsService.Keys.ReceiptNext);
            Resync("prescriptions", "prescription_no", SettingsService.Keys.PrescriptionPrefix, "RX-", SettingsService.Keys.PrescriptionNext);
            Resync("staff", "staff_code", SettingsService.Keys.StaffPrefix, "STF-", SettingsService.Keys.StaffNext);
        }
    }

    private void Resync(string table, string column, string prefixKey, string prefixFallback, string counterKey)
    {
        var prefix = _settings.Get(prefixKey, prefixFallback);
        var highest = 0;
        using (var cmd = _db.CreateCommand($"SELECT {column} FROM {table} WHERE {column} LIKE $p"))
        {
            cmd.Parameters.AddWithValue("$p", prefix + "%");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var raw = reader.GetString(0);
                var tail = raw.Length > prefix.Length ? raw[prefix.Length..] : string.Empty;
                if (int.TryParse(tail, out var n) && n > highest)
                {
                    highest = n;
                }
            }
        }

        if (highest > 0)
        {
            var current = _settings.GetInt(counterKey, 1);
            if (current <= highest)
            {
                _settings.Set(counterKey, highest + 1);
            }
        }
    }
}
