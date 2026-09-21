using Dentiva.Core.Data;

namespace Dentiva.Core.Services;

/// <summary>
/// Seeds configuration reference data only — treatment categories, expense categories, payment
/// methods and appointment types. It never creates patients, invoices or any other clinical or
/// financial record: the clinic always starts with a genuinely empty database.
/// </summary>
public sealed class ReferenceDataSeeder
{
    private readonly Database _db;
    private readonly SettingsService _settings;

    public ReferenceDataSeeder(Database db, SettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    private static readonly (string Name, string NameBn)[] TreatmentCategories =
    {
        ("Consultation", "পরামর্শ"),
        ("Scaling & Polishing", "স্কেলিং ও পলিশিং"),
        ("Restoration / Filling", "ফিলিং"),
        ("Root Canal Treatment", "রুট ক্যানেল চিকিৎসা"),
        ("Extraction", "দাঁত তোলা"),
        ("Crown & Bridge", "ক্রাউন ও ব্রিজ"),
        ("Implant", "ইমপ্ল্যান্ট"),
        ("Orthodontic Treatment", "অর্থোডন্টিক চিকিৎসা"),
        ("Periodontal Treatment", "পেরিওডন্টাল চিকিৎসা"),
        ("Prosthodontic Treatment", "প্রস্থোডন্টিক চিকিৎসা"),
        ("Teeth Whitening", "দাঁত সাদা করা"),
        ("Paediatric Dentistry", "শিশু দন্তচিকিৎসা"),
        ("Oral Surgery", "ওরাল সার্জারি"),
        ("X-Ray / Imaging", "এক্স-রে / ইমেজিং"),
        ("Emergency Treatment", "জরুরি চিকিৎসা")
    };

    private static readonly (string Name, string NameBn)[] ExpenseCategories =
    {
        ("Chamber Rent", "চেম্বার ভাড়া"),
        ("Electricity", "বিদ্যুৎ বিল"),
        ("Internet", "ইন্টারনেট"),
        ("Water & Utilities", "পানি ও ইউটিলিটি"),
        ("Staff Salary", "কর্মীর বেতন"),
        ("Dental Materials", "ডেন্টাল উপকরণ"),
        ("Equipment", "যন্ত্রপাতি"),
        ("Equipment Maintenance", "যন্ত্রপাতি রক্ষণাবেক্ষণ"),
        ("Laboratory", "ল্যাবরেটরি"),
        ("Medicine & Supplies", "ঔষধ ও সরবরাহ"),
        ("Software & Services", "সফটওয়্যার ও সেবা"),
        ("Transport", "যাতায়াত"),
        ("Marketing", "বিপণন"),
        ("Government Fees", "সরকারি ফি"),
        ("Miscellaneous", "বিবিধ")
    };

    private static readonly string[] PaymentMethods =
    {
        "Cash", "bKash", "Nagad", "Rocket", "Upay", "Bank Transfer", "Card", "Cheque", "Other"
    };

    private static readonly (string Name, int Minutes)[] AppointmentTypes =
    {
        ("Consultation", 20),
        ("Follow-up", 15),
        ("Scaling", 45),
        ("Filling", 45),
        ("Root Canal", 60),
        ("Extraction", 30),
        ("Orthodontic Adjustment", 30),
        ("Emergency", 30)
    };

    public void Seed()
    {
        if (_settings.GetBool(SettingsService.Keys.SchemaSeeded))
        {
            return;
        }

        var now = DateTime.UtcNow.ToString("O");

        using (var tx = _db.Connection.BeginTransaction())
        {
            var order = 0;
            foreach (var (name, bn) in TreatmentCategories)
            {
                using var cmd = _db.CreateCommand("INSERT OR IGNORE INTO treatment_categories(name, name_bn, default_fee, is_active, sort_order, created_utc) VALUES($n,$bn,0,1,$o,$c)");
                cmd.Transaction = tx;
                cmd.AddValue("$n", name);
                cmd.AddValue("$bn", bn);
                cmd.AddValue("$o", order++);
                cmd.AddValue("$c", now);
                cmd.ExecuteNonQuery();
            }

            order = 0;
            foreach (var (name, bn) in ExpenseCategories)
            {
                using var cmd = _db.CreateCommand("INSERT OR IGNORE INTO expense_categories(name, name_bn, is_active, sort_order, created_utc) VALUES($n,$bn,1,$o,$c)");
                cmd.Transaction = tx;
                cmd.AddValue("$n", name);
                cmd.AddValue("$bn", bn);
                cmd.AddValue("$o", order++);
                cmd.AddValue("$c", now);
                cmd.ExecuteNonQuery();
            }

            order = 0;
            foreach (var method in PaymentMethods)
            {
                using var cmd = _db.CreateCommand("INSERT OR IGNORE INTO payment_methods(name, is_active, sort_order) VALUES($n,1,$o)");
                cmd.Transaction = tx;
                cmd.AddValue("$n", method);
                cmd.AddValue("$o", order++);
                cmd.ExecuteNonQuery();
            }

            order = 0;
            foreach (var (name, minutes) in AppointmentTypes)
            {
                using var cmd = _db.CreateCommand("INSERT OR IGNORE INTO appointment_types(name, duration_minutes, is_active, sort_order) VALUES($n,$d,1,$o)");
                cmd.Transaction = tx;
                cmd.AddValue("$n", name);
                cmd.AddValue("$d", minutes);
                cmd.AddValue("$o", order++);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        _settings.Set(SettingsService.Keys.SchemaSeeded, true);
    }
}
