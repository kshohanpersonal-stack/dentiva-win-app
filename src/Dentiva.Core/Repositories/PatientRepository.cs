using Dentiva.Core.Data;
using Dentiva.Core.Models;
using Dentiva.Core.Services;
using Microsoft.Data.Sqlite;

namespace Dentiva.Core.Repositories;

public sealed class PatientQuery
{
    public string? Search { get; set; }
    public string? Gender { get; set; }
    public int? MinAge { get; set; }
    public int? MaxAge { get; set; }
    public bool IncludeArchived { get; set; }
    public bool OnlyArchived { get; set; }
    public bool OnlyOutstanding { get; set; }
    public DateTime? VisitedFrom { get; set; }
    public DateTime? VisitedTo { get; set; }
    public long? AssignedDentistId { get; set; }
    public string SortColumn { get; set; } = "created_utc";
    public bool SortDescending { get; set; } = true;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public sealed class PatientListItem
{
    public long Id { get; set; }
    public string PatientCode { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Gender { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public int? Age { get; set; }
    public bool IsArchived { get; set; }
    public DateTime? LastVisit { get; set; }
    public DateTime? NextAppointment { get; set; }
    public int VisitCount { get; set; }
    public decimal Outstanding { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int TotalCount { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

/// <summary>
/// Patient persistence. Every query is parameterised and paged so the UI never materialises the
/// whole table; sort columns are mapped through a whitelist rather than concatenated from input.
/// </summary>
public sealed class PatientRepository
{
    private readonly Database _db;
    private readonly SequenceService _sequences;
    private readonly AuditService _audit;

    public PatientRepository(Database db, SequenceService sequences, AuditService audit)
    {
        _db = db;
        _sequences = sequences;
        _audit = audit;
    }

    private static readonly Dictionary<string, string> SortMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["full_name"] = "p.full_name",
        ["patient_code"] = "p.patient_code",
        ["created_utc"] = "p.created_utc",
        ["last_visit"] = "last_visit",
        ["outstanding"] = "outstanding",
        ["phone"] = "p.phone"
    };

    public async Task<PagedResult<PatientListItem>> QueryAsync(PatientQuery query, CancellationToken ct = default)
    {
        var where = new List<string>();
        var parameters = new List<(string, object)>();

        if (query.OnlyArchived)
        {
            where.Add("p.is_archived = 1");
        }
        else if (!query.IncludeArchived)
        {
            where.Add("p.is_archived = 0");
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            where.Add("(p.full_name LIKE $search OR p.patient_code LIKE $search OR p.phone LIKE $search OR p.alternate_phone LIKE $search OR p.email LIKE $search OR p.preferred_name LIKE $search)");
            parameters.Add(("$search", "%" + query.Search.Trim() + "%"));
        }

        if (!string.IsNullOrWhiteSpace(query.Gender))
        {
            where.Add("p.gender = $gender");
            parameters.Add(("$gender", query.Gender));
        }

        if (query.AssignedDentistId is { } dentistId)
        {
            where.Add("p.assigned_dentist_id = $dentist");
            parameters.Add(("$dentist", dentistId));
        }

        if (query.MinAge is { } minAge)
        {
            where.Add("p.date_of_birth IS NOT NULL AND p.date_of_birth <= $maxDob");
            parameters.Add(("$maxDob", DateTime.Today.AddYears(-minAge).ToDbDate()));
        }

        if (query.MaxAge is { } maxAge)
        {
            where.Add("p.date_of_birth IS NOT NULL AND p.date_of_birth >= $minDob");
            parameters.Add(("$minDob", DateTime.Today.AddYears(-(maxAge + 1)).AddDays(1).ToDbDate()));
        }

        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;

        var havingParts = new List<string>();
        if (query.OnlyOutstanding)
        {
            havingParts.Add("outstanding > 0.004");
        }

        if (query.VisitedFrom is { } vf)
        {
            havingParts.Add("last_visit >= $visitFrom");
            parameters.Add(("$visitFrom", vf.ToDbDate()));
        }

        if (query.VisitedTo is { } vt)
        {
            havingParts.Add("last_visit <= $visitTo");
            parameters.Add(("$visitTo", vt.ToDbDate()));
        }

        var havingSql = havingParts.Count > 0 ? "HAVING " + string.Join(" AND ", havingParts) : string.Empty;

        var sortColumn = SortMap.TryGetValue(query.SortColumn, out var mapped) ? mapped : "p.created_utc";
        var direction = query.SortDescending ? "DESC" : "ASC";
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var page = Math.Max(1, query.Page);

        const string projection = """
            SELECT p.id, p.patient_code, p.full_name, p.phone, p.email, p.gender, p.date_of_birth,
                   p.is_archived, p.created_utc,
                   (SELECT MAX(v.visit_date) FROM visits v WHERE v.patient_id = p.id) AS last_visit,
                   (SELECT COUNT(*) FROM visits v WHERE v.patient_id = p.id) AS visit_count,
                   (SELECT MIN(a.appointment_date) FROM appointments a WHERE a.patient_id = p.id
                        AND a.appointment_date >= $today AND a.status IN ('Scheduled','Confirmed','Checked In','Waiting','In Treatment')) AS next_appt,
                   COALESCE((SELECT SUM(i.total - i.paid_amount) FROM invoices i WHERE i.patient_id = p.id AND i.status <> 'Cancelled'), 0) AS outstanding
            FROM patients p
            """;

        var listSql = $"{projection} {whereSql} {havingSql} ORDER BY {sortColumn} {direction}, p.id DESC LIMIT $limit OFFSET $offset";
        var countSql = $"SELECT COUNT(*) FROM ({projection} {whereSql} {havingSql})";

        var items = new List<PatientListItem>();
        var total = 0;

        await Task.Run(() =>
        {
            using (var countCmd = _db.CreateCommand(countSql))
            {
                countCmd.AddValue("$today", DateTime.Today.ToDbDate());
                foreach (var (n, v) in parameters) countCmd.AddValue(n, v);
                total = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);
            }

            using var cmd = _db.CreateCommand(listSql);
            cmd.AddValue("$today", DateTime.Today.ToDbDate());
            foreach (var (n, v) in parameters) cmd.AddValue(n, v);
            cmd.AddValue("$limit", pageSize);
            cmd.AddValue("$offset", (page - 1) * pageSize);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var dob = reader.GetDateOrNull("date_of_birth");
                items.Add(new PatientListItem
                {
                    Id = reader.GetInt64Value("id"),
                    PatientCode = reader.GetStringOrEmpty("patient_code"),
                    FullName = reader.GetStringOrEmpty("full_name"),
                    Phone = reader.GetStringOrNull("phone"),
                    Email = reader.GetStringOrNull("email"),
                    Gender = reader.GetStringOrNull("gender"),
                    DateOfBirth = dob,
                    Age = CalculateAge(dob),
                    IsArchived = reader.GetBoolValue("is_archived"),
                    LastVisit = reader.GetDateOrNull("last_visit"),
                    NextAppointment = reader.GetDateOrNull("next_appt"),
                    VisitCount = reader.GetIntValue("visit_count"),
                    Outstanding = reader.GetDecimalValue("outstanding"),
                    CreatedUtc = reader.GetUtcValue("created_utc")
                });
            }
        }, ct).ConfigureAwait(false);

        return new PagedResult<PatientListItem> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    public static int? CalculateAge(DateTime? dob)
    {
        if (dob is null) return null;
        var today = DateTime.Today;
        var age = today.Year - dob.Value.Year;
        if (dob.Value.Date > today.AddYears(-age)) age--;
        return age < 0 ? null : age;
    }

    public Task<Patient?> GetAsync(long id, CancellationToken ct = default) => Task.Run(() =>
    {
        using var cmd = _db.CreateCommand("SELECT * FROM patients WHERE id = $id");
        cmd.AddValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }, ct);

    public Task<Patient?> GetByCodeAsync(string code, CancellationToken ct = default) => Task.Run(() =>
    {
        using var cmd = _db.CreateCommand("SELECT * FROM patients WHERE patient_code = $c");
        cmd.AddValue("$c", code);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }, ct);

    public Task<List<Patient>> FindDuplicatesAsync(string? phone, string fullName, long excludeId = 0, CancellationToken ct = default) => Task.Run(() =>
    {
        var results = new List<Patient>();
        var normalised = Validation.NormalisePhone(phone);
        using var cmd = _db.CreateCommand("""
            SELECT * FROM patients
            WHERE id <> $id AND (
                ($phone <> '' AND (REPLACE(REPLACE(REPLACE(phone,' ',''),'-',''),'+','') LIKE '%' || $phoneDigits
                 OR REPLACE(REPLACE(REPLACE(alternate_phone,' ',''),'-',''),'+','') LIKE '%' || $phoneDigits))
                OR full_name = $name COLLATE NOCASE)
            LIMIT 25
            """);
        cmd.AddValue("$id", excludeId);
        cmd.AddValue("$phone", normalised);
        cmd.AddValue("$phoneDigits", normalised.TrimStart('+'));
        cmd.AddValue("$name", fullName.Trim());
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(Map(reader));
        return results;
    }, ct);

    public async Task<long> CreateAsync(Patient patient, string? actor, CancellationToken ct = default)
    {
        var nameCheck = Validation.ValidateFullName(patient.FullName);
        if (!nameCheck.IsValid) throw new DentivaValidationException(nameCheck.Message!);
        var phoneCheck = Validation.ValidatePhone(patient.Phone);
        if (!phoneCheck.IsValid) throw new DentivaValidationException(phoneCheck.Message!);
        var emailCheck = Validation.ValidateEmail(patient.Email);
        if (!emailCheck.IsValid) throw new DentivaValidationException(emailCheck.Message!);
        var dobCheck = Validation.ValidateDateOfBirth(patient.DateOfBirth);
        if (!dobCheck.IsValid) throw new DentivaValidationException(dobCheck.Message!);

        if (string.IsNullOrWhiteSpace(patient.PatientCode))
        {
            patient.PatientCode = _sequences.NextPatientCode();
        }

        patient.FullName = patient.FullName.Trim();
        patient.CreatedUtc = DateTime.UtcNow;
        patient.UpdatedUtc = DateTime.UtcNow;
        patient.CreatedBy = actor;
        patient.UpdatedBy = actor;

        var id = await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("""
                INSERT INTO patients (patient_code, full_name, preferred_name, phone, alternate_phone, email,
                    date_of_birth, gender, blood_group, nationality, address, city, district, division, postal_code,
                    emergency_contact_name, emergency_relationship, emergency_phone, allergies, current_medications,
                    medical_conditions, dental_history, medical_history, surgical_history, family_history,
                    pregnancy_status, tobacco_status, clinician_notes, referral_source, assigned_dentist_id,
                    is_archived, created_utc, created_by, updated_utc, updated_by)
                VALUES ($code, $name, $preferred, $phone, $alt, $email, $dob, $gender, $blood, $nationality,
                    $address, $city, $district, $division, $postal, $ecName, $ecRel, $ecPhone, $allergies,
                    $meds, $conditions, $dental, $medical, $surgical, $family, $pregnancy, $tobacco, $notes,
                    $referral, $dentist, $archived, $created, $createdBy, $updated, $updatedBy);
                SELECT last_insert_rowid();
                """);
            cmd.Transaction = tx;
            BindPatient(cmd, patient);
            var newId = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            return Task.FromResult(newId);
        }, ct).ConfigureAwait(false);

        patient.Id = id;
        _audit.Log("patient.created", "Patient", id.ToString(), $"{patient.PatientCode} — {patient.FullName}", actor);
        return id;
    }

    public async Task UpdateAsync(Patient patient, string? actor, CancellationToken ct = default)
    {
        var nameCheck = Validation.ValidateFullName(patient.FullName);
        if (!nameCheck.IsValid) throw new DentivaValidationException(nameCheck.Message!);
        var phoneCheck = Validation.ValidatePhone(patient.Phone);
        if (!phoneCheck.IsValid) throw new DentivaValidationException(phoneCheck.Message!);
        var emailCheck = Validation.ValidateEmail(patient.Email);
        if (!emailCheck.IsValid) throw new DentivaValidationException(emailCheck.Message!);

        patient.UpdatedUtc = DateTime.UtcNow;
        patient.UpdatedBy = actor;

        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("""
                UPDATE patients SET patient_code=$code, full_name=$name, preferred_name=$preferred, phone=$phone,
                    alternate_phone=$alt, email=$email, date_of_birth=$dob, gender=$gender, blood_group=$blood,
                    nationality=$nationality, address=$address, city=$city, district=$district, division=$division,
                    postal_code=$postal, emergency_contact_name=$ecName, emergency_relationship=$ecRel,
                    emergency_phone=$ecPhone, allergies=$allergies, current_medications=$meds,
                    medical_conditions=$conditions, dental_history=$dental, medical_history=$medical,
                    surgical_history=$surgical, family_history=$family, pregnancy_status=$pregnancy,
                    tobacco_status=$tobacco, clinician_notes=$notes, referral_source=$referral,
                    assigned_dentist_id=$dentist, is_archived=$archived, updated_utc=$updated, updated_by=$updatedBy
                WHERE id=$id
                """);
            cmd.Transaction = tx;
            BindPatient(cmd, patient);
            cmd.AddValue("$id", patient.Id);
            cmd.AddValue("$created", patient.CreatedUtc.ToString("O"));
            cmd.AddValue("$createdBy", patient.CreatedBy);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        _audit.Log("patient.updated", "Patient", patient.Id.ToString(), $"{patient.PatientCode} — {patient.FullName}", actor);
    }

    public async Task SetArchivedAsync(long id, bool archived, string? actor, CancellationToken ct = default)
    {
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("UPDATE patients SET is_archived=$a, updated_utc=$u WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddBool("$a", archived);
            cmd.AddValue("$u", DateTime.UtcNow.ToString("O"));
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        _audit.Log(archived ? "patient.archived" : "patient.restored", "Patient", id.ToString(), null, actor);
    }

    /// <summary>
    /// Permanently removes a patient and all dependent clinical rows. Invoices and payments use
    /// RESTRICT, so a patient with financial history cannot be deleted by accident.
    /// </summary>
    public async Task<bool> DeleteAsync(long id, string? actor, CancellationToken ct = default)
    {
        var hasFinancials = await Task.Run(() =>
        {
            using var cmd = _db.CreateCommand("SELECT (SELECT COUNT(*) FROM invoices WHERE patient_id=$id) + (SELECT COUNT(*) FROM payments WHERE patient_id=$id)");
            cmd.AddValue("$id", id);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
        }, ct).ConfigureAwait(false);

        if (hasFinancials)
        {
            throw new DentivaValidationException("This patient has billing history and cannot be deleted. Archive the patient instead to preserve the financial record.");
        }

        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("DELETE FROM patients WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        _audit.Log("patient.deleted", "Patient", id.ToString(), null, actor);
        return true;
    }

    public Task<int> CountAsync(bool includeArchived = false, CancellationToken ct = default) => Task.Run(() =>
    {
        using var cmd = _db.CreateCommand(includeArchived
            ? "SELECT COUNT(*) FROM patients"
            : "SELECT COUNT(*) FROM patients WHERE is_archived = 0");
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }, ct);

    public Task<decimal> GetOutstandingAsync(long patientId, CancellationToken ct = default) => Task.Run(() =>
    {
        using var cmd = _db.CreateCommand("SELECT COALESCE(SUM(total - paid_amount), 0) FROM invoices WHERE patient_id=$id AND status <> 'Cancelled'");
        cmd.AddValue("$id", patientId);
        return Convert.ToDecimal(cmd.ExecuteScalar() ?? 0d);
    }, ct);

    public Task<List<PatientListItem>> QuickSearchAsync(string term, int limit = 20, CancellationToken ct = default) => Task.Run(() =>
    {
        var results = new List<PatientListItem>();
        if (string.IsNullOrWhiteSpace(term)) return results;

        using var cmd = _db.CreateCommand("""
            SELECT id, patient_code, full_name, phone, email, gender, date_of_birth, is_archived, created_utc
            FROM patients
            WHERE full_name LIKE $t OR patient_code LIKE $t OR phone LIKE $t OR email LIKE $t OR preferred_name LIKE $t
            ORDER BY is_archived ASC, full_name COLLATE NOCASE LIMIT $l
            """);
        cmd.AddValue("$t", "%" + term.Trim() + "%");
        cmd.AddValue("$l", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var dob = reader.GetDateOrNull("date_of_birth");
            results.Add(new PatientListItem
            {
                Id = reader.GetInt64Value("id"),
                PatientCode = reader.GetStringOrEmpty("patient_code"),
                FullName = reader.GetStringOrEmpty("full_name"),
                Phone = reader.GetStringOrNull("phone"),
                Email = reader.GetStringOrNull("email"),
                Gender = reader.GetStringOrNull("gender"),
                DateOfBirth = dob,
                Age = CalculateAge(dob),
                IsArchived = reader.GetBoolValue("is_archived"),
                CreatedUtc = reader.GetUtcValue("created_utc")
            });
        }

        return results;
    }, ct);

    private static void BindPatient(SqliteCommand cmd, Patient p)
    {
        cmd.AddValue("$code", p.PatientCode);
        cmd.AddValue("$name", p.FullName);
        cmd.AddValue("$preferred", p.PreferredName);
        cmd.AddValue("$phone", p.Phone);
        cmd.AddValue("$alt", p.AlternatePhone);
        cmd.AddValue("$email", p.Email);
        cmd.AddDate("$dob", p.DateOfBirth);
        cmd.AddValue("$gender", p.Gender);
        cmd.AddValue("$blood", p.BloodGroup);
        cmd.AddValue("$nationality", p.Nationality);
        cmd.AddValue("$address", p.Address);
        cmd.AddValue("$city", p.City);
        cmd.AddValue("$district", p.District);
        cmd.AddValue("$division", p.Division);
        cmd.AddValue("$postal", p.PostalCode);
        cmd.AddValue("$ecName", p.EmergencyContactName);
        cmd.AddValue("$ecRel", p.EmergencyRelationship);
        cmd.AddValue("$ecPhone", p.EmergencyPhone);
        cmd.AddValue("$allergies", p.Allergies);
        cmd.AddValue("$meds", p.CurrentMedications);
        cmd.AddValue("$conditions", p.MedicalConditions);
        cmd.AddValue("$dental", p.DentalHistory);
        cmd.AddValue("$medical", p.MedicalHistory);
        cmd.AddValue("$surgical", p.SurgicalHistory);
        cmd.AddValue("$family", p.FamilyHistory);
        cmd.AddValue("$pregnancy", p.PregnancyStatus);
        cmd.AddValue("$tobacco", p.TobaccoStatus);
        cmd.AddValue("$notes", p.ClinicianNotes);
        cmd.AddValue("$referral", p.ReferralSource);
        cmd.AddValue("$dentist", p.AssignedDentistId);
        cmd.AddBool("$archived", p.IsArchived);
        if (!cmd.Parameters.Contains("$created")) cmd.AddValue("$created", p.CreatedUtc.ToString("O"));
        if (!cmd.Parameters.Contains("$createdBy")) cmd.AddValue("$createdBy", p.CreatedBy);
        cmd.AddValue("$updated", p.UpdatedUtc.ToString("O"));
        cmd.AddValue("$updatedBy", p.UpdatedBy);
    }

    internal static Patient Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64Value("id"),
        PatientCode = reader.GetStringOrEmpty("patient_code"),
        FullName = reader.GetStringOrEmpty("full_name"),
        PreferredName = reader.GetStringOrNull("preferred_name"),
        Phone = reader.GetStringOrNull("phone"),
        AlternatePhone = reader.GetStringOrNull("alternate_phone"),
        Email = reader.GetStringOrNull("email"),
        DateOfBirth = reader.GetDateOrNull("date_of_birth"),
        Gender = reader.GetStringOrNull("gender"),
        BloodGroup = reader.GetStringOrNull("blood_group"),
        Nationality = reader.GetStringOrNull("nationality"),
        Address = reader.GetStringOrNull("address"),
        City = reader.GetStringOrNull("city"),
        District = reader.GetStringOrNull("district"),
        Division = reader.GetStringOrNull("division"),
        PostalCode = reader.GetStringOrNull("postal_code"),
        EmergencyContactName = reader.GetStringOrNull("emergency_contact_name"),
        EmergencyRelationship = reader.GetStringOrNull("emergency_relationship"),
        EmergencyPhone = reader.GetStringOrNull("emergency_phone"),
        Allergies = reader.GetStringOrNull("allergies"),
        CurrentMedications = reader.GetStringOrNull("current_medications"),
        MedicalConditions = reader.GetStringOrNull("medical_conditions"),
        DentalHistory = reader.GetStringOrNull("dental_history"),
        MedicalHistory = reader.GetStringOrNull("medical_history"),
        SurgicalHistory = reader.GetStringOrNull("surgical_history"),
        FamilyHistory = reader.GetStringOrNull("family_history"),
        PregnancyStatus = reader.GetStringOrNull("pregnancy_status"),
        TobaccoStatus = reader.GetStringOrNull("tobacco_status"),
        ClinicianNotes = reader.GetStringOrNull("clinician_notes"),
        ReferralSource = reader.GetStringOrNull("referral_source"),
        AssignedDentistId = reader.GetInt64OrNull("assigned_dentist_id"),
        IsArchived = reader.GetBoolValue("is_archived"),
        CreatedUtc = reader.GetUtcValue("created_utc"),
        CreatedBy = reader.GetStringOrNull("created_by"),
        UpdatedUtc = reader.GetUtcValue("updated_utc"),
        UpdatedBy = reader.GetStringOrNull("updated_by")
    };
}

public sealed class DentivaValidationException : Exception
{
    public DentivaValidationException(string message) : base(message) { }
}
