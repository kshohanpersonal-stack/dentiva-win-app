using Dentiva.Core.Data;
using Dentiva.Core.Models;

namespace Dentiva.Core.Services;

/// <summary>
/// Persisted notification centre. Operational alerts are recomputed from live data each refresh so
/// the list can never show a stale or invented warning.
/// </summary>
public sealed class NotificationService
{
    private readonly Database _db;
    private readonly AppPaths _paths;

    public NotificationService(Database db, AppPaths paths)
    {
        _db = db;
        _paths = paths;
    }

    public event EventHandler? Changed;

    public void Push(string title, string? message, string severity = "Information", string? category = null, string? linkEntity = null, string? linkId = null)
    {
        try
        {
            using var cmd = _db.CreateCommand("""
                INSERT INTO notifications(created_utc, severity, title, message, category, is_read, link_entity, link_id)
                VALUES($c,$s,$t,$m,$cat,0,$le,$li)
                """);
            cmd.AddValue("$c", DateTime.UtcNow.ToString("O"));
            cmd.AddValue("$s", severity);
            cmd.AddValue("$t", title);
            cmd.AddValue("$m", message);
            cmd.AddValue("$cat", category);
            cmd.AddValue("$le", linkEntity);
            cmd.AddValue("$li", linkId);
            cmd.ExecuteNonQuery();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Notifications are non-critical.
        }
    }

    public Task<List<AppNotification>> GetAsync(bool unreadOnly = false, int limit = 100, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<AppNotification>();
        using var cmd = _db.CreateCommand(unreadOnly
            ? "SELECT * FROM notifications WHERE is_read=0 ORDER BY created_utc DESC LIMIT $l"
            : "SELECT * FROM notifications ORDER BY created_utc DESC LIMIT $l");
        cmd.AddValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AppNotification
            {
                Id = r.GetInt64Value("id"),
                CreatedUtc = r.GetUtcValue("created_utc"),
                Severity = r.GetStringOrEmpty("severity"),
                Title = r.GetStringOrEmpty("title"),
                Message = r.GetStringOrNull("message"),
                Category = r.GetStringOrNull("category"),
                IsRead = r.GetBoolValue("is_read"),
                LinkEntity = r.GetStringOrNull("link_entity"),
                LinkId = r.GetStringOrNull("link_id")
            });
        }

        return list;
    }, ct);

    public int GetUnreadCount()
    {
        using var cmd = _db.CreateCommand("SELECT COUNT(*) FROM notifications WHERE is_read=0");
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public void MarkRead(long id)
    {
        using var cmd = _db.CreateCommand("UPDATE notifications SET is_read=1 WHERE id=$id");
        cmd.AddValue("$id", id);
        cmd.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkAllRead()
    {
        using var cmd = _db.CreateCommand("UPDATE notifications SET is_read=1 WHERE is_read=0");
        cmd.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        using var cmd = _db.CreateCommand("DELETE FROM notifications");
        cmd.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Recomputes operational alerts (appointments, follow-ups, dues, storage) without duplicating
    /// notifications that were already raised for the same subject today.
    /// </summary>
    public Task RefreshOperationalAlertsAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var today = DateTime.Today;

        var todayAppointments = Scalar("SELECT COUNT(*) FROM appointments WHERE appointment_date=$d AND status IN ('Scheduled','Confirmed')", ("$d", today.ToDbDate()));
        if (todayAppointments > 0)
        {
            PushOnce($"{todayAppointments} appointment(s) scheduled today", "Open the queue to check patients in.", "Information", "appointment");
        }

        var followUps = Scalar("""
            SELECT COUNT(*) FROM (
                SELECT patient_id FROM visits WHERE follow_up_date=$d
                UNION SELECT patient_id FROM treatments WHERE follow_up_date=$d)
            """, ("$d", today.ToDbDate()));
        if (followUps > 0)
        {
            PushOnce($"{followUps} follow-up(s) due today", "Review the follow-up list on the dashboard.", "Information", "followup");
        }

        var overdue = Scalar("""
            SELECT COUNT(*) FROM (
                SELECT patient_id FROM visits WHERE follow_up_date < $d AND follow_up_date >= $limit
                UNION SELECT patient_id FROM treatments WHERE follow_up_date < $d AND follow_up_date >= $limit)
            """, ("$d", today.ToDbDate()), ("$limit", today.AddDays(-30).ToDbDate()));
        if (overdue > 0)
        {
            PushOnce($"{overdue} follow-up(s) overdue", "These patients have passed their planned follow-up date.", "Warning", "followup");
        }

        var dues = Scalar("SELECT COUNT(*) FROM invoices WHERE status IN ('Unpaid','Partial')");
        if (dues > 0)
        {
            PushOnce($"{dues} invoice(s) with an outstanding balance", "Review pending payments in Billing.", "Warning", "billing");
        }

        var missed = Scalar("SELECT COUNT(*) FROM appointments WHERE appointment_date < $d AND status IN ('Scheduled','Confirmed')", ("$d", today.ToDbDate()));
        if (missed > 0)
        {
            PushOnce($"{missed} past appointment(s) were never closed", "Mark them as completed, cancelled or no-show.", "Warning", "appointment");
        }

        var free = _paths.GetAvailableFreeBytes();
        if (free >= 0 && free < 1024L * 1024 * 1024)
        {
            PushOnce("Low disk space", $"Only {free / (1024.0 * 1024):0} MB remain on the drive holding the Dentiva data folder.", "Danger", "storage");
        }

        return Task.CompletedTask;
    }, ct);

    private void PushOnce(string title, string? message, string severity, string category)
    {
        using (var check = _db.CreateCommand("SELECT COUNT(*) FROM notifications WHERE title=$t AND created_utc >= $since"))
        {
            check.AddValue("$t", title);
            check.AddValue("$since", DateTime.UtcNow.Date.ToString("O"));
            if (Convert.ToInt32(check.ExecuteScalar() ?? 0) > 0)
            {
                return;
            }
        }

        Push(title, message, severity, category);
    }

    private int Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        try
        {
            using var cmd = _db.CreateCommand(sql);
            foreach (var (n, v) in parameters) cmd.AddValue(n, v);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        catch
        {
            return 0;
        }
    }
}
