using Dentiva.Core.Data;
using Dentiva.Core.Models;
using Dentiva.Core.Services;

namespace Dentiva.Core.Repositories;

/// <summary>
/// Invoices and payments. Invoice header, line items and the resulting payment are always written in
/// a single transaction so a failure can never leave a half-created financial record.
/// </summary>
public sealed class BillingRepository
{
    private readonly Database _db;
    private readonly SequenceService _sequences;
    private readonly AuditService _audit;
    private readonly SettingsService _settings;

    public BillingRepository(Database db, SequenceService sequences, AuditService audit, SettingsService settings)
    {
        _db = db;
        _sequences = sequences;
        _audit = audit;
        _settings = settings;
    }

    public Task<PagedResult<Invoice>> QueryInvoicesAsync(string? search, string? status, DateTime? from, DateTime? to,
        long? patientId = null, int page = 1, int pageSize = 50, CancellationToken ct = default) => Task.Run(() =>
    {
        var where = new List<string>();
        var ps = new List<(string, object)>();

        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(i.invoice_number LIKE $s OR p.full_name LIKE $s OR p.patient_code LIKE $s OR p.phone LIKE $s)");
            ps.Add(("$s", "%" + search.Trim() + "%"));
        }

        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "All", StringComparison.OrdinalIgnoreCase))
        {
            where.Add("i.status = $st");
            ps.Add(("$st", status));
        }

        if (from is { } f) { where.Add("i.invoice_date >= $f"); ps.Add(("$f", f.ToDbDate())); }
        if (to is { } t) { where.Add("i.invoice_date <= $t"); ps.Add(("$t", t.ToDbDate())); }
        if (patientId is { } pid) { where.Add("i.patient_id = $pid"); ps.Add(("$pid", pid)); }

        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;
        pageSize = Math.Clamp(pageSize, 1, 500);
        page = Math.Max(1, page);

        int total;
        using (var countCmd = _db.CreateCommand($"SELECT COUNT(*) FROM invoices i JOIN patients p ON p.id=i.patient_id {whereSql}"))
        {
            foreach (var (n, v) in ps) countCmd.AddValue(n, v);
            total = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);
        }

        var items = new List<Invoice>();
        using var cmd = _db.CreateCommand($"""
            SELECT i.*, p.full_name AS patient_name, p.patient_code
            FROM invoices i JOIN patients p ON p.id = i.patient_id
            {whereSql}
            ORDER BY i.invoice_date DESC, i.id DESC LIMIT $l OFFSET $o
            """);
        foreach (var (n, v) in ps) cmd.AddValue(n, v);
        cmd.AddValue("$l", pageSize);
        cmd.AddValue("$o", (page - 1) * pageSize);
        using var r = cmd.ExecuteReader();
        while (r.Read()) items.Add(MapInvoice(r));

        return new PagedResult<Invoice> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }, ct);

    public Task<Invoice?> GetInvoiceAsync(long id, CancellationToken ct = default) => Task.Run(() =>
    {
        Invoice? invoice = null;
        using (var cmd = _db.CreateCommand("""
            SELECT i.*, p.full_name AS patient_name, p.patient_code
            FROM invoices i JOIN patients p ON p.id=i.patient_id WHERE i.id=$id
            """))
        {
            cmd.AddValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (r.Read()) invoice = MapInvoice(r);
        }

        if (invoice is null) return null;

        using (var cmd = _db.CreateCommand("SELECT * FROM invoice_items WHERE invoice_id=$id ORDER BY sort_order, id"))
        {
            cmd.AddValue("$id", id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                invoice.Items.Add(new InvoiceItem
                {
                    Id = r.GetInt64Value("id"),
                    InvoiceId = r.GetInt64Value("invoice_id"),
                    Description = r.GetStringOrEmpty("description"),
                    ItemType = r.GetStringOrNull("item_type"),
                    Quantity = r.GetDecimalValue("quantity"),
                    UnitPrice = r.GetDecimalValue("unit_price"),
                    Discount = r.GetDecimalValue("discount"),
                    LineTotal = r.GetDecimalValue("line_total"),
                    TreatmentId = r.GetInt64OrNull("treatment_id"),
                    SortOrder = r.GetIntValue("sort_order")
                });
            }
        }

        return invoice;
    }, ct);

    /// <summary>
    /// Creates or updates an invoice together with its line items inside one transaction.
    /// Totals are always recomputed server-side from the items; the caller cannot inject a wrong total.
    /// </summary>
    public async Task<long> SaveInvoiceAsync(Invoice invoice, string? actor, CancellationToken ct = default)
    {
        if (invoice.PatientId <= 0)
            throw new DentivaValidationException("Select a patient before saving the invoice.");
        if (invoice.Items.Count == 0)
            throw new DentivaValidationException("An invoice must contain at least one line item.");
        if (invoice.Items.Any(i => string.IsNullOrWhiteSpace(i.Description)))
            throw new DentivaValidationException("Every invoice line requires a description.");

        foreach (var item in invoice.Items)
        {
            var qty = Validation.ValidateAmount(item.Quantity, allowZero: false, max: 100_000m);
            if (!qty.IsValid) throw new DentivaValidationException($"'{item.Description}': {qty.Message}");
            var price = Validation.ValidateAmount(item.UnitPrice);
            if (!price.IsValid) throw new DentivaValidationException($"'{item.Description}': {price.Message}");
            if (item.Discount > item.Quantity * item.UnitPrice)
                throw new DentivaValidationException($"The discount on '{item.Description}' exceeds its line value.");
        }

        var totals = BillingCalculator.Compute(invoice.Items, invoice.Discount, invoice.DiscountType, invoice.TaxRate, invoice.PaidAmount);
        invoice.Subtotal = totals.Subtotal;
        invoice.TaxAmount = totals.TaxAmount;
        invoice.Total = totals.Total;
        invoice.Status = invoice.Status == "Cancelled" ? "Cancelled" : totals.Status;
        invoice.UpdatedUtc = DateTime.UtcNow;

        var isNew = invoice.Id == 0;
        if (isNew && string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
        {
            invoice.InvoiceNumber = _sequences.NextInvoiceNumber();
        }

        var id = await _db.WriteAsync(tx =>
        {
            if (isNew)
            {
                invoice.CreatedUtc = DateTime.UtcNow;
                invoice.CreatedBy = actor;
                using var cmd = _db.CreateCommand("""
                    INSERT INTO invoices(invoice_number, patient_id, invoice_date, due_date, subtotal, discount,
                        discount_type, tax_rate, tax_amount, total, paid_amount, status, notes, created_utc, created_by, updated_utc)
                    VALUES($no,$pid,$date,$due,$sub,$disc,$dtype,$trate,$tamt,$total,$paid,$status,$notes,$c,$cb,$u);
                    SELECT last_insert_rowid();
                    """);
                cmd.Transaction = tx;
                BindInvoice(cmd, invoice);
                invoice.Id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            else
            {
                using var cmd = _db.CreateCommand("""
                    UPDATE invoices SET invoice_date=$date, due_date=$due, subtotal=$sub, discount=$disc,
                        discount_type=$dtype, tax_rate=$trate, tax_amount=$tamt, total=$total, status=$status,
                        notes=$notes, updated_utc=$u WHERE id=$id
                    """);
                cmd.Transaction = tx;
                BindInvoice(cmd, invoice);
                cmd.AddValue("$id", invoice.Id);
                cmd.ExecuteNonQuery();

                using var del = _db.CreateCommand("DELETE FROM invoice_items WHERE invoice_id=$id");
                del.Transaction = tx;
                del.AddValue("$id", invoice.Id);
                del.ExecuteNonQuery();
            }

            var order = 0;
            foreach (var item in invoice.Items)
            {
                using var ins = _db.CreateCommand("""
                    INSERT INTO invoice_items(invoice_id, description, item_type, quantity, unit_price, discount, line_total, treatment_id, sort_order)
                    VALUES($iid,$d,$t,$q,$up,$disc,$lt,$tid,$o)
                    """);
                ins.Transaction = tx;
                ins.AddValue("$iid", invoice.Id);
                ins.AddValue("$d", item.Description.Trim());
                ins.AddValue("$t", item.ItemType);
                ins.AddMoney("$q", item.Quantity);
                ins.AddMoney("$up", item.UnitPrice);
                ins.AddMoney("$disc", item.Discount);
                ins.AddMoney("$lt", item.LineTotal);
                ins.AddValue("$tid", item.TreatmentId);
                ins.AddValue("$o", order++);
                ins.ExecuteNonQuery();

                if (item.TreatmentId is { } tid)
                {
                    using var link = _db.CreateCommand("UPDATE treatments SET invoice_id=$iid WHERE id=$tid");
                    link.Transaction = tx;
                    link.AddValue("$iid", invoice.Id);
                    link.AddValue("$tid", tid);
                    link.ExecuteNonQuery();
                }
            }

            return Task.FromResult(invoice.Id);
        }, ct).ConfigureAwait(false);

        _audit.Log(isNew ? "invoice.created" : "invoice.updated", "Invoice", id.ToString(),
            $"{invoice.InvoiceNumber} — total {invoice.Total:0.00}", actor);
        return id;
    }

    private static void BindInvoice(Microsoft.Data.Sqlite.SqliteCommand cmd, Invoice i)
    {
        cmd.AddValue("$no", i.InvoiceNumber);
        cmd.AddValue("$pid", i.PatientId);
        cmd.AddDate("$date", i.InvoiceDate);
        cmd.AddDate("$due", i.DueDate);
        cmd.AddMoney("$sub", i.Subtotal);
        cmd.AddMoney("$disc", i.Discount);
        cmd.AddValue("$dtype", i.DiscountType);
        cmd.AddMoney("$trate", i.TaxRate);
        cmd.AddMoney("$tamt", i.TaxAmount);
        cmd.AddMoney("$total", i.Total);
        cmd.AddMoney("$paid", i.PaidAmount);
        cmd.AddValue("$status", i.Status);
        cmd.AddValue("$notes", i.Notes);
        cmd.AddValue("$c", i.CreatedUtc.ToString("O"));
        cmd.AddValue("$cb", i.CreatedBy);
        cmd.AddValue("$u", i.UpdatedUtc.ToString("O"));
    }

    private static Invoice MapInvoice(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64Value("id"),
        InvoiceNumber = r.GetStringOrEmpty("invoice_number"),
        PatientId = r.GetInt64Value("patient_id"),
        InvoiceDate = r.GetDateValue("invoice_date"),
        DueDate = r.GetDateOrNull("due_date"),
        Subtotal = r.GetDecimalValue("subtotal"),
        Discount = r.GetDecimalValue("discount"),
        DiscountType = r.GetStringOrEmpty("discount_type"),
        TaxRate = r.GetDecimalValue("tax_rate"),
        TaxAmount = r.GetDecimalValue("tax_amount"),
        Total = r.GetDecimalValue("total"),
        PaidAmount = r.GetDecimalValue("paid_amount"),
        Status = r.GetStringOrEmpty("status"),
        Notes = r.GetStringOrNull("notes"),
        CreatedUtc = r.GetUtcValue("created_utc"),
        CreatedBy = r.GetStringOrNull("created_by"),
        UpdatedUtc = r.GetUtcValue("updated_utc"),
        PatientName = r.GetStringOrEmpty("patient_name"),
        PatientCode = r.GetStringOrEmpty("patient_code")
    };

    public async Task CancelInvoiceAsync(long id, string? actor, CancellationToken ct = default)
    {
        var paid = await Task.Run(() =>
        {
            using var cmd = _db.CreateCommand("SELECT paid_amount FROM invoices WHERE id=$id");
            cmd.AddValue("$id", id);
            return Convert.ToDecimal(cmd.ExecuteScalar() ?? 0d);
        }, ct).ConfigureAwait(false);

        if (paid > 0m)
            throw new DentivaValidationException("This invoice already has payments recorded and cannot be cancelled. Record a refund instead.");

        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("UPDATE invoices SET status='Cancelled', updated_utc=$u WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddValue("$u", DateTime.UtcNow.ToString("O"));
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        _audit.Log("invoice.cancelled", "Invoice", id.ToString(), null, actor);
    }

    public async Task DeleteInvoiceAsync(long id, string? actor, CancellationToken ct = default)
    {
        var hasPayments = await Task.Run(() =>
        {
            using var cmd = _db.CreateCommand("SELECT COUNT(*) FROM payments WHERE invoice_id=$id");
            cmd.AddValue("$id", id);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
        }, ct).ConfigureAwait(false);

        if (hasPayments)
            throw new DentivaValidationException("Payments are linked to this invoice. Remove the payments first or cancel the invoice instead.");

        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("DELETE FROM invoices WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        _audit.Log("invoice.deleted", "Invoice", id.ToString(), null, actor);
    }

    // ---------- Payments ----------
    public async Task<Payment> RecordPaymentAsync(Payment payment, string? actor, CancellationToken ct = default)
    {
        var amountCheck = Validation.ValidateAmount(payment.Amount, allowZero: false);
        if (!amountCheck.IsValid) throw new DentivaValidationException(amountCheck.Message!);

        var allowAdvance = _settings.GetBool(SettingsService.Keys.AllowAdvance, true);

        if (payment.InvoiceId is { } invoiceId)
        {
            var snapshot = await Task.Run(() =>
            {
                using var cmd = _db.CreateCommand("SELECT total, paid_amount, status, patient_id FROM invoices WHERE id=$id");
                cmd.AddValue("$id", invoiceId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return ((decimal, decimal, string, long)?)null;
                return (r.GetDecimalValue("total"), r.GetDecimalValue("paid_amount"), r.GetStringOrEmpty("status"), r.GetInt64Value("patient_id"));
            }, ct).ConfigureAwait(false);

            if (snapshot is null)
                throw new DentivaValidationException("The selected invoice no longer exists.");

            var (total, alreadyPaid, status, invoicePatient) = snapshot.Value;
            if (status == "Cancelled")
                throw new DentivaValidationException("Payments cannot be recorded against a cancelled invoice.");

            if (!payment.IsRefund)
            {
                var check = BillingCalculator.ValidatePayment(payment.Amount, total, alreadyPaid, allowAdvance);
                if (!check.IsValid) throw new DentivaValidationException(check.Message!);
            }
            else if (payment.Amount > alreadyPaid)
            {
                throw new DentivaValidationException($"The refund exceeds the amount collected on this invoice ({alreadyPaid:0.00}).");
            }

            if (payment.PatientId <= 0) payment.PatientId = invoicePatient;
        }

        if (payment.PatientId <= 0)
            throw new DentivaValidationException("A payment must be linked to a patient.");

        if (string.IsNullOrWhiteSpace(payment.ReceiptNumber))
            payment.ReceiptNumber = _sequences.NextReceiptNumber();

        payment.CreatedUtc = DateTime.UtcNow;

        await _db.WriteAsync(tx =>
        {
            using (var cmd = _db.CreateCommand("""
                INSERT INTO payments(receipt_number, invoice_id, patient_id, amount, payment_date, method, reference, notes, received_by, is_refund, created_utc, updated_utc)
                VALUES($no,$inv,$pid,$amt,$date,$m,$ref,$notes,$by,$refund,$c,$c);
                SELECT last_insert_rowid();
                """))
            {
                cmd.Transaction = tx;
                cmd.AddValue("$no", payment.ReceiptNumber);
                cmd.AddValue("$inv", payment.InvoiceId);
                cmd.AddValue("$pid", payment.PatientId);
                cmd.AddMoney("$amt", payment.Amount);
                cmd.AddDateTime("$date", payment.PaymentDate);
                cmd.AddValue("$m", payment.Method);
                cmd.AddValue("$ref", payment.Reference);
                cmd.AddValue("$notes", payment.Notes);
                cmd.AddValue("$by", payment.ReceivedBy ?? actor);
                cmd.AddBool("$refund", payment.IsRefund);
                cmd.AddValue("$c", payment.CreatedUtc.ToString("O"));
                payment.Id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }

            if (payment.InvoiceId is { } invId)
            {
                RecalculateInvoice(invId, tx);
            }

            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        _audit.Log(payment.IsRefund ? "payment.refunded" : "payment.recorded", "Payment", payment.Id.ToString(),
            $"{payment.ReceiptNumber} — {payment.Amount:0.00} via {payment.Method}", actor);
        return payment;
    }

    /// <summary>Recomputes paid_amount and status for an invoice from its payment ledger.</summary>
    private void RecalculateInvoice(long invoiceId, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        decimal paid;
        using (var cmd = _db.CreateCommand("SELECT COALESCE(SUM(CASE WHEN is_refund=1 THEN -amount ELSE amount END),0) FROM payments WHERE invoice_id=$id"))
        {
            cmd.Transaction = tx;
            cmd.AddValue("$id", invoiceId);
            paid = Convert.ToDecimal(cmd.ExecuteScalar() ?? 0d);
        }

        decimal total;
        string status;
        using (var cmd = _db.CreateCommand("SELECT total, status FROM invoices WHERE id=$id"))
        {
            cmd.Transaction = tx;
            cmd.AddValue("$id", invoiceId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return;
            total = r.GetDecimalValue("total");
            status = r.GetStringOrEmpty("status");
        }

        var newStatus = status == "Cancelled" ? "Cancelled" : BillingCalculator.DetermineStatus(total, paid);
        using (var upd = _db.CreateCommand("UPDATE invoices SET paid_amount=$p, status=$s, updated_utc=$u WHERE id=$id"))
        {
            upd.Transaction = tx;
            upd.AddMoney("$p", BillingCalculator.Round(Math.Max(0m, paid)));
            upd.AddValue("$s", newStatus);
            upd.AddValue("$u", DateTime.UtcNow.ToString("O"));
            upd.AddValue("$id", invoiceId);
            upd.ExecuteNonQuery();
        }
    }

    public Task<PagedResult<Payment>> QueryPaymentsAsync(string? search, DateTime? from, DateTime? to, string? method,
        long? patientId = null, int page = 1, int pageSize = 50, CancellationToken ct = default) => Task.Run(() =>
    {
        var where = new List<string>();
        var ps = new List<(string, object)>();

        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(pay.receipt_number LIKE $s OR p.full_name LIKE $s OR p.patient_code LIKE $s OR i.invoice_number LIKE $s OR pay.reference LIKE $s)");
            ps.Add(("$s", "%" + search.Trim() + "%"));
        }

        if (from is { } f) { where.Add("date(pay.payment_date) >= $f"); ps.Add(("$f", f.ToDbDate())); }
        if (to is { } t) { where.Add("date(pay.payment_date) <= $t"); ps.Add(("$t", t.ToDbDate())); }
        if (!string.IsNullOrWhiteSpace(method) && method != "All") { where.Add("pay.method = $m"); ps.Add(("$m", method)); }
        if (patientId is { } pid) { where.Add("pay.patient_id = $pid"); ps.Add(("$pid", pid)); }

        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;
        pageSize = Math.Clamp(pageSize, 1, 500);
        page = Math.Max(1, page);

        int total;
        using (var countCmd = _db.CreateCommand($"""
            SELECT COUNT(*) FROM payments pay JOIN patients p ON p.id=pay.patient_id
            LEFT JOIN invoices i ON i.id=pay.invoice_id {whereSql}
            """))
        {
            foreach (var (n, v) in ps) countCmd.AddValue(n, v);
            total = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);
        }

        var items = new List<Payment>();
        using var cmd = _db.CreateCommand($"""
            SELECT pay.*, p.full_name AS patient_name, p.patient_code, i.invoice_number
            FROM payments pay JOIN patients p ON p.id=pay.patient_id
            LEFT JOIN invoices i ON i.id=pay.invoice_id
            {whereSql}
            ORDER BY pay.payment_date DESC, pay.id DESC LIMIT $l OFFSET $o
            """);
        foreach (var (n, v) in ps) cmd.AddValue(n, v);
        cmd.AddValue("$l", pageSize);
        cmd.AddValue("$o", (page - 1) * pageSize);
        using var r = cmd.ExecuteReader();
        while (r.Read()) items.Add(MapPayment(r));

        return new PagedResult<Payment> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }, ct);

    public Task<Payment?> GetPaymentAsync(long id, CancellationToken ct = default) => Task.Run(() =>
    {
        using var cmd = _db.CreateCommand("""
            SELECT pay.*, p.full_name AS patient_name, p.patient_code, i.invoice_number
            FROM payments pay JOIN patients p ON p.id=pay.patient_id
            LEFT JOIN invoices i ON i.id=pay.invoice_id WHERE pay.id=$id
            """);
        cmd.AddValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapPayment(r) : null;
    }, ct);

    private static Payment MapPayment(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64Value("id"),
        ReceiptNumber = r.GetStringOrEmpty("receipt_number"),
        InvoiceId = r.GetInt64OrNull("invoice_id"),
        PatientId = r.GetInt64Value("patient_id"),
        Amount = r.GetDecimalValue("amount"),
        PaymentDate = r.GetDateValue("payment_date"),
        Method = r.GetStringOrEmpty("method"),
        Reference = r.GetStringOrNull("reference"),
        Notes = r.GetStringOrNull("notes"),
        ReceivedBy = r.GetStringOrNull("received_by"),
        IsRefund = r.GetBoolValue("is_refund"),
        CreatedUtc = r.GetUtcValue("created_utc"),
        PatientName = r.GetStringOrEmpty("patient_name"),
        PatientCode = r.GetStringOrEmpty("patient_code"),
        InvoiceNumber = r.GetStringOrNull("invoice_number")
    };

    public async Task DeletePaymentAsync(long id, string? actor, CancellationToken ct = default)
    {
        long? invoiceId = await Task.Run(() =>
        {
            using var cmd = _db.CreateCommand("SELECT invoice_id FROM payments WHERE id=$id");
            cmd.AddValue("$id", id);
            var value = cmd.ExecuteScalar();
            return value is null or DBNull ? (long?)null : Convert.ToInt64(value);
        }, ct).ConfigureAwait(false);

        await _db.WriteAsync(tx =>
        {
            using (var cmd = _db.CreateCommand("DELETE FROM payments WHERE id=$id"))
            {
                cmd.Transaction = tx;
                cmd.AddValue("$id", id);
                cmd.ExecuteNonQuery();
            }

            if (invoiceId is { } inv) RecalculateInvoice(inv, tx);
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        _audit.Log("payment.deleted", "Payment", id.ToString(), null, actor);
    }

    public Task<List<Treatment>> GetUnbilledTreatmentsAsync(long patientId, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<Treatment>();
        using var cmd = _db.CreateCommand("SELECT * FROM treatments WHERE patient_id=$p AND invoice_id IS NULL ORDER BY treatment_date DESC");
        cmd.AddValue("$p", patientId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Treatment
            {
                Id = r.GetInt64Value("id"),
                PatientId = r.GetInt64Value("patient_id"),
                TreatmentDate = r.GetDateValue("treatment_date"),
                CategoryName = r.GetStringOrNull("category_name"),
                ProcedureName = r.GetStringOrEmpty("procedure_name"),
                Teeth = r.GetStringOrNull("teeth"),
                Cost = r.GetDecimalValue("cost"),
                Discount = r.GetDecimalValue("discount")
            });
        }

        return list;
    }, ct);

    public Task<List<string>> GetPaymentMethodsAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<string>();
        using var cmd = _db.CreateCommand("SELECT name FROM payment_methods WHERE is_active=1 ORDER BY sort_order, name");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }, ct);

    public async Task SavePaymentMethodAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DentivaValidationException("The payment method name is required.");
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("INSERT OR IGNORE INTO payment_methods(name, is_active, sort_order) VALUES($n,1,999)");
            cmd.Transaction = tx;
            cmd.AddValue("$n", name.Trim());
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    public async Task SetPaymentMethodActiveAsync(string name, bool active, CancellationToken ct = default)
    {
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("UPDATE payment_methods SET is_active=$a WHERE name=$n");
            cmd.Transaction = tx;
            cmd.AddBool("$a", active);
            cmd.AddValue("$n", name);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }
}
