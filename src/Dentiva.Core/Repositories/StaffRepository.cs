using Dentiva.Core.Data;
using Dentiva.Core.Models;
using Dentiva.Core.Services;

namespace Dentiva.Core.Repositories;

public sealed class StaffRepository
{
    private readonly Database _db;
    private readonly SequenceService _sequences;
    private readonly AuditService _audit;

    public StaffRepository(Database db, SequenceService sequences, AuditService audit)
    {
        _db = db;
        _sequences = sequences;
        _audit = audit;
    }

    public Task<List<StaffMember>> GetAllAsync(string? search = null, bool activeOnly = false, CancellationToken ct = default) => Task.Run(() =>
    {
        var where = new List<string>();
        if (activeOnly) where.Add("status = 'Active'");
        if (!string.IsNullOrWhiteSpace(search)) where.Add("(full_name LIKE $s OR staff_code LIKE $s OR role LIKE $s OR phone LIKE $s)");
        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;

        var list = new List<StaffMember>();
        using var cmd = _db.CreateCommand($"SELECT * FROM staff {whereSql} ORDER BY full_name COLLATE NOCASE");
        if (!string.IsNullOrWhiteSpace(search)) cmd.AddValue("$s", "%" + search.Trim() + "%");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }, ct);

    public async Task<long> SaveAsync(StaffMember staff, string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(staff.FullName))
            throw new DentivaValidationException("The staff member's name is required.");
        if (string.IsNullOrWhiteSpace(staff.Role))
            throw new DentivaValidationException("Select a role for the staff member.");
        var phone = Validation.ValidatePhone(staff.Phone);
        if (!phone.IsValid) throw new DentivaValidationException(phone.Message!);
        var email = Validation.ValidateEmail(staff.Email);
        if (!email.IsValid) throw new DentivaValidationException(email.Message!);
        var salary = Validation.ValidateAmount(staff.Salary);
        if (!salary.IsValid) throw new DentivaValidationException(salary.Message!);

        var isNew = staff.Id == 0;
        if (isNew && string.IsNullOrWhiteSpace(staff.StaffCode))
        {
            staff.StaffCode = _sequences.NextStaffCode();
        }

        staff.UpdatedUtc = DateTime.UtcNow;

        var id = await _db.WriteAsync(tx =>
        {
            if (isNew)
            {
                staff.CreatedUtc = DateTime.UtcNow;
                using var cmd = _db.CreateCommand("""
                    INSERT INTO staff(staff_code, full_name, role, department, phone, email, address, joining_date,
                        salary, salary_type, payment_schedule, status, emergency_contact, notes, created_utc, updated_utc)
                    VALUES($code,$n,$r,$dept,$p,$e,$addr,$jd,$sal,$st,$sched,$status,$ec,$notes,$c,$u);
                    SELECT last_insert_rowid();
                    """);
                cmd.Transaction = tx;
                Bind(cmd, staff);
                staff.Id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            else
            {
                using var cmd = _db.CreateCommand("""
                    UPDATE staff SET full_name=$n, role=$r, department=$dept, phone=$p, email=$e, address=$addr,
                        joining_date=$jd, salary=$sal, salary_type=$st, payment_schedule=$sched, status=$status,
                        emergency_contact=$ec, notes=$notes, updated_utc=$u WHERE id=$id
                    """);
                cmd.Transaction = tx;
                Bind(cmd, staff);
                cmd.AddValue("$id", staff.Id);
                cmd.ExecuteNonQuery();
            }

            return Task.FromResult(staff.Id);
        }, ct).ConfigureAwait(false);

        _audit.Log(isNew ? "staff.created" : "staff.updated", "Staff", id.ToString(), staff.FullName, actor);
        return id;
    }

    private static void Bind(Microsoft.Data.Sqlite.SqliteCommand cmd, StaffMember s)
    {
        cmd.AddValue("$code", s.StaffCode);
        cmd.AddValue("$n", s.FullName.Trim());
        cmd.AddValue("$r", s.Role);
        cmd.AddValue("$dept", s.Department);
        cmd.AddValue("$p", s.Phone);
        cmd.AddValue("$e", s.Email);
        cmd.AddValue("$addr", s.Address);
        cmd.AddDate("$jd", s.JoiningDate);
        cmd.AddMoney("$sal", s.Salary);
        cmd.AddValue("$st", s.SalaryType);
        cmd.AddValue("$sched", s.PaymentSchedule);
        cmd.AddValue("$status", s.Status);
        cmd.AddValue("$ec", s.EmergencyContact);
        cmd.AddValue("$notes", s.Notes);
        cmd.AddValue("$c", s.CreatedUtc.ToString("O"));
        cmd.AddValue("$u", s.UpdatedUtc.ToString("O"));
    }

    private static StaffMember Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64Value("id"),
        StaffCode = r.GetStringOrEmpty("staff_code"),
        FullName = r.GetStringOrEmpty("full_name"),
        Role = r.GetStringOrEmpty("role"),
        Department = r.GetStringOrNull("department"),
        Phone = r.GetStringOrNull("phone"),
        Email = r.GetStringOrNull("email"),
        Address = r.GetStringOrNull("address"),
        JoiningDate = r.GetDateOrNull("joining_date"),
        Salary = r.GetDecimalValue("salary"),
        SalaryType = r.GetStringOrEmpty("salary_type"),
        PaymentSchedule = r.GetStringOrNull("payment_schedule"),
        Status = r.GetStringOrEmpty("status"),
        EmergencyContact = r.GetStringOrNull("emergency_contact"),
        Notes = r.GetStringOrNull("notes"),
        CreatedUtc = r.GetUtcValue("created_utc"),
        UpdatedUtc = r.GetUtcValue("updated_utc")
    };

    public async Task DeleteAsync(long id, string? actor, CancellationToken ct = default)
    {
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("DELETE FROM staff WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        _audit.Log("staff.deleted", "Staff", id.ToString(), null, actor);
    }

    public Task<List<SalaryPayment>> GetSalaryPaymentsAsync(long? staffId = null, DateTime? from = null, DateTime? to = null, CancellationToken ct = default) => Task.Run(() =>
    {
        var where = new List<string>();
        if (staffId is not null) where.Add("sp.staff_id = $s");
        if (from is not null) where.Add("sp.payment_date >= $f");
        if (to is not null) where.Add("sp.payment_date <= $t");
        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;

        var list = new List<SalaryPayment>();
        using var cmd = _db.CreateCommand($"""
            SELECT sp.*, s.full_name AS staff_name FROM salary_payments sp
            JOIN staff s ON s.id = sp.staff_id {whereSql}
            ORDER BY sp.payment_date DESC, sp.id DESC
            """);
        if (staffId is { } sid) cmd.AddValue("$s", sid);
        if (from is { } f) cmd.AddDate("$f", f);
        if (to is { } t) cmd.AddDate("$t", t);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SalaryPayment
            {
                Id = r.GetInt64Value("id"),
                StaffId = r.GetInt64Value("staff_id"),
                PaymentDate = r.GetDateValue("payment_date"),
                PeriodLabel = r.GetStringOrNull("period_label"),
                Amount = r.GetDecimalValue("amount"),
                Method = r.GetStringOrNull("method"),
                Reference = r.GetStringOrNull("reference"),
                Notes = r.GetStringOrNull("notes"),
                StaffName = r.GetStringOrEmpty("staff_name")
            });
        }

        return list;
    }, ct);

    public async Task<long> SaveSalaryPaymentAsync(SalaryPayment payment, string? actor, CancellationToken ct = default)
    {
        var amount = Validation.ValidateAmount(payment.Amount, allowZero: false);
        if (!amount.IsValid) throw new DentivaValidationException(amount.Message!);
        if (payment.StaffId <= 0) throw new DentivaValidationException("Select the staff member being paid.");

        var id = await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("""
                INSERT INTO salary_payments(staff_id, payment_date, period_label, amount, method, reference, notes, created_utc)
                VALUES($s,$d,$p,$a,$m,$r,$n,$c);
                SELECT last_insert_rowid();
                """);
            cmd.Transaction = tx;
            cmd.AddValue("$s", payment.StaffId);
            cmd.AddDate("$d", payment.PaymentDate);
            cmd.AddValue("$p", payment.PeriodLabel);
            cmd.AddMoney("$a", payment.Amount);
            cmd.AddValue("$m", payment.Method);
            cmd.AddValue("$r", payment.Reference);
            cmd.AddValue("$n", payment.Notes);
            cmd.AddValue("$c", DateTime.UtcNow.ToString("O"));
            return Task.FromResult(Convert.ToInt64(cmd.ExecuteScalar() ?? 0L));
        }, ct).ConfigureAwait(false);

        _audit.Log("salary.paid", "SalaryPayment", id.ToString(), $"{payment.Amount:0.00}", actor);
        return id;
    }

    public async Task DeleteSalaryPaymentAsync(long id, string? actor, CancellationToken ct = default)
    {
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("DELETE FROM salary_payments WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        _audit.Log("salary.deleted", "SalaryPayment", id.ToString(), null, actor);
    }
}
