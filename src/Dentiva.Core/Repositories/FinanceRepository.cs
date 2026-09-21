using Dentiva.Core.Data;
using Dentiva.Core.Models;
using Dentiva.Core.Services;

namespace Dentiva.Core.Repositories;

public sealed class FinanceSummary
{
    public decimal GrossBilled { get; set; }
    public decimal Collected { get; set; }
    public decimal Refunded { get; set; }
    public decimal Outstanding { get; set; }
    public decimal Expenses { get; set; }
    public decimal SalaryPaid { get; set; }
    public decimal NetResult => decimal.Round(Collected - Refunded - Expenses - SalaryPaid, 2, MidpointRounding.AwayFromZero);
    public int InvoiceCount { get; set; }
    public int PaymentCount { get; set; }
    public int ExpenseCount { get; set; }
}

public sealed record CategoryTotal(string Category, decimal Amount, int Count);
public sealed record TrendPoint(DateTime Date, decimal Value, decimal Secondary);

/// <summary>
/// Expenses, salary payments and all aggregate finance analytics. Aggregation happens in SQL so the
/// UI never needs to load the transaction tables into memory.
/// </summary>
public sealed class FinanceRepository
{
    private readonly Database _db;
    private readonly AuditService _audit;

    public FinanceRepository(Database db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public Task<FinanceSummary> GetSummaryAsync(DateTime from, DateTime to, CancellationToken ct = default) => Task.Run(() =>
    {
        var s = new FinanceSummary();

        using (var cmd = _db.CreateCommand("""
            SELECT COALESCE(SUM(total),0), COUNT(*) FROM invoices
            WHERE invoice_date BETWEEN $f AND $t AND status <> 'Cancelled'
            """))
        {
            cmd.AddDate("$f", from); cmd.AddDate("$t", to);
            using var r = cmd.ExecuteReader();
            if (r.Read()) { s.GrossBilled = Convert.ToDecimal(r.GetDouble(0)); s.InvoiceCount = r.GetInt32(1); }
        }

        using (var cmd = _db.CreateCommand("""
            SELECT COALESCE(SUM(CASE WHEN is_refund=0 THEN amount ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN is_refund=1 THEN amount ELSE 0 END),0),
                   COUNT(*)
            FROM payments WHERE date(payment_date) BETWEEN $f AND $t
            """))
        {
            cmd.AddDate("$f", from); cmd.AddDate("$t", to);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                s.Collected = Convert.ToDecimal(r.GetDouble(0));
                s.Refunded = Convert.ToDecimal(r.GetDouble(1));
                s.PaymentCount = r.GetInt32(2);
            }
        }

        using (var cmd = _db.CreateCommand("SELECT COALESCE(SUM(total - paid_amount),0) FROM invoices WHERE status NOT IN ('Cancelled','Paid')"))
        {
            s.Outstanding = Convert.ToDecimal(cmd.ExecuteScalar() ?? 0d);
        }

        using (var cmd = _db.CreateCommand("SELECT COALESCE(SUM(amount),0), COUNT(*) FROM expenses WHERE expense_date BETWEEN $f AND $t"))
        {
            cmd.AddDate("$f", from); cmd.AddDate("$t", to);
            using var r = cmd.ExecuteReader();
            if (r.Read()) { s.Expenses = Convert.ToDecimal(r.GetDouble(0)); s.ExpenseCount = r.GetInt32(1); }
        }

        using (var cmd = _db.CreateCommand("SELECT COALESCE(SUM(amount),0) FROM salary_payments WHERE payment_date BETWEEN $f AND $t"))
        {
            cmd.AddDate("$f", from); cmd.AddDate("$t", to);
            s.SalaryPaid = Convert.ToDecimal(cmd.ExecuteScalar() ?? 0d);
        }

        return s;
    }, ct);

    public Task<List<CategoryTotal>> GetExpenseByCategoryAsync(DateTime from, DateTime to, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<CategoryTotal>();
        using var cmd = _db.CreateCommand("""
            SELECT COALESCE(NULLIF(category_name,''), 'Uncategorised') AS cat, SUM(amount), COUNT(*)
            FROM expenses WHERE expense_date BETWEEN $f AND $t GROUP BY cat ORDER BY SUM(amount) DESC
            """);
        cmd.AddDate("$f", from); cmd.AddDate("$t", to);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new CategoryTotal(r.GetString(0), Convert.ToDecimal(r.GetDouble(1)), r.GetInt32(2)));
        return list;
    }, ct);

    public Task<List<CategoryTotal>> GetIncomeByCategoryAsync(DateTime from, DateTime to, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<CategoryTotal>();
        using var cmd = _db.CreateCommand("""
            SELECT COALESCE(NULLIF(ii.item_type,''), 'Other') AS cat, SUM(ii.line_total), COUNT(*)
            FROM invoice_items ii JOIN invoices i ON i.id = ii.invoice_id
            WHERE i.invoice_date BETWEEN $f AND $t AND i.status <> 'Cancelled'
            GROUP BY cat ORDER BY SUM(ii.line_total) DESC
            """);
        cmd.AddDate("$f", from); cmd.AddDate("$t", to);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new CategoryTotal(r.GetString(0), Convert.ToDecimal(r.GetDouble(1)), r.GetInt32(2)));
        return list;
    }, ct);

    public Task<List<CategoryTotal>> GetPaymentMethodDistributionAsync(DateTime from, DateTime to, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<CategoryTotal>();
        using var cmd = _db.CreateCommand("""
            SELECT method, SUM(CASE WHEN is_refund=1 THEN -amount ELSE amount END), COUNT(*)
            FROM payments WHERE date(payment_date) BETWEEN $f AND $t
            GROUP BY method ORDER BY 2 DESC
            """);
        cmd.AddDate("$f", from); cmd.AddDate("$t", to);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new CategoryTotal(r.GetString(0), Convert.ToDecimal(r.GetDouble(1)), r.GetInt32(2)));
        return list;
    }, ct);

    /// <summary>Daily collected income vs expense for trend charts.</summary>
    public Task<List<TrendPoint>> GetDailyTrendAsync(DateTime from, DateTime to, CancellationToken ct = default) => Task.Run(() =>
    {
        var income = new Dictionary<DateTime, decimal>();
        var expense = new Dictionary<DateTime, decimal>();

        using (var cmd = _db.CreateCommand("""
            SELECT date(payment_date) AS d, SUM(CASE WHEN is_refund=1 THEN -amount ELSE amount END)
            FROM payments WHERE date(payment_date) BETWEEN $f AND $t GROUP BY d
            """))
        {
            cmd.AddDate("$f", from); cmd.AddDate("$t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (DateTime.TryParse(r.GetString(0), out var d)) income[d.Date] = Convert.ToDecimal(r.GetDouble(1));
            }
        }

        using (var cmd = _db.CreateCommand("SELECT expense_date, SUM(amount) FROM expenses WHERE expense_date BETWEEN $f AND $t GROUP BY expense_date"))
        {
            cmd.AddDate("$f", from); cmd.AddDate("$t", to);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (DateTime.TryParse(r.GetString(0), out var d)) expense[d.Date] = Convert.ToDecimal(r.GetDouble(1));
            }
        }

        var points = new List<TrendPoint>();
        var span = (to.Date - from.Date).Days;
        if (span < 0) return points;
        for (var i = 0; i <= Math.Min(span, 730); i++)
        {
            var day = from.Date.AddDays(i);
            points.Add(new TrendPoint(day, income.GetValueOrDefault(day), expense.GetValueOrDefault(day)));
        }

        return points;
    }, ct);

    // ---------- Expenses ----------
    public Task<PagedResult<Expense>> QueryExpensesAsync(string? search, long? categoryId, DateTime? from, DateTime? to,
        int page = 1, int pageSize = 50, CancellationToken ct = default) => Task.Run(() =>
    {
        var where = new List<string>();
        var ps = new List<(string, object)>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(description LIKE $s OR vendor LIKE $s OR reference LIKE $s OR category_name LIKE $s)");
            ps.Add(("$s", "%" + search.Trim() + "%"));
        }
        if (categoryId is { } cid) { where.Add("category_id = $c"); ps.Add(("$c", cid)); }
        if (from is { } f) { where.Add("expense_date >= $f"); ps.Add(("$f", f.ToDbDate())); }
        if (to is { } t) { where.Add("expense_date <= $t"); ps.Add(("$t", t.ToDbDate())); }

        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;
        pageSize = Math.Clamp(pageSize, 1, 500);
        page = Math.Max(1, page);

        int total;
        using (var countCmd = _db.CreateCommand($"SELECT COUNT(*) FROM expenses {whereSql}"))
        {
            foreach (var (n, v) in ps) countCmd.AddValue(n, v);
            total = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);
        }

        var items = new List<Expense>();
        using var cmd = _db.CreateCommand($"SELECT * FROM expenses {whereSql} ORDER BY expense_date DESC, id DESC LIMIT $l OFFSET $o");
        foreach (var (n, v) in ps) cmd.AddValue(n, v);
        cmd.AddValue("$l", pageSize);
        cmd.AddValue("$o", (page - 1) * pageSize);
        using var r = cmd.ExecuteReader();
        while (r.Read()) items.Add(MapExpense(r));

        return new PagedResult<Expense> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }, ct);

    private static Expense MapExpense(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64Value("id"),
        ExpenseDate = r.GetDateValue("expense_date"),
        CategoryId = r.GetInt64OrNull("category_id"),
        CategoryName = r.GetStringOrNull("category_name"),
        Description = r.GetStringOrEmpty("description"),
        Amount = r.GetDecimalValue("amount"),
        PaymentMethod = r.GetStringOrNull("payment_method"),
        Reference = r.GetStringOrNull("reference"),
        Vendor = r.GetStringOrNull("vendor"),
        Notes = r.GetStringOrNull("notes"),
        StaffId = r.GetInt64OrNull("staff_id"),
        CreatedUtc = r.GetUtcValue("created_utc"),
        CreatedBy = r.GetStringOrNull("created_by"),
        UpdatedUtc = r.GetUtcValue("updated_utc")
    };

    public async Task<long> SaveExpenseAsync(Expense expense, string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expense.Description))
            throw new DentivaValidationException("Enter a description for the expense.");
        var amount = Validation.ValidateAmount(expense.Amount, allowZero: false);
        if (!amount.IsValid) throw new DentivaValidationException(amount.Message!);

        expense.UpdatedUtc = DateTime.UtcNow;
        var isNew = expense.Id == 0;

        var id = await _db.WriteAsync(tx =>
        {
            if (isNew)
            {
                expense.CreatedUtc = DateTime.UtcNow;
                expense.CreatedBy = actor;
                using var cmd = _db.CreateCommand("""
                    INSERT INTO expenses(expense_date, category_id, category_name, description, amount, payment_method,
                        reference, vendor, notes, staff_id, created_utc, created_by, updated_utc)
                    VALUES($d,$cid,$cname,$desc,$amt,$m,$ref,$vendor,$notes,$staff,$c,$cb,$u);
                    SELECT last_insert_rowid();
                    """);
                cmd.Transaction = tx;
                BindExpense(cmd, expense);
                expense.Id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            else
            {
                using var cmd = _db.CreateCommand("""
                    UPDATE expenses SET expense_date=$d, category_id=$cid, category_name=$cname, description=$desc,
                        amount=$amt, payment_method=$m, reference=$ref, vendor=$vendor, notes=$notes, staff_id=$staff,
                        updated_utc=$u WHERE id=$id
                    """);
                cmd.Transaction = tx;
                BindExpense(cmd, expense);
                cmd.AddValue("$id", expense.Id);
                cmd.ExecuteNonQuery();
            }

            return Task.FromResult(expense.Id);
        }, ct).ConfigureAwait(false);

        _audit.Log(isNew ? "expense.created" : "expense.updated", "Expense", id.ToString(), $"{expense.Description} — {expense.Amount:0.00}", actor);
        return id;
    }

    private static void BindExpense(Microsoft.Data.Sqlite.SqliteCommand cmd, Expense e)
    {
        cmd.AddDate("$d", e.ExpenseDate);
        cmd.AddValue("$cid", e.CategoryId);
        cmd.AddValue("$cname", e.CategoryName);
        cmd.AddValue("$desc", e.Description.Trim());
        cmd.AddMoney("$amt", e.Amount);
        cmd.AddValue("$m", e.PaymentMethod);
        cmd.AddValue("$ref", e.Reference);
        cmd.AddValue("$vendor", e.Vendor);
        cmd.AddValue("$notes", e.Notes);
        cmd.AddValue("$staff", e.StaffId);
        cmd.AddValue("$c", e.CreatedUtc.ToString("O"));
        cmd.AddValue("$cb", e.CreatedBy);
        cmd.AddValue("$u", e.UpdatedUtc.ToString("O"));
    }

    public async Task DeleteExpenseAsync(long id, string? actor, CancellationToken ct = default)
    {
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("DELETE FROM expenses WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        _audit.Log("expense.deleted", "Expense", id.ToString(), null, actor);
    }

    public Task<List<ExpenseCategory>> GetExpenseCategoriesAsync(bool activeOnly = true, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<ExpenseCategory>();
        using var cmd = _db.CreateCommand(activeOnly
            ? "SELECT * FROM expense_categories WHERE is_active=1 ORDER BY sort_order, name"
            : "SELECT * FROM expense_categories ORDER BY sort_order, name");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ExpenseCategory
            {
                Id = r.GetInt64Value("id"),
                Name = r.GetStringOrEmpty("name"),
                NameBn = r.GetStringOrNull("name_bn"),
                IsActive = r.GetBoolValue("is_active"),
                SortOrder = r.GetIntValue("sort_order")
            });
        }

        return list;
    }, ct);

    public async Task SaveExpenseCategoryAsync(ExpenseCategory category, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(category.Name))
            throw new DentivaValidationException("The expense category name is required.");

        await _db.WriteAsync(tx =>
        {
            if (category.Id == 0)
            {
                using var cmd = _db.CreateCommand("INSERT INTO expense_categories(name, name_bn, is_active, sort_order, created_utc) VALUES($n,$bn,$a,$o,$c)");
                cmd.Transaction = tx;
                cmd.AddValue("$n", category.Name.Trim());
                cmd.AddValue("$bn", category.NameBn);
                cmd.AddBool("$a", category.IsActive);
                cmd.AddValue("$o", category.SortOrder);
                cmd.AddValue("$c", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }
            else
            {
                using var cmd = _db.CreateCommand("UPDATE expense_categories SET name=$n, name_bn=$bn, is_active=$a, sort_order=$o WHERE id=$id");
                cmd.Transaction = tx;
                cmd.AddValue("$n", category.Name.Trim());
                cmd.AddValue("$bn", category.NameBn);
                cmd.AddBool("$a", category.IsActive);
                cmd.AddValue("$o", category.SortOrder);
                cmd.AddValue("$id", category.Id);
                cmd.ExecuteNonQuery();
            }

            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    public async Task DeleteExpenseCategoryAsync(long id, CancellationToken ct = default)
    {
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("UPDATE expense_categories SET is_active=0 WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddValue("$id", id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }
}
