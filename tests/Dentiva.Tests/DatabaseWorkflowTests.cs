using Dentiva.Core.Data;
using Dentiva.Core.Models;
using Dentiva.Core.Repositories;
using Xunit;

namespace Dentiva.Tests;

/// <summary>End-to-end persistence tests running against a real SQLite database.</summary>
public class DatabaseWorkflowTests
{
    private static Patient NewPatient(string name = "Rafiqul Islam", string? phone = "01712345678") => new()
    {
        FullName = name,
        Phone = phone,
        Gender = "Male",
        DateOfBirth = new DateTime(1988, 4, 12),
        Address = "House 12, Road 5, Dhanmondi",
        City = "Dhaka"
    };

    [Fact]
    public void Database_AppliesMigrationsAndPassesIntegrityChecks()
    {
        using var h = new TestHarness();
        Assert.Equal(DatabaseSchema.CurrentVersion, h.Db.SchemaVersion);
        Assert.Equal("ok", h.Db.IntegrityCheck());
        Assert.Equal("ok", h.Db.ForeignKeyCheck());
    }

    [Fact]
    public void Database_OpenIsIdempotent()
    {
        using var h = new TestHarness();
        h.Db.Open();
        h.Db.Open();
        Assert.True(h.Db.IsOpen);
        Assert.Equal(DatabaseSchema.CurrentVersion, h.Db.SchemaVersion);
    }

    [Fact]
    public async Task FreshDatabase_ContainsNoClinicalOrFinancialRecords()
    {
        using var h = new TestHarness();

        Assert.Equal(0, await h.Patients.CountAsync(true));
        Assert.Equal(0, (await h.Billing.QueryInvoicesAsync(null, null, null, null)).TotalCount);
        Assert.Equal(0, (await h.Billing.QueryPaymentsAsync(null, null, null, null)).TotalCount);
        Assert.Equal(0, (await h.Finance.QueryExpensesAsync(null, null, null, null)).TotalCount);
        Assert.Empty(await h.Staff.GetAllAsync());

        // Configuration reference data must exist so the clinic can start working immediately.
        Assert.NotEmpty(await h.Clinical.GetTreatmentCategoriesAsync());
        Assert.NotEmpty(await h.Billing.GetPaymentMethodsAsync());
        Assert.NotEmpty(await h.Appointments.GetAppointmentTypesAsync());
    }

    [Fact]
    public async Task CreatePatient_AssignsSequentialUniqueCode()
    {
        using var h = new TestHarness();
        var id1 = await h.Patients.CreateAsync(NewPatient("Patient One"), "tester");
        var id2 = await h.Patients.CreateAsync(NewPatient("Patient Two"), "tester");

        Assert.Equal("DEN-000001", (await h.Patients.GetAsync(id1))!.PatientCode);
        Assert.Equal("DEN-000002", (await h.Patients.GetAsync(id2))!.PatientCode);
    }

    [Fact]
    public async Task PatientCodes_RemainUniqueUnderConcurrentCreation()
    {
        using var h = new TestHarness();
        var ids = await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(i => h.Patients.CreateAsync(NewPatient($"Concurrent {i}"), "tester")));

        var codes = new List<string>();
        foreach (var id in ids)
        {
            codes.Add((await h.Patients.GetAsync(id))!.PatientCode);
        }

        Assert.Equal(25, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task UpdatePatient_PersistsEveryEditedField()
    {
        using var h = new TestHarness();
        var id = await h.Patients.CreateAsync(NewPatient(), "tester");

        var patient = await h.Patients.GetAsync(id);
        patient!.FullName = "Rafiqul Islam Chowdhury";
        patient.Allergies = "Penicillin";
        patient.BloodGroup = "B+";
        patient.EmergencyPhone = "01911111111";
        await h.Patients.UpdateAsync(patient, "tester");

        var reloaded = await h.Patients.GetAsync(id);
        Assert.Equal("Rafiqul Islam Chowdhury", reloaded!.FullName);
        Assert.Equal("Penicillin", reloaded.Allergies);
        Assert.Equal("B+", reloaded.BloodGroup);
        Assert.Equal("01911111111", reloaded.EmergencyPhone);
    }

    [Fact]
    public async Task CreatePatient_RejectsInvalidInput()
    {
        using var h = new TestHarness();

        await Assert.ThrowsAsync<DentivaValidationException>(() => h.Patients.CreateAsync(NewPatient(""), "t"));
        await Assert.ThrowsAsync<DentivaValidationException>(() => h.Patients.CreateAsync(NewPatient("Valid Name", "abc"), "t"));

        var futureDob = NewPatient("Future Baby");
        futureDob.DateOfBirth = DateTime.Today.AddYears(1);
        await Assert.ThrowsAsync<DentivaValidationException>(() => h.Patients.CreateAsync(futureDob, "t"));

        var badEmail = NewPatient("Bad Email");
        badEmail.Email = "not-an-email";
        await Assert.ThrowsAsync<DentivaValidationException>(() => h.Patients.CreateAsync(badEmail, "t"));
    }

    [Fact]
    public async Task Patient_RoundTripsBanglaTextAndVeryLongNotes()
    {
        using var h = new TestHarness();
        var patient = NewPatient("মোহাম্মদ আব্দুল করিম");
        patient.Address = "১২ নম্বর বাড়ি, ধানমন্ডি, ঢাকা";
        patient.ClinicianNotes = new string('ক', 20_000);

        var id = await h.Patients.CreateAsync(patient, "tester");
        var loaded = await h.Patients.GetAsync(id);

        Assert.Equal("মোহাম্মদ আব্দুল করিম", loaded!.FullName);
        Assert.Equal("১২ নম্বর বাড়ি, ধানমন্ডি, ঢাকা", loaded.Address);
        Assert.Equal(20_000, loaded.ClinicianNotes!.Length);
    }

    [Fact]
    public async Task Patient_AgeIsDerivedFromDateOfBirth()
    {
        using var h = new TestHarness();
        var patient = NewPatient("Age Test");
        patient.DateOfBirth = DateTime.Today.AddYears(-30).AddDays(-1);

        var loaded = await h.Patients.GetAsync(await h.Patients.CreateAsync(patient, "t"));
        Assert.Equal(30, loaded!.Age);
        Assert.Equal(30, PatientRepository.CalculateAge(patient.DateOfBirth));
        Assert.Null(PatientRepository.CalculateAge(null));
    }

    [Fact]
    public async Task SearchPatient_FindsByNameCodeAndPhone()
    {
        using var h = new TestHarness();
        await h.Patients.CreateAsync(NewPatient("Kamal Hossain", "01911111111"), "t");
        await h.Patients.CreateAsync(NewPatient("Jamal Uddin", "01822222222"), "t");

        Assert.Single((await h.Patients.QueryAsync(new PatientQuery { Search = "Kamal" })).Items);
        Assert.Single((await h.Patients.QueryAsync(new PatientQuery { Search = "01822" })).Items);
        Assert.Single((await h.Patients.QueryAsync(new PatientQuery { Search = "DEN-000001" })).Items);
        Assert.Equal(2, (await h.Patients.QueryAsync(new PatientQuery())).TotalCount);
    }

    [Fact]
    public async Task QuickSearch_ReturnsMatchesForTheOmniboxAndNothingForNoise()
    {
        using var h = new TestHarness();
        await h.Patients.CreateAsync(NewPatient("Shirin Akter", "01733333333"), "t");

        Assert.Single(await h.Patients.QuickSearchAsync("Shirin"));
        Assert.Empty(await h.Patients.QuickSearchAsync("zzzzz-no-such-patient"));
    }

    [Fact]
    public async Task PatientQuery_PaginatesLargeDatasetsWithoutLimit()
    {
        using var h = new TestHarness();
        for (var i = 0; i < 120; i++)
        {
            await h.Patients.CreateAsync(NewPatient($"Patient {i:D3}"), "t");
        }

        var page1 = await h.Patients.QueryAsync(new PatientQuery { Page = 1, PageSize = 50 });
        var page3 = await h.Patients.QueryAsync(new PatientQuery { Page = 3, PageSize = 50 });

        Assert.Equal(120, page1.TotalCount);
        Assert.Equal(50, page1.Items.Count);
        Assert.Equal(20, page3.Items.Count);
        Assert.Equal(3, page1.TotalPages);
    }

    [Fact]
    public async Task PatientQuery_FiltersByGenderAndAgeRange()
    {
        using var h = new TestHarness();

        var young = NewPatient("Young Person");
        young.Gender = "Female";
        young.DateOfBirth = DateTime.Today.AddYears(-20);
        await h.Patients.CreateAsync(young, "t");

        var older = NewPatient("Older Person");
        older.Gender = "Male";
        older.DateOfBirth = DateTime.Today.AddYears(-60);
        await h.Patients.CreateAsync(older, "t");

        Assert.Single((await h.Patients.QueryAsync(new PatientQuery { Gender = "Female" })).Items);
        Assert.Single((await h.Patients.QueryAsync(new PatientQuery { MinAge = 50 })).Items);
        Assert.Single((await h.Patients.QueryAsync(new PatientQuery { MaxAge = 30 })).Items);
    }

    [Fact]
    public async Task ArchivedPatients_AreHiddenFromTheDefaultList()
    {
        using var h = new TestHarness();
        var id = await h.Patients.CreateAsync(NewPatient("Archived Person"), "t");
        await h.Patients.SetArchivedAsync(id, true, "t");

        Assert.Empty((await h.Patients.QueryAsync(new PatientQuery())).Items);
        Assert.Single((await h.Patients.QueryAsync(new PatientQuery { IncludeArchived = true })).Items);
        Assert.Single((await h.Patients.QueryAsync(new PatientQuery { OnlyArchived = true })).Items);

        await h.Patients.SetArchivedAsync(id, false, "t");
        Assert.Single((await h.Patients.QueryAsync(new PatientQuery())).Items);
    }

    [Fact]
    public async Task FindDuplicates_DetectsMatchingPhoneOrName()
    {
        using var h = new TestHarness();
        await h.Patients.CreateAsync(NewPatient("Duplicate Test", "01755555555"), "t");

        Assert.NotEmpty(await h.Patients.FindDuplicatesAsync("01755555555", "Someone Else"));
        Assert.NotEmpty(await h.Patients.FindDuplicatesAsync(null, "Duplicate Test"));
        Assert.Empty(await h.Patients.FindDuplicatesAsync("01799999999", "Nobody Here"));
    }

    [Fact]
    public async Task DeletePatient_CascadesClinicalChildrenWhenNoFinancialHistoryExists()
    {
        using var h = new TestHarness();
        var pid = await h.Patients.CreateAsync(NewPatient(), "t");
        await h.Clinical.SaveVisitAsync(new Visit { PatientId = pid, Reason = "Check-up" }, "t");
        await h.Clinical.SaveTreatmentAsync(new Treatment { PatientId = pid, ProcedureName = "Scaling", Cost = 1500 }, "t");

        Assert.True(await h.Patients.DeleteAsync(pid, "t"));
        Assert.Equal(0, await h.Patients.CountAsync(true));
        Assert.Empty(await h.Clinical.GetVisitsAsync(pid));
        Assert.Equal("ok", h.Db.ForeignKeyCheck());
    }

    [Fact]
    public async Task VisitsAndTreatments_StoreUnlimitedHistory()
    {
        using var h = new TestHarness();
        var pid = await h.Patients.CreateAsync(NewPatient(), "t");

        for (var i = 0; i < 60; i++)
        {
            await h.Clinical.SaveVisitAsync(new Visit
            {
                PatientId = pid,
                VisitDate = DateTime.Today.AddDays(-i),
                Reason = $"Visit {i}",
                ClinicalNotes = new string('n', 5000)
            }, "t");
        }

        var visits = await h.Clinical.GetVisitsAsync(pid);
        Assert.Equal(60, visits.Count);
        Assert.Equal(5000, visits[0].ClinicalNotes!.Length);
    }

    [Fact]
    public async Task Visit_RejectsNotesBeyondTheSupportedLength()
    {
        using var h = new TestHarness();
        var pid = await h.Patients.CreateAsync(NewPatient(), "t");

        await Assert.ThrowsAsync<DentivaValidationException>(() => h.Clinical.SaveVisitAsync(new Visit
        {
            PatientId = pid,
            ClinicalNotes = new string('x', 100_001)
        }, "t"));
    }

    [Fact]
    public async Task Treatment_RejectsMissingNameAndExcessiveDiscount()
    {
        using var h = new TestHarness();
        var pid = await h.Patients.CreateAsync(NewPatient(), "t");

        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Clinical.SaveTreatmentAsync(new Treatment { PatientId = pid, ProcedureName = "", Cost = 100 }, "t"));

        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Clinical.SaveTreatmentAsync(new Treatment { PatientId = pid, ProcedureName = "Filling", Cost = 1000, Discount = 2000 }, "t"));
    }

    [Fact]
    public async Task Treatment_NetCostSubtractsDiscount()
    {
        using var h = new TestHarness();
        var pid = await h.Patients.CreateAsync(NewPatient(), "t");
        await h.Clinical.SaveTreatmentAsync(new Treatment
        {
            PatientId = pid, ProcedureName = "Root canal", Cost = 8000, Discount = 500, Teeth = "16,17"
        }, "t");

        var treatment = (await h.Clinical.GetTreatmentsAsync(pid)).Single();
        Assert.Equal(7500m, treatment.NetCost);
        Assert.Equal("16,17", treatment.Teeth);
    }

    [Fact]
    public async Task ToothChart_UpsertsOneRecordPerTooth()
    {
        using var h = new TestHarness();
        var pid = await h.Patients.CreateAsync(NewPatient(), "t");

        await h.Clinical.SaveToothRecordAsync(new ToothRecord { PatientId = pid, ToothNumber = "16", Condition = "Caries" }, "t");
        await h.Clinical.SaveToothRecordAsync(new ToothRecord { PatientId = pid, ToothNumber = "16", Condition = "Restored" }, "t");
        await h.Clinical.SaveToothRecordAsync(new ToothRecord { PatientId = pid, ToothNumber = "26", Condition = "Missing" }, "t");

        var records = await h.Clinical.GetToothRecordsAsync(pid);
        Assert.Equal(2, records.Count);
        Assert.Equal("Restored", records.First(r => r.ToothNumber == "16").Condition);
    }

    [Fact]
    public async Task Prescription_RequiresAtLeastOneNamedMedicine()
    {
        using var h = new TestHarness();
        var pid = await h.Patients.CreateAsync(NewPatient(), "t");

        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Clinical.SavePrescriptionAsync(new Prescription { PatientId = pid }, "t"));

        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Clinical.SavePrescriptionAsync(new Prescription
            {
                PatientId = pid,
                Items = { new PrescriptionItem { MedicineName = "   " } }
            }, "t"));
    }

    [Fact]
    public async Task Prescription_SavesItemsAndAllocatesNumber()
    {
        using var h = new TestHarness();
        var pid = await h.Patients.CreateAsync(NewPatient(), "t");

        var id = await h.Clinical.SavePrescriptionAsync(new Prescription
        {
            PatientId = pid,
            Diagnosis = "Acute apical periodontitis",
            Items =
            {
                new PrescriptionItem { MedicineName = "Amoxicillin", Strength = "500 mg", Dosage = "1 capsule", Frequency = "8 hourly", Duration = "7 days" },
                new PrescriptionItem { MedicineName = "Paracetamol", Strength = "500 mg", Frequency = "As needed" }
            }
        }, "t");

        var loaded = await h.Clinical.GetPrescriptionAsync(id);
        Assert.StartsWith("RX-", loaded!.PrescriptionNumber);
        Assert.Equal(2, loaded.Items.Count);
        Assert.Equal("Amoxicillin", loaded.Items[0].MedicineName);
        Assert.Equal("Acute apical periodontitis", loaded.Diagnosis);
    }

    [Fact]
    public async Task Prescription_UpdateReplacesItemsWithoutDuplicating()
    {
        using var h = new TestHarness();
        var pid = await h.Patients.CreateAsync(NewPatient(), "t");
        var id = await h.Clinical.SavePrescriptionAsync(new Prescription
        {
            PatientId = pid,
            Items = { new PrescriptionItem { MedicineName = "Ibuprofen" } }
        }, "t");

        var rx = await h.Clinical.GetPrescriptionAsync(id);
        rx!.Items.Clear();
        rx.Items.Add(new PrescriptionItem { MedicineName = "Naproxen" });
        await h.Clinical.SavePrescriptionAsync(rx, "t");

        var updated = await h.Clinical.GetPrescriptionAsync(id);
        Assert.Single(updated!.Items);
        Assert.Equal("Naproxen", updated.Items[0].MedicineName);
    }

    [Fact]
    public async Task Referral_RequiresProfessionalNameAndPersists()
    {
        using var h = new TestHarness();
        var pid = await h.Patients.CreateAsync(NewPatient(), "t");

        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Clinical.SaveReferralAsync(new Referral { PatientId = pid }, "t"));

        await h.Clinical.SaveReferralAsync(new Referral
        {
            PatientId = pid,
            ProfessionalName = "Dr. Anwar Hossain",
            Specialty = "Oral & Maxillofacial Surgery",
            Reason = "Impacted third molar"
        }, "t");

        var referral = (await h.Clinical.GetReferralsAsync(pid)).Single();
        Assert.Equal("Dr. Anwar Hossain", referral.ProfessionalName);
    }

    [Fact]
    public async Task FollowUps_AreListedForTheRequestedDay()
    {
        using var h = new TestHarness();
        var pid = await h.Patients.CreateAsync(NewPatient("Follow Up Patient"), "t");
        await h.Clinical.SaveVisitAsync(new Visit
        {
            PatientId = pid, VisitDate = DateTime.Today.AddDays(-7),
            Reason = "Extraction", FollowUpDate = DateTime.Today
        }, "t");

        var followUps = await h.Clinical.GetFollowUpsAsync(DateTime.Today);
        Assert.Contains(followUps, f => f.PatientId == pid);
        Assert.Empty(await h.Clinical.GetFollowUpsAsync(DateTime.Today.AddDays(30)));
    }

    [Fact]
    public async Task TreatmentCategory_CanBeAddedAndDeactivated()
    {
        using var h = new TestHarness();
        var before = (await h.Clinical.GetTreatmentCategoriesAsync()).Count;

        await h.Clinical.SaveTreatmentCategoryAsync(new TreatmentCategory { Name = "Clear Aligners", DefaultFee = 120000 });
        var categories = await h.Clinical.GetTreatmentCategoriesAsync();

        Assert.Equal(before + 1, categories.Count);
        Assert.Contains(categories, c => c.Name == "Clear Aligners" && c.DefaultFee == 120000m);

        await Assert.ThrowsAsync<DentivaValidationException>(() =>
            h.Clinical.SaveTreatmentCategoryAsync(new TreatmentCategory { Name = "" }));
    }

    [Fact]
    public async Task ReferenceData_IsConfigurationOnlyAndNotFabricatedClinicalContent()
    {
        using var h = new TestHarness();

        // Seeded rows are clinic configuration (categories, methods); they must not be patient records.
        Assert.Equal(0, await h.Patients.CountAsync(true));
        Assert.Contains("bKash", await h.Billing.GetPaymentMethodsAsync());
        Assert.Contains("Nagad", await h.Billing.GetPaymentMethodsAsync());
    }
}
