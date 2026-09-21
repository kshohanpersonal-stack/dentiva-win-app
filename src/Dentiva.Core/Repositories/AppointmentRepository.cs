using Dentiva.Core.Data;
using Dentiva.Core.Models;
using Dentiva.Core.Services;

namespace Dentiva.Core.Repositories;

/// <summary>
/// Appointment scheduling, the daily serial queue and double-booking protection.
/// </summary>
public sealed class AppointmentRepository
{
    private readonly Database _db;
    private readonly AuditService _audit;
    private readonly SettingsService _settings;

    public AppointmentRepository(Database db, AuditService audit, SettingsService settings)
    {
        _db = db;
        _audit = audit;
        _settings = settings;
    }

    public Task<List<Appointment>> GetForDateAsync(DateTime date, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<Appointment>();
        using var cmd = _db.CreateCommand("""
            SELECT a.*, p.full_name AS patient_name, p.patient_code, p.phone AS patient_phone
            FROM appointments a JOIN patients p ON p.id=a.patient_id
            WHERE a.appointment_date = $d
            ORDER BY COALESCE(a.serial_number, 9999), a.start_time, a.id
            """);
        cmd.AddDate("$d", date);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }, ct);

    public Task<List<Appointment>> GetRangeAsync(DateTime from, DateTime to, string? status = null, long? patientId = null, CancellationToken ct = default) => Task.Run(() =>
    {
        var where = new List<string> { "a.appointment_date BETWEEN $f AND $t" };
        if (!string.IsNullOrWhiteSpace(status) && status != "All") where.Add("a.status = $s");
        if (patientId is not null) where.Add("a.patient_id = $p");

        var list = new List<Appointment>();
        using var cmd = _db.CreateCommand($"""
            SELECT a.*, p.full_name AS patient_name, p.patient_code, p.phone AS patient_phone
            FROM appointments a JOIN patients p ON p.id=a.patient_id
            WHERE {string.Join(" AND ", where)}
            ORDER BY a.appointment_date, a.start_time, a.id
            """);
        cmd.AddDate("$f", from);
        cmd.AddDate("$t", to);
        if (!string.IsNullOrWhiteSpace(status) && status != "All") cmd.AddValue("$s", status);
        if (patientId is { } pid) cmd.AddValue("$p", pid);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }, ct);

    public Task<List<Appointment>> GetUpcomingAsync(int limit = 10, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<Appointment>();
        using var cmd = _db.CreateCommand("""
            SELECT a.*, p.full_name AS patient_name, p.patient_code, p.phone AS patient_phone
            FROM appointments a JOIN patients p ON p.id=a.patient_id
            WHERE (a.appointment_date > $today OR (a.appointment_date = $today AND a.status IN ('Scheduled','Confirmed')))
              AND a.status NOT IN ('Cancelled','Completed','No Show')
            ORDER BY a.appointment_date, a.start_time LIMIT $l
            """);
        cmd.AddDate("$today", DateTime.Today);
        cmd.AddValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }, ct);

    /// <summary>
    /// Detects an overlapping appointment for the same dentist. Cancelled and completed slots are ignored.
    /// </summary>
    public Task<Appointment?> FindConflictAsync(DateTime date, TimeSpan start, int durationMinutes, long? dentistId, long excludeId = 0, CancellationToken ct = default) => Task.Run(() =>
    {
        var end = start.Add(TimeSpan.FromMinutes(Math.Max(1, durationMinutes)));
        using var cmd = _db.CreateCommand("""
            SELECT a.*, p.full_name AS patient_name, p.patient_code, p.phone AS patient_phone
            FROM appointments a JOIN patients p ON p.id=a.patient_id
            WHERE a.appointment_date=$d AND a.id <> $ex
              AND a.status NOT IN ('Cancelled','No Show','Completed')
              AND ($dentist IS NULL OR a.dentist_id IS NULL OR a.dentist_id = $dentist)
            """);
        cmd.AddDate("$d", date);
        cmd.AddValue("$ex", excludeId);
        cmd.AddValue("$dentist", dentistId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var existing = Map(r);
            var existingStart = existing.StartTime;
            var existingEnd = existing.EndTime ?? existingStart.Add(TimeSpan.FromMinutes(Math.Max(1, existing.DurationMinutes)));
            if (start < existingEnd && existingStart < end)
            {
                return existing;
            }
        }

        return null;
    }, ct);

    public Task<int> GetNextSerialAsync(DateTime date, CancellationToken ct = default) => Task.Run(() =>
    {
        using var cmd = _db.CreateCommand("SELECT COALESCE(MAX(serial_number),0)+1 FROM appointments WHERE appointment_date=$d");
        cmd.AddDate("$d", date);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 1);
    }, ct);

    public async Task<long> SaveAsync(Appointment appointment, string? actor, bool overrideConflict = false, CancellationToken ct = default)
    {
        if (appointment.PatientId <= 0)
            throw new DentivaValidationException("Select a patient for the appointment.");
        if (appointment.DurationMinutes is < 1 or > 1440)
            throw new DentivaValidationException("The appointment duration must be between 1 and 1440 minutes.");

        if (!overrideConflict && _settings.GetBool(SettingsService.Keys.DoubleBookingBlock, true))
        {
            var conflict = await FindConflictAsync(appointment.AppointmentDate, appointment.StartTime,
                appointment.DurationMinutes, appointment.DentistId, appointment.Id, ct).ConfigureAwait(false);
            if (conflict is not null)
            {
                throw new AppointmentConflictException(
                    $"This time overlaps with {conflict.PatientName} at {conflict.StartTime:hh\\:mm}. Choose another slot or confirm the double booking.",
                    conflict);
            }
        }

        if (appointment.SerialNumber is null && _settings.GetBool(SettingsService.Keys.SerialAuto, true))
        {
            appointment.SerialNumber = await GetNextSerialAsync(appointment.AppointmentDate, ct).ConfigureAwait(false);
        }

        appointment.EndTime = appointment.StartTime.Add(TimeSpan.FromMinutes(appointment.DurationMinutes));
        appointment.UpdatedUtc = DateTime.UtcNow;
        var isNew = appointment.Id == 0;

        var id = await _db.WriteAsync(tx =>
        {
            if (isNew)
            {
                appointment.CreatedUtc = DateTime.UtcNow;
                appointment.CreatedBy = actor;
                using var cmd = _db.CreateCommand("""
                    INSERT INTO appointments(patient_id, appointment_date, start_time, end_time, duration_minutes, serial_number,
                        appointment_type, reason, status, priority, dentist_id, dentist_name, notes, checked_in_utc, completed_utc,
                        created_utc, created_by, updated_utc)
                    VALUES($p,$d,$st,$et,$dur,$serial,$type,$reason,$status,$prio,$did,$dname,$notes,$ci,$comp,$c,$cb,$u);
                    SELECT last_insert_rowid();
                    """);
                cmd.Transaction = tx;
                Bind(cmd, appointment);
                appointment.Id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            else
            {
                using var cmd = _db.CreateCommand("""
                    UPDATE appointments SET patient_id=$p, appointment_date=$d, start_time=$st, end_time=$et,
                        duration_minutes=$dur, serial_number=$serial, appointment_type=$type, reason=$reason, status=$status,
                        priority=$prio, dentist_id=$did, dentist_name=$dname, notes=$notes, checked_in_utc=$ci,
                        completed_utc=$comp, updated_utc=$u WHERE id=$id
                    """);
                cmd.Transaction = tx;
                Bind(cmd, appointment);
                cmd.AddValue("$id", appointment.Id);
                cmd.ExecuteNonQuery();
            }

            return Task.FromResult(appointment.Id);
        }, ct).ConfigureAwait(false);

        _audit.Log(isNew ? "appointment.created" : "appointment.updated", "Appointment", id.ToString(),
            $"{appointment.AppointmentDate:yyyy-MM-dd} {appointment.StartTime:hh\\:mm}", actor);
        return id;
    }

    public async Task SetStatusAsync(long id, string status, string? actor, CancellationToken ct = default)
    {
        if (!AppointmentStatuses.All.Contains(status))
            throw new DentivaValidationException($"'{status}' is not a recognised appointment status.");

        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("""
                UPDATE appointments SET status=$s, updated_utc=$u,
                    checked_in_utc = CASE WHEN $s='Checked In' AND checked_in_utc IS NULL THEN $u ELSE checked_in_utc END,
                    completed_utc  = CASE WHEN $s='Completed' THEN $u ELSE completed_utc END
                WHERE id=$id
                """);
            cmd.Transaction = tx;
            cmd.AddValue("$s", status);
            cmd.AddValue("$u", DateTime.UtcNow.ToString("O"));
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        _audit.Log("appointment.status", "Appointment", id.ToString(), status, actor);
    }

    public async Task DeleteAsync(long id, string? actor, CancellationToken ct = default)
    {
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("DELETE FROM appointments WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        _audit.Log("appointment.deleted", "Appointment", id.ToString(), null, actor);
    }

    public Task<Dictionary<string, int>> GetStatusCountsAsync(DateTime date, CancellationToken ct = default) => Task.Run(() =>
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in AppointmentStatuses.All) map[s] = 0;
        using var cmd = _db.CreateCommand("SELECT status, COUNT(*) FROM appointments WHERE appointment_date=$d GROUP BY status");
        cmd.AddDate("$d", date);
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }, ct);

    public Task<List<string>> GetAppointmentTypesAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<string>();
        using var cmd = _db.CreateCommand("SELECT name FROM appointment_types WHERE is_active=1 ORDER BY sort_order, name");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }, ct);

    public async Task SaveAppointmentTypeAsync(string name, int duration, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DentivaValidationException("The appointment type name is required.");
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("""
                INSERT INTO appointment_types(name, duration_minutes, is_active, sort_order) VALUES($n,$d,1,999)
                ON CONFLICT(name) DO UPDATE SET duration_minutes=excluded.duration_minutes, is_active=1
                """);
            cmd.Transaction = tx;
            cmd.AddValue("$n", name.Trim());
            cmd.AddValue("$d", Math.Clamp(duration, 1, 1440));
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    private static void Bind(Microsoft.Data.Sqlite.SqliteCommand cmd, Appointment a)
    {
        cmd.AddValue("$p", a.PatientId);
        cmd.AddDate("$d", a.AppointmentDate);
        cmd.AddTime("$st", a.StartTime);
        cmd.AddTime("$et", a.EndTime);
        cmd.AddValue("$dur", a.DurationMinutes);
        cmd.AddValue("$serial", a.SerialNumber);
        cmd.AddValue("$type", a.AppointmentType);
        cmd.AddValue("$reason", a.Reason);
        cmd.AddValue("$status", a.Status);
        cmd.AddValue("$prio", a.Priority);
        cmd.AddValue("$did", a.DentistId);
        cmd.AddValue("$dname", a.DentistName);
        cmd.AddValue("$notes", a.Notes);
        cmd.AddUtc("$ci", a.CheckedInUtc);
        cmd.AddUtc("$comp", a.CompletedUtc);
        cmd.AddValue("$c", a.CreatedUtc.ToString("O"));
        cmd.AddValue("$cb", a.CreatedBy);
        cmd.AddValue("$u", a.UpdatedUtc.ToString("O"));
    }

    private static Appointment Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64Value("id"),
        PatientId = r.GetInt64Value("patient_id"),
        AppointmentDate = r.GetDateValue("appointment_date"),
        StartTime = r.GetTimeValue("start_time"),
        EndTime = string.IsNullOrEmpty(r.GetStringOrNull("end_time")) ? null : r.GetTimeValue("end_time"),
        DurationMinutes = r.GetIntValue("duration_minutes"),
        SerialNumber = r.GetIntOrNull("serial_number"),
        AppointmentType = r.GetStringOrNull("appointment_type"),
        Reason = r.GetStringOrNull("reason"),
        Status = r.GetStringOrEmpty("status"),
        Priority = r.GetStringOrEmpty("priority"),
        DentistId = r.GetInt64OrNull("dentist_id"),
        DentistName = r.GetStringOrNull("dentist_name"),
        Notes = r.GetStringOrNull("notes"),
        CheckedInUtc = r.GetUtcOrNull("checked_in_utc"),
        CompletedUtc = r.GetUtcOrNull("completed_utc"),
        CreatedUtc = r.GetUtcValue("created_utc"),
        UpdatedUtc = r.GetUtcValue("updated_utc"),
        PatientName = r.GetStringOrEmpty("patient_name"),
        PatientCode = r.GetStringOrEmpty("patient_code"),
        PatientPhone = r.GetStringOrNull("patient_phone")
    };
}

public sealed class AppointmentConflictException : Exception
{
    public AppointmentConflictException(string message, Appointment conflicting) : base(message)
    {
        Conflicting = conflicting;
    }

    public Appointment Conflicting { get; }
}
