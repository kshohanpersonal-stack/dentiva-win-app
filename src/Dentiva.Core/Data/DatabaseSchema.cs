namespace Dentiva.Core.Data;

/// <summary>
/// Versioned migration set. Migrations are applied in order inside a transaction and recorded in
/// schema_migrations so upgrades never destroy existing clinical data.
/// </summary>
public static class DatabaseSchema
{
    public const int CurrentVersion = 1;

    public static IReadOnlyList<Migration> Migrations { get; } = new List<Migration>
    {
        new(1, "Initial clinical schema", InitialSchema)
    };

    private const string InitialSchema = """
CREATE TABLE IF NOT EXISTS settings (
    key             TEXT PRIMARY KEY,
    value           TEXT NOT NULL,
    updated_utc     TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS users (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    username            TEXT NOT NULL UNIQUE COLLATE NOCASE,
    display_name        TEXT NOT NULL,
    role                TEXT NOT NULL,
    password_hash       TEXT NOT NULL,
    password_salt       TEXT NOT NULL,
    password_iterations INTEGER NOT NULL,
    permissions_json    TEXT NOT NULL DEFAULT '[]',
    is_active           INTEGER NOT NULL DEFAULT 1,
    last_login_utc      TEXT,
    failed_attempts     INTEGER NOT NULL DEFAULT 0,
    locked_until_utc    TEXT,
    created_utc         TEXT NOT NULL,
    updated_utc         TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_users_role ON users(role);

CREATE TABLE IF NOT EXISTS patients (
    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    patient_code            TEXT NOT NULL UNIQUE COLLATE NOCASE,
    full_name               TEXT NOT NULL,
    preferred_name          TEXT,
    phone                   TEXT,
    alternate_phone         TEXT,
    email                   TEXT,
    date_of_birth           TEXT,
    gender                  TEXT,
    blood_group             TEXT,
    nationality             TEXT,
    address                 TEXT,
    city                    TEXT,
    district                TEXT,
    division                TEXT,
    postal_code             TEXT,
    emergency_contact_name  TEXT,
    emergency_relationship  TEXT,
    emergency_phone         TEXT,
    allergies               TEXT,
    current_medications     TEXT,
    medical_conditions      TEXT,
    dental_history          TEXT,
    medical_history         TEXT,
    surgical_history        TEXT,
    family_history          TEXT,
    pregnancy_status        TEXT,
    tobacco_status          TEXT,
    clinician_notes         TEXT,
    referral_source         TEXT,
    assigned_dentist_id     INTEGER REFERENCES users(id) ON DELETE SET NULL,
    is_archived             INTEGER NOT NULL DEFAULT 0,
    created_utc             TEXT NOT NULL,
    created_by              TEXT,
    updated_utc             TEXT NOT NULL,
    updated_by              TEXT
);
CREATE INDEX IF NOT EXISTS ix_patients_name ON patients(full_name);
CREATE INDEX IF NOT EXISTS ix_patients_phone ON patients(phone);
CREATE INDEX IF NOT EXISTS ix_patients_email ON patients(email);
CREATE INDEX IF NOT EXISTS ix_patients_archived ON patients(is_archived);
CREATE INDEX IF NOT EXISTS ix_patients_created ON patients(created_utc);

CREATE TABLE IF NOT EXISTS visits (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    patient_id          INTEGER NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    visit_date          TEXT NOT NULL,
    reason              TEXT,
    complaint           TEXT,
    clinical_notes      TEXT,
    diagnosis           TEXT,
    examination         TEXT,
    treatment_performed TEXT,
    medication_summary  TEXT,
    dentist_id          INTEGER REFERENCES users(id) ON DELETE SET NULL,
    dentist_name        TEXT,
    follow_up_date      TEXT,
    additional_notes    TEXT,
    created_utc         TEXT NOT NULL,
    created_by          TEXT,
    updated_utc         TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_visits_patient ON visits(patient_id, visit_date DESC);
CREATE INDEX IF NOT EXISTS ix_visits_followup ON visits(follow_up_date);

CREATE TABLE IF NOT EXISTS treatment_categories (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    name         TEXT NOT NULL UNIQUE COLLATE NOCASE,
    name_bn      TEXT,
    default_fee  REAL NOT NULL DEFAULT 0,
    is_active    INTEGER NOT NULL DEFAULT 1,
    sort_order   INTEGER NOT NULL DEFAULT 0,
    created_utc  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS treatments (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    patient_id      INTEGER NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    visit_id        INTEGER REFERENCES visits(id) ON DELETE SET NULL,
    treatment_date  TEXT NOT NULL,
    category_id     INTEGER REFERENCES treatment_categories(id) ON DELETE SET NULL,
    category_name   TEXT,
    procedure_name  TEXT NOT NULL,
    teeth           TEXT,
    surfaces        TEXT,
    diagnosis       TEXT,
    notes           TEXT,
    dentist_name    TEXT,
    cost            REAL NOT NULL DEFAULT 0,
    discount        REAL NOT NULL DEFAULT 0,
    status          TEXT NOT NULL DEFAULT 'Completed',
    follow_up_date  TEXT,
    invoice_id      INTEGER,
    created_utc     TEXT NOT NULL,
    created_by      TEXT,
    updated_utc     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_treatments_patient ON treatments(patient_id, treatment_date DESC);
CREATE INDEX IF NOT EXISTS ix_treatments_category ON treatments(category_id);

CREATE TABLE IF NOT EXISTS tooth_records (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    patient_id      INTEGER NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    tooth_number    TEXT NOT NULL,
    dentition       TEXT NOT NULL DEFAULT 'Permanent',
    surface         TEXT,
    condition       TEXT NOT NULL DEFAULT 'Healthy',
    planned_treatment TEXT,
    notes           TEXT,
    recorded_date   TEXT NOT NULL,
    updated_utc     TEXT NOT NULL,
    UNIQUE(patient_id, tooth_number)
);
CREATE INDEX IF NOT EXISTS ix_tooth_patient ON tooth_records(patient_id);

CREATE TABLE IF NOT EXISTS prescriptions (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    patient_id      INTEGER NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    visit_id        INTEGER REFERENCES visits(id) ON DELETE SET NULL,
    prescription_no TEXT NOT NULL UNIQUE COLLATE NOCASE,
    issued_date     TEXT NOT NULL,
    dentist_name    TEXT,
    diagnosis       TEXT,
    advice          TEXT,
    follow_up_date  TEXT,
    notes           TEXT,
    created_utc     TEXT NOT NULL,
    created_by      TEXT,
    updated_utc     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_prescriptions_patient ON prescriptions(patient_id, issued_date DESC);

CREATE TABLE IF NOT EXISTS prescription_items (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    prescription_id INTEGER NOT NULL REFERENCES prescriptions(id) ON DELETE CASCADE,
    medicine_name   TEXT NOT NULL,
    generic_name    TEXT,
    strength        TEXT,
    dosage          TEXT,
    frequency       TEXT,
    duration        TEXT,
    route           TEXT,
    meal_relation   TEXT,
    quantity        TEXT,
    instructions    TEXT,
    sort_order      INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_prescription_items_parent ON prescription_items(prescription_id);

CREATE TABLE IF NOT EXISTS referrals (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    patient_id          INTEGER NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    referral_date       TEXT NOT NULL,
    professional_name   TEXT NOT NULL,
    specialty           TEXT,
    organization        TEXT,
    reason              TEXT,
    clinical_summary    TEXT,
    instructions        TEXT,
    outcome             TEXT,
    status              TEXT NOT NULL DEFAULT 'Sent',
    notes               TEXT,
    created_utc         TEXT NOT NULL,
    updated_utc         TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_referrals_patient ON referrals(patient_id, referral_date DESC);

CREATE TABLE IF NOT EXISTS appointments (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    patient_id          INTEGER NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
    appointment_date    TEXT NOT NULL,
    start_time          TEXT NOT NULL,
    end_time            TEXT,
    duration_minutes    INTEGER NOT NULL DEFAULT 30,
    serial_number       INTEGER,
    appointment_type    TEXT,
    reason              TEXT,
    status              TEXT NOT NULL DEFAULT 'Scheduled',
    priority            TEXT NOT NULL DEFAULT 'Normal',
    dentist_id          INTEGER REFERENCES users(id) ON DELETE SET NULL,
    dentist_name        TEXT,
    notes               TEXT,
    checked_in_utc      TEXT,
    completed_utc       TEXT,
    created_utc         TEXT NOT NULL,
    created_by          TEXT,
    updated_utc         TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_appointments_date ON appointments(appointment_date, start_time);
CREATE INDEX IF NOT EXISTS ix_appointments_patient ON appointments(patient_id, appointment_date DESC);
CREATE INDEX IF NOT EXISTS ix_appointments_status ON appointments(status);

CREATE TABLE IF NOT EXISTS invoices (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    invoice_number  TEXT NOT NULL UNIQUE COLLATE NOCASE,
    patient_id      INTEGER NOT NULL REFERENCES patients(id) ON DELETE RESTRICT,
    invoice_date    TEXT NOT NULL,
    due_date        TEXT,
    subtotal        REAL NOT NULL DEFAULT 0,
    discount        REAL NOT NULL DEFAULT 0,
    discount_type   TEXT NOT NULL DEFAULT 'Amount',
    tax_rate        REAL NOT NULL DEFAULT 0,
    tax_amount      REAL NOT NULL DEFAULT 0,
    total           REAL NOT NULL DEFAULT 0,
    paid_amount     REAL NOT NULL DEFAULT 0,
    status          TEXT NOT NULL DEFAULT 'Unpaid',
    notes           TEXT,
    created_utc     TEXT NOT NULL,
    created_by      TEXT,
    updated_utc     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_invoices_patient ON invoices(patient_id, invoice_date DESC);
CREATE INDEX IF NOT EXISTS ix_invoices_date ON invoices(invoice_date);
CREATE INDEX IF NOT EXISTS ix_invoices_status ON invoices(status);

CREATE TABLE IF NOT EXISTS invoice_items (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    invoice_id  INTEGER NOT NULL REFERENCES invoices(id) ON DELETE CASCADE,
    description TEXT NOT NULL,
    item_type   TEXT,
    quantity    REAL NOT NULL DEFAULT 1,
    unit_price  REAL NOT NULL DEFAULT 0,
    discount    REAL NOT NULL DEFAULT 0,
    line_total  REAL NOT NULL DEFAULT 0,
    treatment_id INTEGER REFERENCES treatments(id) ON DELETE SET NULL,
    sort_order  INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_invoice_items_parent ON invoice_items(invoice_id);

CREATE TABLE IF NOT EXISTS payments (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    receipt_number  TEXT NOT NULL UNIQUE COLLATE NOCASE,
    invoice_id      INTEGER REFERENCES invoices(id) ON DELETE SET NULL,
    patient_id      INTEGER NOT NULL REFERENCES patients(id) ON DELETE RESTRICT,
    amount          REAL NOT NULL,
    payment_date    TEXT NOT NULL,
    method          TEXT NOT NULL,
    reference       TEXT,
    notes           TEXT,
    received_by     TEXT,
    is_refund       INTEGER NOT NULL DEFAULT 0,
    created_utc     TEXT NOT NULL,
    updated_utc     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_payments_invoice ON payments(invoice_id);
CREATE INDEX IF NOT EXISTS ix_payments_patient ON payments(patient_id, payment_date DESC);
CREATE INDEX IF NOT EXISTS ix_payments_date ON payments(payment_date);

CREATE TABLE IF NOT EXISTS staff (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    staff_code          TEXT NOT NULL UNIQUE COLLATE NOCASE,
    full_name           TEXT NOT NULL,
    role                TEXT NOT NULL,
    department          TEXT,
    phone               TEXT,
    email               TEXT,
    address             TEXT,
    joining_date        TEXT,
    salary              REAL NOT NULL DEFAULT 0,
    salary_type         TEXT NOT NULL DEFAULT 'Monthly',
    payment_schedule    TEXT,
    status              TEXT NOT NULL DEFAULT 'Active',
    emergency_contact   TEXT,
    notes               TEXT,
    created_utc         TEXT NOT NULL,
    updated_utc         TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_staff_status ON staff(status);

CREATE TABLE IF NOT EXISTS expense_categories (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT NOT NULL UNIQUE COLLATE NOCASE,
    name_bn     TEXT,
    is_active   INTEGER NOT NULL DEFAULT 1,
    sort_order  INTEGER NOT NULL DEFAULT 0,
    created_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS expenses (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    expense_date    TEXT NOT NULL,
    category_id     INTEGER REFERENCES expense_categories(id) ON DELETE SET NULL,
    category_name   TEXT,
    description     TEXT NOT NULL,
    amount          REAL NOT NULL,
    payment_method  TEXT,
    reference       TEXT,
    vendor          TEXT,
    notes           TEXT,
    staff_id        INTEGER REFERENCES staff(id) ON DELETE SET NULL,
    created_utc     TEXT NOT NULL,
    created_by      TEXT,
    updated_utc     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_expenses_date ON expenses(expense_date);
CREATE INDEX IF NOT EXISTS ix_expenses_category ON expenses(category_id);

CREATE TABLE IF NOT EXISTS salary_payments (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    staff_id        INTEGER NOT NULL REFERENCES staff(id) ON DELETE CASCADE,
    payment_date    TEXT NOT NULL,
    period_label    TEXT,
    amount          REAL NOT NULL,
    method          TEXT,
    reference       TEXT,
    notes           TEXT,
    created_utc     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_salary_staff ON salary_payments(staff_id, payment_date DESC);

CREATE TABLE IF NOT EXISTS attachments (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    attachment_uid  TEXT NOT NULL UNIQUE,
    patient_id      INTEGER REFERENCES patients(id) ON DELETE CASCADE,
    referral_id     INTEGER REFERENCES referrals(id) ON DELETE SET NULL,
    expense_id      INTEGER REFERENCES expenses(id) ON DELETE SET NULL,
    staff_id        INTEGER REFERENCES staff(id) ON DELETE SET NULL,
    category        TEXT NOT NULL DEFAULT 'Documents',
    original_name   TEXT NOT NULL,
    stored_path     TEXT NOT NULL,
    content_type    TEXT,
    size_bytes      INTEGER NOT NULL DEFAULT 0,
    checksum_sha256 TEXT,
    document_date   TEXT,
    description     TEXT,
    source          TEXT,
    notes           TEXT,
    created_utc     TEXT NOT NULL,
    created_by      TEXT
);
CREATE INDEX IF NOT EXISTS ix_attachments_patient ON attachments(patient_id);
CREATE INDEX IF NOT EXISTS ix_attachments_category ON attachments(category);

CREATE TABLE IF NOT EXISTS audit_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp_utc TEXT NOT NULL,
    username    TEXT,
    action      TEXT NOT NULL,
    entity      TEXT,
    entity_id   TEXT,
    summary     TEXT,
    metadata    TEXT
);
CREATE INDEX IF NOT EXISTS ix_audit_time ON audit_log(timestamp_utc DESC);
CREATE INDEX IF NOT EXISTS ix_audit_entity ON audit_log(entity, entity_id);

CREATE TABLE IF NOT EXISTS notifications (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    created_utc TEXT NOT NULL,
    severity    TEXT NOT NULL DEFAULT 'Information',
    title       TEXT NOT NULL,
    message     TEXT,
    category    TEXT,
    is_read     INTEGER NOT NULL DEFAULT 0,
    link_entity TEXT,
    link_id     TEXT
);
CREATE INDEX IF NOT EXISTS ix_notifications_read ON notifications(is_read, created_utc DESC);

CREATE TABLE IF NOT EXISTS payment_methods (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT NOT NULL UNIQUE COLLATE NOCASE,
    is_active   INTEGER NOT NULL DEFAULT 1,
    sort_order  INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS appointment_types (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    name             TEXT NOT NULL UNIQUE COLLATE NOCASE,
    duration_minutes INTEGER NOT NULL DEFAULT 30,
    is_active        INTEGER NOT NULL DEFAULT 1,
    sort_order       INTEGER NOT NULL DEFAULT 0
);
""";
}

public sealed record Migration(int Version, string Description, string Sql);
