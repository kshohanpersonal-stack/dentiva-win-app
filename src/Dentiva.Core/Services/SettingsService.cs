using System.Globalization;
using Dentiva.Core.Data;

namespace Dentiva.Core.Services;

/// <summary>
/// Strongly typed access to the key/value settings table with an in-memory cache.
/// </summary>
public sealed class SettingsService
{
    private readonly Database _db;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private bool _loaded;

    public SettingsService(Database db)
    {
        _db = db;
    }

    public event EventHandler? Changed;

    public void Reload()
    {
        lock (_sync)
        {
            _cache.Clear();
            using var cmd = _db.CreateCommand("SELECT key, value FROM settings");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _cache[reader.GetString(0)] = reader.GetString(1);
            }

            _loaded = true;
        }
    }

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            Reload();
        }
    }

    public string Get(string key, string fallback = "")
    {
        EnsureLoaded();
        lock (_sync)
        {
            return _cache.TryGetValue(key, out var value) ? value : fallback;
        }
    }

    public bool GetBool(string key, bool fallback = false)
        => bool.TryParse(Get(key, fallback.ToString()), out var v) ? v : fallback;

    public int GetInt(string key, int fallback = 0)
        => int.TryParse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public decimal GetDecimal(string key, decimal fallback = 0m)
        => decimal.TryParse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public void Set(string key, string? value)
    {
        EnsureLoaded();
        value ??= string.Empty;
        using var cmd = _db.CreateCommand("""
            INSERT INTO settings(key, value, updated_utc) VALUES ($k, $v, $t)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc
            """);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();

        lock (_sync)
        {
            _cache[key] = value;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Set(string key, bool value) => Set(key, value.ToString());
    public void Set(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));
    public void Set(string key, decimal value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        EnsureLoaded();
        lock (_sync)
        {
            return new Dictionary<string, string>(_cache, StringComparer.OrdinalIgnoreCase);
        }
    }

    // ---- Well-known keys -------------------------------------------------
    public static class Keys
    {
        public const string SetupCompleted = "app.setup_completed";
        public const string SchemaSeeded = "app.seeded";
        public const string Language = "app.language";
        public const string DateFormat = "app.date_format";
        public const string TimeFormat = "app.time_format";
        public const string Currency = "app.currency";
        public const string CurrencySymbol = "app.currency_symbol";
        public const string AccentColor = "ui.accent";
        public const string Density = "ui.density";
        public const string FontScale = "ui.font_scale";
        public const string ReduceMotion = "ui.reduce_motion";

        public const string ClinicName = "clinic.name";
        public const string ClinicLogo = "clinic.logo";
        public const string ClinicPhone = "clinic.phone";
        public const string ClinicPhone2 = "clinic.phone2";
        public const string ClinicEmail = "clinic.email";
        public const string ClinicWebsite = "clinic.website";
        public const string ClinicAddress = "clinic.address";
        public const string ClinicCity = "clinic.city";
        public const string ClinicDistrict = "clinic.district";
        public const string ClinicDivision = "clinic.division";
        public const string ClinicCountry = "clinic.country";
        public const string ClinicPostal = "clinic.postal";
        public const string ClinicRegistration = "clinic.registration";
        public const string ClinicTaxId = "clinic.tax_id";
        public const string InvoiceFooter = "clinic.invoice_footer";
        public const string ReceiptFooter = "clinic.receipt_footer";

        public const string DentistName = "dentist.name";
        public const string DentistTitle = "dentist.title";
        public const string DentistQualification = "dentist.qualification";
        public const string DentistSpecialty = "dentist.specialty";
        public const string DentistRegistration = "dentist.bmdc";
        public const string DentistPhone = "dentist.phone";
        public const string DentistEmail = "dentist.email";
        public const string DentistSignature = "dentist.signature";
        public const string DentistPhoto = "dentist.photo";

        public const string PatientCodePrefix = "numbering.patient_prefix";
        public const string PatientCodePadding = "numbering.patient_padding";
        public const string PatientCodeNext = "numbering.patient_next";
        public const string InvoicePrefix = "numbering.invoice_prefix";
        public const string InvoicePadding = "numbering.invoice_padding";
        public const string InvoiceNext = "numbering.invoice_next";
        public const string ReceiptPrefix = "numbering.receipt_prefix";
        public const string ReceiptPadding = "numbering.receipt_padding";
        public const string ReceiptNext = "numbering.receipt_next";
        public const string PrescriptionPrefix = "numbering.prescription_prefix";
        public const string PrescriptionPadding = "numbering.prescription_padding";
        public const string PrescriptionNext = "numbering.prescription_next";
        public const string StaffPrefix = "numbering.staff_prefix";
        public const string StaffPadding = "numbering.staff_padding";
        public const string StaffNext = "numbering.staff_next";

        public const string TaxEnabled = "billing.tax_enabled";
        public const string TaxRate = "billing.tax_rate";
        public const string TaxLabel = "billing.tax_label";
        public const string AllowAdvance = "billing.allow_advance";
        public const string DefaultConsultationFee = "billing.consultation_fee";

        public const string WorkingHoursStart = "appointment.start";
        public const string WorkingHoursEnd = "appointment.end";
        public const string SlotMinutes = "appointment.slot_minutes";
        public const string SerialAuto = "appointment.serial_auto";
        public const string SerialResetDaily = "appointment.serial_reset_daily";
        public const string DoubleBookingBlock = "appointment.block_double";

        public const string AutoLockEnabled = "security.autolock_enabled";
        public const string AutoLockMinutes = "security.autolock_minutes";

        public const string BackupFolder = "backup.folder";
        public const string BackupAuto = "backup.auto";
        public const string BackupFrequency = "backup.frequency";
        public const string BackupOnExit = "backup.on_exit";
        public const string BackupRetention = "backup.retention";
        public const string BackupLastUtc = "backup.last_utc";
        public const string BackupVerify = "backup.verify";

        public const string PrintPaper = "print.paper";
        public const string PrintPrinter = "print.printer";
        public const string PrintMarginMm = "print.margin_mm";
        public const string PrintCopies = "print.copies";
        public const string ReceiptWidthMm = "print.receipt_width_mm";
    }

    public static IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [Keys.Language] = "en",
        [Keys.DateFormat] = "dd MMM yyyy",
        [Keys.TimeFormat] = "hh:mm tt",
        [Keys.Currency] = "BDT",
        [Keys.CurrencySymbol] = "৳",
        [Keys.AccentColor] = "#0E5FA6",
        [Keys.Density] = "Comfortable",
        [Keys.FontScale] = "1.0",
        [Keys.ReduceMotion] = "False",
        [Keys.ClinicCountry] = "Bangladesh",
        [Keys.PatientCodePrefix] = "DEN-",
        [Keys.PatientCodePadding] = "6",
        [Keys.PatientCodeNext] = "1",
        [Keys.InvoicePrefix] = "INV-",
        [Keys.InvoicePadding] = "6",
        [Keys.InvoiceNext] = "1",
        [Keys.ReceiptPrefix] = "RCP-",
        [Keys.ReceiptPadding] = "6",
        [Keys.ReceiptNext] = "1",
        [Keys.PrescriptionPrefix] = "RX-",
        [Keys.PrescriptionPadding] = "6",
        [Keys.PrescriptionNext] = "1",
        [Keys.StaffPrefix] = "STF-",
        [Keys.StaffPadding] = "4",
        [Keys.StaffNext] = "1",
        [Keys.TaxEnabled] = "False",
        [Keys.TaxRate] = "0",
        [Keys.TaxLabel] = "VAT",
        [Keys.AllowAdvance] = "True",
        [Keys.DefaultConsultationFee] = "0",
        [Keys.WorkingHoursStart] = "09:00",
        [Keys.WorkingHoursEnd] = "21:00",
        [Keys.SlotMinutes] = "30",
        [Keys.SerialAuto] = "True",
        [Keys.SerialResetDaily] = "True",
        [Keys.DoubleBookingBlock] = "True",
        [Keys.AutoLockEnabled] = "True",
        [Keys.AutoLockMinutes] = "15",
        [Keys.BackupAuto] = "False",
        [Keys.BackupFrequency] = "Daily",
        [Keys.BackupOnExit] = "False",
        [Keys.BackupRetention] = "10",
        [Keys.BackupVerify] = "True",
        [Keys.PrintPaper] = "A4",
        [Keys.PrintMarginMm] = "12",
        [Keys.PrintCopies] = "1",
        [Keys.ReceiptWidthMm] = "80",
        [Keys.SetupCompleted] = "False"
    };

    public void EnsureDefaults()
    {
        EnsureLoaded();
        foreach (var (key, value) in Defaults)
        {
            lock (_sync)
            {
                if (_cache.ContainsKey(key))
                {
                    continue;
                }
            }

            Set(key, value);
        }
    }
}
