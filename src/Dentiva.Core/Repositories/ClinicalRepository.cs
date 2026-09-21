using Dentiva.Core.Data;
using Dentiva.Core.Models;
using Dentiva.Core.Services;

namespace Dentiva.Core.Repositories;

/// <summary>
/// Visits, treatments, tooth chart, prescriptions and referrals. Clinical history is append-only in
/// spirit: records are never silently replaced and every mutation is audited.
/// </summary>
public sealed class ClinicalRepository
{
    private readonly Database _db;
    private readonly SequenceService _sequences;
    private readonly AuditService _audit;

    public ClinicalRepository(Database db, SequenceService sequences, AuditService audit)
    {
        _db = db;
        _sequences = sequences;
        _audit = audit;
    }

    // ---------- Visits ----------
    public Task<List<Visit>> GetVisitsAsync(long patientId, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<Visit>();
        using var cmd = _db.CreateCommand("SELECT * FROM visits WHERE patient_id=$p ORDER BY visit_date DESC, id DESC");
        cmd.AddValue("$p", patientId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapVisit(r));
        return list;
    }, ct);

    public async Task<long> SaveVisitAsync(Visit visit, string? actor, CancellationToken ct = default)
    {
        var notes = Validation.ValidateNotes(visit.ClinicalNotes);
        if (!notes.IsValid) throw new DentivaValidationException(notes.Message!);

        visit.UpdatedUtc = DateTime.UtcNow;
        var isNew = visit.Id == 0;

        var savedId = await _db.WriteAsync(tx =>
        {
            if (isNew)
            {
                visit.CreatedUtc = DateTime.UtcNow;
                visit.CreatedBy = actor;
                using var cmd = _db.CreateCommand("""
                    INSERT INTO visits(patient_id, visit_date, reason, complaint, clinical_notes, diagnosis, examination,
                        treatment_performed, medication_summary, dentist_id, dentist_name, follow_up_date, additional_notes,
                        created_utc, created_by, updated_utc)
                    VALUES($p,$d,$reason,$complaint,$notes,$diag,$exam,$treat,$med,$did,$dname,$follow,$extra,$c,$cb,$u);
                    SELECT last_insert_rowid();
                    """);
                cmd.Transaction = tx;
                BindVisit(cmd, visit);
                visit.Id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            else
            {
                using var cmd = _db.CreateCommand("""
                    UPDATE visits SET visit_date=$d, reason=$reason, complaint=$complaint, clinical_notes=$notes,
                        diagnosis=$diag, examination=$exam, treatment_performed=$treat, medication_summary=$med,
                        dentist_id=$did, dentist_name=$dname, follow_up_date=$follow, additional_notes=$extra, updated_utc=$u
                    WHERE id=$id
                    """);
                cmd.Transaction = tx;
                BindVisit(cmd, visit);
                cmd.AddValue("$id", visit.Id);
                cmd.ExecuteNonQuery();
            }

            return Task.FromResult(visit.Id);
        }, ct).ConfigureAwait(false);

        _audit.Log(isNew ? "visit.created" : "visit.updated", "Visit", savedId.ToString(), $"Patient #{visit.PatientId}", actor);
        return savedId;
    }

    public async Task DeleteVisitAsync(long id, string? actor, CancellationToken ct = default)
    {
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("DELETE FROM visits WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        _audit.Log("visit.deleted", "Visit", id.ToString(), null, actor);
    }

    private static void BindVisit(Microsoft.Data.Sqlite.SqliteCommand cmd, Visit v)
    {
        cmd.AddValue("$p", v.PatientId);
        cmd.AddDate("$d", v.VisitDate);
        cmd.AddValue("$reason", v.Reason);
        cmd.AddValue("$complaint", v.Complaint);
        cmd.AddValue("$notes", v.ClinicalNotes);
        cmd.AddValue("$diag", v.Diagnosis);
        cmd.AddValue("$exam", v.Examination);
        cmd.AddValue("$treat", v.TreatmentPerformed);
        cmd.AddValue("$med", v.MedicationSummary);
        cmd.AddValue("$did", v.DentistId);
        cmd.AddValue("$dname", v.DentistName);
        cmd.AddDate("$follow", v.FollowUpDate);
        cmd.AddValue("$extra", v.AdditionalNotes);
        cmd.AddValue("$c", v.CreatedUtc.ToString("O"));
        cmd.AddValue("$cb", v.CreatedBy);
        cmd.AddValue("$u", v.UpdatedUtc.ToString("O"));
    }

    private static Visit MapVisit(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64Value("id"),
        PatientId = r.GetInt64Value("patient_id"),
        VisitDate = r.GetDateValue("visit_date"),
        Reason = r.GetStringOrNull("reason"),
        Complaint = r.GetStringOrNull("complaint"),
        ClinicalNotes = r.GetStringOrNull("clinical_notes"),
        Diagnosis = r.GetStringOrNull("diagnosis"),
        Examination = r.GetStringOrNull("examination"),
        TreatmentPerformed = r.GetStringOrNull("treatment_performed"),
        MedicationSummary = r.GetStringOrNull("medication_summary"),
        DentistId = r.GetInt64OrNull("dentist_id"),
        DentistName = r.GetStringOrNull("dentist_name"),
        FollowUpDate = r.GetDateOrNull("follow_up_date"),
        AdditionalNotes = r.GetStringOrNull("additional_notes"),
        CreatedUtc = r.GetUtcValue("created_utc"),
        CreatedBy = r.GetStringOrNull("created_by"),
        UpdatedUtc = r.GetUtcValue("updated_utc")
    };

    // ---------- Treatments ----------
    public Task<List<Treatment>> GetTreatmentsAsync(long patientId, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<Treatment>();
        using var cmd = _db.CreateCommand("SELECT * FROM treatments WHERE patient_id=$p ORDER BY treatment_date DESC, id DESC");
        cmd.AddValue("$p", patientId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapTreatment(r));
        return list;
    }, ct);

    public Task<List<Treatment>> GetTreatmentsInRangeAsync(DateTime from, DateTime to, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<Treatment>();
        using var cmd = _db.CreateCommand("SELECT * FROM treatments WHERE treatment_date BETWEEN $f AND $t ORDER BY treatment_date DESC, id DESC");
        cmd.AddDate("$f", from);
        cmd.AddDate("$t", to);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapTreatment(r));
        return list;
    }, ct);

    public async Task<long> SaveTreatmentAsync(Treatment t, string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(t.ProcedureName))
            throw new DentivaValidationException("A procedure name is required for the treatment record.");
        var costCheck = Validation.ValidateAmount(t.Cost);
        if (!costCheck.IsValid) throw new DentivaValidationException(costCheck.Message!);
        if (t.Discount > t.Cost) throw new DentivaValidationException("The discount cannot exceed the treatment cost.");

        t.UpdatedUtc = DateTime.UtcNow;
        var isNew = t.Id == 0;

        var id = await _db.WriteAsync(tx =>
        {
            if (isNew)
            {
                t.CreatedUtc = DateTime.UtcNow;
                t.CreatedBy = actor;
                using var cmd = _db.CreateCommand("""
                    INSERT INTO treatments(patient_id, visit_id, treatment_date, category_id, category_name, procedure_name,
                        teeth, surfaces, diagnosis, notes, dentist_name, cost, discount, status, follow_up_date, invoice_id,
                        created_utc, created_by, updated_utc)
                    VALUES($p,$v,$d,$cid,$cname,$proc,$teeth,$surf,$diag,$notes,$dentist,$cost,$disc,$status,$follow,$inv,$c,$cb,$u);
                    SELECT last_insert_rowid();
                    """);
                cmd.Transaction = tx;
                BindTreatment(cmd, t);
                t.Id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            else
            {
                using var cmd = _db.CreateCommand("""
                    UPDATE treatments SET visit_id=$v, treatment_date=$d, category_id=$cid, category_name=$cname,
                        procedure_name=$proc, teeth=$teeth, surfaces=$surf, diagnosis=$diag, notes=$notes,
                        dentist_name=$dentist, cost=$cost, discount=$disc, status=$status, follow_up_date=$follow,
                        invoice_id=$inv, updated_utc=$u
                    WHERE id=$id
                    """);
                cmd.Transaction = tx;
                BindTreatment(cmd, t);
                cmd.AddValue("$id", t.Id);
                cmd.ExecuteNonQuery();
            }

            return Task.FromResult(t.Id);
        }, ct).ConfigureAwait(false);

        _audit.Log(isNew ? "treatment.created" : "treatment.updated", "Treatment", id.ToString(), t.ProcedureName, actor);
        return id;
    }

    public async Task DeleteTreatmentAsync(long id, string? actor, CancellationToken ct = default)
    {
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("DELETE FROM treatments WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        _audit.Log("treatment.deleted", "Treatment", id.ToString(), null, actor);
    }

    private static void BindTreatment(Microsoft.Data.Sqlite.SqliteCommand cmd, Treatment t)
    {
        cmd.AddValue("$p", t.PatientId);
        cmd.AddValue("$v", t.VisitId);
        cmd.AddDate("$d", t.TreatmentDate);
        cmd.AddValue("$cid", t.CategoryId);
        cmd.AddValue("$cname", t.CategoryName);
        cmd.AddValue("$proc", t.ProcedureName);
        cmd.AddValue("$teeth", t.Teeth);
        cmd.AddValue("$surf", t.Surfaces);
        cmd.AddValue("$diag", t.Diagnosis);
        cmd.AddValue("$notes", t.Notes);
        cmd.AddValue("$dentist", t.DentistName);
        cmd.AddMoney("$cost", t.Cost);
        cmd.AddMoney("$disc", t.Discount);
        cmd.AddValue("$status", t.Status);
        cmd.AddDate("$follow", t.FollowUpDate);
        cmd.AddValue("$inv", t.InvoiceId);
        cmd.AddValue("$c", t.CreatedUtc.ToString("O"));
        cmd.AddValue("$cb", t.CreatedBy);
        cmd.AddValue("$u", t.UpdatedUtc.ToString("O"));
    }

    private static Treatment MapTreatment(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64Value("id"),
        PatientId = r.GetInt64Value("patient_id"),
        VisitId = r.GetInt64OrNull("visit_id"),
        TreatmentDate = r.GetDateValue("treatment_date"),
        CategoryId = r.GetInt64OrNull("category_id"),
        CategoryName = r.GetStringOrNull("category_name"),
        ProcedureName = r.GetStringOrEmpty("procedure_name"),
        Teeth = r.GetStringOrNull("teeth"),
        Surfaces = r.GetStringOrNull("surfaces"),
        Diagnosis = r.GetStringOrNull("diagnosis"),
        Notes = r.GetStringOrNull("notes"),
        DentistName = r.GetStringOrNull("dentist_name"),
        Cost = r.GetDecimalValue("cost"),
        Discount = r.GetDecimalValue("discount"),
        Status = r.GetStringOrEmpty("status"),
        FollowUpDate = r.GetDateOrNull("follow_up_date"),
        InvoiceId = r.GetInt64OrNull("invoice_id"),
        CreatedUtc = r.GetUtcValue("created_utc"),
        CreatedBy = r.GetStringOrNull("created_by"),
        UpdatedUtc = r.GetUtcValue("updated_utc")
    };

    // ---------- Tooth chart ----------
    public Task<List<ToothRecord>> GetToothRecordsAsync(long patientId, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<ToothRecord>();
        using var cmd = _db.CreateCommand("SELECT * FROM tooth_records WHERE patient_id=$p");
        cmd.AddValue("$p", patientId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ToothRecord
            {
                Id = r.GetInt64Value("id"),
                PatientId = r.GetInt64Value("patient_id"),
                ToothNumber = r.GetStringOrEmpty("tooth_number"),
                Dentition = r.GetStringOrEmpty("dentition"),
                Surface = r.GetStringOrNull("surface"),
                Condition = r.GetStringOrEmpty("condition"),
                PlannedTreatment = r.GetStringOrNull("planned_treatment"),
                Notes = r.GetStringOrNull("notes"),
                RecordedDate = r.GetDateValue("recorded_date"),
                UpdatedUtc = r.GetUtcValue("updated_utc")
            });
        }

        return list;
    }, ct);

    public async Task SaveToothRecordAsync(ToothRecord record, string? actor, CancellationToken ct = default)
    {
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("""
                INSERT INTO tooth_records(patient_id, tooth_number, dentition, surface, condition, planned_treatment, notes, recorded_date, updated_utc)
                VALUES($p,$t,$d,$s,$c,$plan,$n,$rd,$u)
                ON CONFLICT(patient_id, tooth_number) DO UPDATE SET
                    dentition=excluded.dentition, surface=excluded.surface, condition=excluded.condition,
                    planned_treatment=excluded.planned_treatment, notes=excluded.notes,
                    recorded_date=excluded.recorded_date, updated_utc=excluded.updated_utc
                """);
            cmd.Transaction = tx;
            cmd.AddValue("$p", record.PatientId);
            cmd.AddValue("$t", record.ToothNumber);
            cmd.AddValue("$d", record.Dentition);
            cmd.AddValue("$s", record.Surface);
            cmd.AddValue("$c", record.Condition);
            cmd.AddValue("$plan", record.PlannedTreatment);
            cmd.AddValue("$n", record.Notes);
            cmd.AddDate("$rd", record.RecordedDate);
            cmd.AddValue("$u", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        _audit.Log("tooth.updated", "ToothRecord", $"{record.PatientId}/{record.ToothNumber}", record.Condition, actor);
    }

    // ---------- Prescriptions ----------
    public Task<List<Prescription>> GetPrescriptionsAsync(long patientId, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<Prescription>();
        using (var cmd = _db.CreateCommand("SELECT * FROM prescriptions WHERE patient_id=$p ORDER BY issued_date DESC, id DESC"))
        {
            cmd.AddValue("$p", patientId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(MapPrescription(r));
        }

        foreach (var p in list) p.Items = LoadPrescriptionItems(p.Id);
        return list;
    }, ct);

    public Task<Prescription?> GetPrescriptionAsync(long id, CancellationToken ct = default) => Task.Run(() =>
    {
        Prescription? result = null;
        using (var cmd = _db.CreateCommand("SELECT * FROM prescriptions WHERE id=$id"))
        {
            cmd.AddValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (r.Read()) result = MapPrescription(r);
        }

        if (result is not null) result.Items = LoadPrescriptionItems(result.Id);
        return result;
    }, ct);

    private List<PrescriptionItem> LoadPrescriptionItems(long prescriptionId)
    {
        var items = new List<PrescriptionItem>();
        using var cmd = _db.CreateCommand("SELECT * FROM prescription_items WHERE prescription_id=$p ORDER BY sort_order, id");
        cmd.AddValue("$p", prescriptionId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            items.Add(new PrescriptionItem
            {
                Id = r.GetInt64Value("id"),
                PrescriptionId = r.GetInt64Value("prescription_id"),
                MedicineName = r.GetStringOrEmpty("medicine_name"),
                GenericName = r.GetStringOrNull("generic_name"),
                Strength = r.GetStringOrNull("strength"),
                Dosage = r.GetStringOrNull("dosage"),
                Frequency = r.GetStringOrNull("frequency"),
                Duration = r.GetStringOrNull("duration"),
                Route = r.GetStringOrNull("route"),
                MealRelation = r.GetStringOrNull("meal_relation"),
                Quantity = r.GetStringOrNull("quantity"),
                Instructions = r.GetStringOrNull("instructions"),
                SortOrder = r.GetIntValue("sort_order")
            });
        }

        return items;
    }

    public async Task<long> SavePrescriptionAsync(Prescription p, string? actor, CancellationToken ct = default)
    {
        if (p.Items.Count == 0)
            throw new DentivaValidationException("Add at least one medicine before saving the prescription.");
        if (p.Items.Any(i => string.IsNullOrWhiteSpace(i.MedicineName)))
            throw new DentivaValidationException("Every prescription line must have a medicine name.");

        var isNew = p.Id == 0;
        if (isNew && string.IsNullOrWhiteSpace(p.PrescriptionNumber))
        {
            p.PrescriptionNumber = _sequences.NextPrescriptionNumber();
        }

        p.UpdatedUtc = DateTime.UtcNow;

        var id = await _db.WriteAsync(tx =>
        {
            if (isNew)
            {
                p.CreatedUtc = DateTime.UtcNow;
                p.CreatedBy = actor;
                using var cmd = _db.CreateCommand("""
                    INSERT INTO prescriptions(patient_id, visit_id, prescription_no, issued_date, dentist_name, diagnosis,
                        advice, follow_up_date, notes, created_utc, created_by, updated_utc)
                    VALUES($p,$v,$no,$d,$dentist,$diag,$advice,$follow,$notes,$c,$cb,$u);
                    SELECT last_insert_rowid();
                    """);
                cmd.Transaction = tx;
                BindPrescription(cmd, p);
                p.Id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            else
            {
                using var cmd = _db.CreateCommand("""
                    UPDATE prescriptions SET visit_id=$v, issued_date=$d, dentist_name=$dentist, diagnosis=$diag,
                        advice=$advice, follow_up_date=$follow, notes=$notes, updated_utc=$u WHERE id=$id
                    """);
                cmd.Transaction = tx;
                BindPrescription(cmd, p);
                cmd.AddValue("$id", p.Id);
                cmd.ExecuteNonQuery();

                using var del = _db.CreateCommand("DELETE FROM prescription_items WHERE prescription_id=$id");
                del.Transaction = tx;
                del.AddValue("$id", p.Id);
                del.ExecuteNonQuery();
            }

            var order = 0;
            foreach (var item in p.Items)
            {
                using var ins = _db.CreateCommand("""
                    INSERT INTO prescription_items(prescription_id, medicine_name, generic_name, strength, dosage,
                        frequency, duration, route, meal_relation, quantity, instructions, sort_order)
                    VALUES($pid,$m,$g,$s,$dose,$f,$dur,$r,$meal,$q,$i,$o)
                    """);
                ins.Transaction = tx;
                ins.AddValue("$pid", p.Id);
                ins.AddValue("$m", item.MedicineName);
                ins.AddValue("$g", item.GenericName);
                ins.AddValue("$s", item.Strength);
                ins.AddValue("$dose", item.Dosage);
                ins.AddValue("$f", item.Frequency);
                ins.AddValue("$dur", item.Duration);
                ins.AddValue("$r", item.Route);
                ins.AddValue("$meal", item.MealRelation);
                ins.AddValue("$q", item.Quantity);
                ins.AddValue("$i", item.Instructions);
                ins.AddValue("$o", order++);
                ins.ExecuteNonQuery();
            }

            return Task.FromResult(p.Id);
        }, ct).ConfigureAwait(false);

        _audit.Log(isNew ? "prescription.created" : "prescription.updated", "Prescription", id.ToString(), p.PrescriptionNumber, actor);
        return id;
    }

    private static void BindPrescription(Microsoft.Data.Sqlite.SqliteCommand cmd, Prescription p)
    {
        cmd.AddValue("$p", p.PatientId);
        cmd.AddValue("$v", p.VisitId);
        cmd.AddValue("$no", p.PrescriptionNumber);
        cmd.AddDate("$d", p.IssuedDate);
        cmd.AddValue("$dentist", p.DentistName);
        cmd.AddValue("$diag", p.Diagnosis);
        cmd.AddValue("$advice", p.Advice);
        cmd.AddDate("$follow", p.FollowUpDate);
        cmd.AddValue("$notes", p.Notes);
        cmd.AddValue("$c", p.CreatedUtc.ToString("O"));
        cmd.AddValue("$cb", p.CreatedBy);
        cmd.AddValue("$u", p.UpdatedUtc.ToString("O"));
    }

    private static Prescription MapPrescription(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64Value("id"),
        PatientId = r.GetInt64Value("patient_id"),
        VisitId = r.GetInt64OrNull("visit_id"),
        PrescriptionNumber = r.GetStringOrEmpty("prescription_no"),
        IssuedDate = r.GetDateValue("issued_date"),
        DentistName = r.GetStringOrNull("dentist_name"),
        Diagnosis = r.GetStringOrNull("diagnosis"),
        Advice = r.GetStringOrNull("advice"),
        FollowUpDate = r.GetDateOrNull("follow_up_date"),
        Notes = r.GetStringOrNull("notes"),
        CreatedUtc = r.GetUtcValue("created_utc"),
        CreatedBy = r.GetStringOrNull("created_by"),
        UpdatedUtc = r.GetUtcValue("updated_utc")
    };

    public async Task DeletePrescriptionAsync(long id, string? actor, CancellationToken ct = default)
    {
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("DELETE FROM prescriptions WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        _audit.Log("prescription.deleted", "Prescription", id.ToString(), null, actor);
    }

    // ---------- Referrals ----------
    public Task<List<Referral>> GetReferralsAsync(long patientId, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<Referral>();
        using var cmd = _db.CreateCommand("SELECT * FROM referrals WHERE patient_id=$p ORDER BY referral_date DESC, id DESC");
        cmd.AddValue("$p", patientId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Referral
            {
                Id = r.GetInt64Value("id"),
                PatientId = r.GetInt64Value("patient_id"),
                ReferralDate = r.GetDateValue("referral_date"),
                ProfessionalName = r.GetStringOrEmpty("professional_name"),
                Specialty = r.GetStringOrNull("specialty"),
                Organization = r.GetStringOrNull("organization"),
                Reason = r.GetStringOrNull("reason"),
                ClinicalSummary = r.GetStringOrNull("clinical_summary"),
                Instructions = r.GetStringOrNull("instructions"),
                Outcome = r.GetStringOrNull("outcome"),
                Status = r.GetStringOrEmpty("status"),
                Notes = r.GetStringOrNull("notes"),
                CreatedUtc = r.GetUtcValue("created_utc"),
                UpdatedUtc = r.GetUtcValue("updated_utc")
            });
        }

        return list;
    }, ct);

    public async Task<long> SaveReferralAsync(Referral referral, string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(referral.ProfessionalName))
            throw new DentivaValidationException("Enter the name of the professional the patient is referred to.");

        referral.UpdatedUtc = DateTime.UtcNow;
        var isNew = referral.Id == 0;

        var id = await _db.WriteAsync(tx =>
        {
            if (isNew)
            {
                referral.CreatedUtc = DateTime.UtcNow;
                using var cmd = _db.CreateCommand("""
                    INSERT INTO referrals(patient_id, referral_date, professional_name, specialty, organization, reason,
                        clinical_summary, instructions, outcome, status, notes, created_utc, updated_utc)
                    VALUES($p,$d,$name,$spec,$org,$reason,$sum,$inst,$out,$status,$notes,$c,$u);
                    SELECT last_insert_rowid();
                    """);
                cmd.Transaction = tx;
                BindReferral(cmd, referral);
                referral.Id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            else
            {
                using var cmd = _db.CreateCommand("""
                    UPDATE referrals SET referral_date=$d, professional_name=$name, specialty=$spec, organization=$org,
                        reason=$reason, clinical_summary=$sum, instructions=$inst, outcome=$out, status=$status,
                        notes=$notes, updated_utc=$u WHERE id=$id
                    """);
                cmd.Transaction = tx;
                BindReferral(cmd, referral);
                cmd.AddValue("$id", referral.Id);
                cmd.ExecuteNonQuery();
            }

            return Task.FromResult(referral.Id);
        }, ct).ConfigureAwait(false);

        _audit.Log(isNew ? "referral.created" : "referral.updated", "Referral", id.ToString(), referral.ProfessionalName, actor);
        return id;
    }

    private static void BindReferral(Microsoft.Data.Sqlite.SqliteCommand cmd, Referral x)
    {
        cmd.AddValue("$p", x.PatientId);
        cmd.AddDate("$d", x.ReferralDate);
        cmd.AddValue("$name", x.ProfessionalName);
        cmd.AddValue("$spec", x.Specialty);
        cmd.AddValue("$org", x.Organization);
        cmd.AddValue("$reason", x.Reason);
        cmd.AddValue("$sum", x.ClinicalSummary);
        cmd.AddValue("$inst", x.Instructions);
        cmd.AddValue("$out", x.Outcome);
        cmd.AddValue("$status", x.Status);
        cmd.AddValue("$notes", x.Notes);
        cmd.AddValue("$c", x.CreatedUtc.ToString("O"));
        cmd.AddValue("$u", x.UpdatedUtc.ToString("O"));
    }

    public async Task DeleteReferralAsync(long id, string? actor, CancellationToken ct = default)
    {
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("DELETE FROM referrals WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        _audit.Log("referral.deleted", "Referral", id.ToString(), null, actor);
    }

    // ---------- Categories ----------
    public Task<List<TreatmentCategory>> GetTreatmentCategoriesAsync(bool activeOnly = true, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<TreatmentCategory>();
        using var cmd = _db.CreateCommand(activeOnly
            ? "SELECT * FROM treatment_categories WHERE is_active=1 ORDER BY sort_order, name"
            : "SELECT * FROM treatment_categories ORDER BY sort_order, name");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TreatmentCategory
            {
                Id = r.GetInt64Value("id"),
                Name = r.GetStringOrEmpty("name"),
                NameBn = r.GetStringOrNull("name_bn"),
                DefaultFee = r.GetDecimalValue("default_fee"),
                IsActive = r.GetBoolValue("is_active"),
                SortOrder = r.GetIntValue("sort_order")
            });
        }

        return list;
    }, ct);

    public async Task SaveTreatmentCategoryAsync(TreatmentCategory category, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(category.Name))
            throw new DentivaValidationException("The treatment category name is required.");

        await _db.WriteAsync(tx =>
        {
            if (category.Id == 0)
            {
                using var cmd = _db.CreateCommand("""
                    INSERT INTO treatment_categories(name, name_bn, default_fee, is_active, sort_order, created_utc)
                    VALUES($n,$bn,$fee,$a,$o,$c)
                    """);
                cmd.Transaction = tx;
                cmd.AddValue("$n", category.Name.Trim());
                cmd.AddValue("$bn", category.NameBn);
                cmd.AddMoney("$fee", category.DefaultFee);
                cmd.AddBool("$a", category.IsActive);
                cmd.AddValue("$o", category.SortOrder);
                cmd.AddValue("$c", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }
            else
            {
                using var cmd = _db.CreateCommand("UPDATE treatment_categories SET name=$n, name_bn=$bn, default_fee=$fee, is_active=$a, sort_order=$o WHERE id=$id");
                cmd.Transaction = tx;
                cmd.AddValue("$n", category.Name.Trim());
                cmd.AddValue("$bn", category.NameBn);
                cmd.AddMoney("$fee", category.DefaultFee);
                cmd.AddBool("$a", category.IsActive);
                cmd.AddValue("$o", category.SortOrder);
                cmd.AddValue("$id", category.Id);
                cmd.ExecuteNonQuery();
            }

            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    public async Task DeleteTreatmentCategoryAsync(long id, CancellationToken ct = default)
    {
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("UPDATE treatment_categories SET is_active=0 WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Patients whose follow-up date falls on the supplied day.</summary>
    public Task<List<(long PatientId, string PatientCode, string PatientName, DateTime FollowUp, string? Reason)>> GetFollowUpsAsync(DateTime day, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<(long, string, string, DateTime, string?)>();
        using var cmd = _db.CreateCommand("""
            SELECT p.id, p.patient_code, p.full_name, v.follow_up_date, v.reason
            FROM visits v JOIN patients p ON p.id = v.patient_id
            WHERE v.follow_up_date = $d AND p.is_archived = 0
            UNION
            SELECT p.id, p.patient_code, p.full_name, t.follow_up_date, t.procedure_name
            FROM treatments t JOIN patients p ON p.id = t.patient_id
            WHERE t.follow_up_date = $d AND p.is_archived = 0
            ORDER BY 3
            """);
        cmd.AddDate("$d", day);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDateValue("follow_up_date"), r.IsDBNull(4) ? null : r.GetString(4)));
        }

        return list;
    }, ct);
}
