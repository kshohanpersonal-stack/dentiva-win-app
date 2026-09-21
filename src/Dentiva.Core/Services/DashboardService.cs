using Dentiva.Core.Data;
using Dentiva.Core.Models;

namespace Dentiva.Core.Services;

public sealed class DashboardSnapshot
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }

    public int TotalAppointments { get; set; }
    public int CheckedIn { get; set; }
    public int Waiting { get; set; }
    public int InTreatment { get; set; }
    public int Completed { get; set; }
    public int Cancelled { get; set; }
    public int NoShow { get; set; }
    public int Scheduled { get; set; }

    public decimal Billed { get; set; }
    public decimal Collected { get; set; }
    public decimal Outstanding { get; set; }

    public int NewPatients { get; set; }
    public int TotalPatients { get; set; }
    public int VisitsInPeriod { get; set; }
    public int TreatmentsInPeriod { get; set; }
}

public sealed record QueueEntry(long AppointmentId, int? Serial, string PatientCode, string PatientName,
    TimeSpan Time, string Status, string? Reason, string Priority, long PatientId);

public sealed record FollowUpEntry(long PatientId, string PatientCode, string PatientName, DateTime FollowUpDate, string? Reason);

public sealed record PendingPaymentEntry(long InvoiceId, string InvoiceNumber, long PatientId, string PatientCode,
    string PatientName, DateTime InvoiceDate, decimal Total, decimal Paid, decimal Due);

/// <summary>
/// Aggregates the clinical dashboard. Everything is computed in SQL from real records — if the
/// clinic has no data yet, every figure is genuinely zero.
/// </summary>
public sealed class DashboardService
{
    private readonly Database _db;

    public DashboardService(Database db)
    {
        _db = db;
    }

    public Task<DashboardSnapshot> GetSnapshotAsync(DateTime from, DateTime to, CancellationToken ct = default) => Task.Run(() =>
    {
        var s = new DashboardSnapshot { From = from.Date, To = to.Date };

        using (var cmd = _db.CreateCommand("SELECT status, COUNT(*) FROM appointments WHERE appointment_date BETWEEN $f AND $t GROUP BY status"))
        {
            cmd.AddDate("$f", from); cmd.AddDate("$t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var status = r.GetString(0);
                var count = r.GetInt32(1);
                s.TotalAppointments += count;
                switch (status)
                {
                    case AppointmentStatuses.CheckedIn: s.CheckedIn = count; break;
                    case AppointmentStatuses.Waiting: s.Waiting = count; break;
                    case AppointmentStatuses.InTreatment: s.InTreatment = count; break;
                    case AppointmentStatuses.Completed: s.Completed = count; break;
                    case AppointmentStatuses.Cancelled: s.Cancelled = count; break;
                    case AppointmentStatuses.NoShow: s.NoShow = count; break;
                    case AppointmentStatuses.Scheduled:
                    case AppointmentStatuses.Confirmed: s.Scheduled += count; break;
                }
            }
        }

        using (var cmd = _db.CreateCommand("SELECT COALESCE(SUM(total),0) FROM invoices WHERE invoice_date BETWEEN $f AND $t AND status <> 'Cancelled'"))
        {
            cmd.AddDate("$f", from); cmd.AddDate("$t", to);
            s.Billed = Convert.ToDecimal(cmd.ExecuteScalar() ?? 0d);
        }

        using (var cmd = _db.CreateCommand("SELECT COALESCE(SUM(CASE WHEN is_refund=1 THEN -amount ELSE amount END),0) FROM payments WHERE substr(payment_date,1,10) BETWEEN $f AND $t"))
        {
            cmd.AddDate("$f", from); cmd.AddDate("$t", to);
            s.Collected = Convert.ToDecimal(cmd.ExecuteScalar() ?? 0d);
        }

        using (var cmd = _db.CreateCommand("SELECT COALESCE(SUM(total - paid_amount),0) FROM invoices WHERE status NOT IN ('Cancelled','Paid')"))
        {
            s.Outstanding = Convert.ToDecimal(cmd.ExecuteScalar() ?? 0d);
        }

        using (var cmd = _db.CreateCommand("SELECT COUNT(*) FROM patients WHERE substr(created_utc,1,10) BETWEEN $f AND $t"))
        {
            cmd.AddDate("$f", from); cmd.AddDate("$t", to);
            s.NewPatients = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        using (var cmd = _db.CreateCommand("SELECT COUNT(*) FROM patients WHERE is_archived=0"))
        {
            s.TotalPatients = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        using (var cmd = _db.CreateCommand("SELECT COUNT(*) FROM visits WHERE visit_date BETWEEN $f AND $t"))
        {
            cmd.AddDate("$f", from); cmd.AddDate("$t", to);
            s.VisitsInPeriod = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        using (var cmd = _db.CreateCommand("SELECT COUNT(*) FROM treatments WHERE treatment_date BETWEEN $f AND $t"))
        {
            cmd.AddDate("$f", from); cmd.AddDate("$t", to);
            s.TreatmentsInPeriod = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        return s;
    }, ct);

    public Task<List<QueueEntry>> GetQueueAsync(DateTime date, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<QueueEntry>();
        using var cmd = _db.CreateCommand("""
            SELECT a.id, a.serial_number, a.start_time, a.status, a.reason, a.priority, a.patient_id,
                   p.patient_code, p.full_name
            FROM appointments a JOIN patients p ON p.id=a.patient_id
            WHERE a.appointment_date=$d
            ORDER BY COALESCE(a.serial_number, 9999), a.start_time
            """);
        cmd.AddDate("$d", date);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new QueueEntry(
                r.GetInt64Value("id"),
                r.GetIntOrNull("serial_number"),
                r.GetStringOrEmpty("patient_code"),
                r.GetStringOrEmpty("full_name"),
                r.GetTimeValue("start_time"),
                r.GetStringOrEmpty("status"),
                r.GetStringOrNull("reason"),
                r.GetStringOrEmpty("priority"),
                r.GetInt64Value("patient_id")));
        }

        return list;
    }, ct);

    public Task<List<FollowUpEntry>> GetFollowUpsAsync(DateTime date, bool includeOverdue = false, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<FollowUpEntry>();
        var comparison = includeOverdue ? "<=" : "=";
        using var cmd = _db.CreateCommand($"""
            SELECT DISTINCT p.id, p.patient_code, p.full_name, d.follow_up_date, d.reason FROM (
                SELECT patient_id, follow_up_date, reason FROM visits WHERE follow_up_date IS NOT NULL AND follow_up_date {comparison} $d
                UNION
                SELECT patient_id, follow_up_date, procedure_name FROM treatments WHERE follow_up_date IS NOT NULL AND follow_up_date {comparison} $d
            ) d JOIN patients p ON p.id = d.patient_id
            WHERE p.is_archived = 0
            ORDER BY d.follow_up_date DESC, p.full_name
            LIMIT 200
            """);
        cmd.AddDate("$d", date);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new FollowUpEntry(
                r.GetInt64(0), r.GetString(1), r.GetString(2),
                r.GetDateValue("follow_up_date"),
                r.IsDBNull(4) ? null : r.GetString(4)));
        }

        return list;
    }, ct);

    public Task<List<PendingPaymentEntry>> GetPendingPaymentsAsync(int limit = 50, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<PendingPaymentEntry>();
        using var cmd = _db.CreateCommand("""
            SELECT i.id, i.invoice_number, i.invoice_date, i.total, i.paid_amount,
                   p.id AS pid, p.patient_code, p.full_name
            FROM invoices i JOIN patients p ON p.id=i.patient_id
            WHERE i.status IN ('Unpaid','Partial') AND (i.total - i.paid_amount) > 0.004
            ORDER BY i.invoice_date ASC LIMIT $l
            """);
        cmd.AddValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var total = r.GetDecimalValue("total");
            var paid = r.GetDecimalValue("paid_amount");
            list.Add(new PendingPaymentEntry(
                r.GetInt64Value("id"),
                r.GetStringOrEmpty("invoice_number"),
                r.GetInt64Value("pid"),
                r.GetStringOrEmpty("patient_code"),
                r.GetStringOrEmpty("full_name"),
                r.GetDateValue("invoice_date"),
                total, paid, Math.Max(0m, total - paid)));
        }

        return list;
    }, ct);

    /// <summary>Daily patient-visit counts for the dashboard trend chart.</summary>
    public Task<List<(DateTime Day, int Count)>> GetVisitTrendAsync(DateTime from, DateTime to, CancellationToken ct = default) => Task.Run(() =>
    {
        var map = new Dictionary<DateTime, int>();
        using (var cmd = _db.CreateCommand("SELECT visit_date, COUNT(*) FROM visits WHERE visit_date BETWEEN $f AND $t GROUP BY visit_date"))
        {
            cmd.AddDate("$f", from); cmd.AddDate("$t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (DateTime.TryParse(r.GetString(0), out var d)) map[d.Date] = r.GetInt32(1);
            }
        }

        var points = new List<(DateTime, int)>();
        var days = Math.Min((to.Date - from.Date).Days, 730);
        for (var i = 0; i <= Math.Max(0, days); i++)
        {
            var day = from.Date.AddDays(i);
            points.Add((day, map.GetValueOrDefault(day)));
        }

        return points;
    }, ct);

    public Task<List<CategorySlice>> GetTreatmentDistributionAsync(DateTime from, DateTime to, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<CategorySlice>();
        using var cmd = _db.CreateCommand("""
            SELECT COALESCE(NULLIF(category_name,''),'Uncategorised') AS cat, COUNT(*)
            FROM treatments WHERE treatment_date BETWEEN $f AND $t GROUP BY cat ORDER BY 2 DESC LIMIT 12
            """);
        cmd.AddDate("$f", from); cmd.AddDate("$t", to);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new CategorySlice(r.GetString(0), r.GetInt32(1)));
        return list;
    }, ct);
}

public sealed record CategorySlice(string Label, int Count);
