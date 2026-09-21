using Dentiva.Core.Data;
using Dentiva.Core.Models;
using Dentiva.Core.Repositories;

namespace Dentiva.Core.Services;

/// <summary>
/// Append-only audit trail. Records who did what to which entity, never the clinical payload itself.
/// </summary>
public sealed class AuditService
{
    private readonly Database _db;

    public AuditService(Database db)
    {
        _db = db;
    }

    public string? CurrentUser { get; set; }

    public void Log(string action, string? entity = null, string? entityId = null, string? summary = null, string? actor = null, string? metadata = null)
    {
        try
        {
            using var cmd = _db.CreateCommand("""
                INSERT INTO audit_log(timestamp_utc, username, action, entity, entity_id, summary, metadata)
                VALUES ($t, $u, $a, $e, $eid, $s, $m)
                """);
            cmd.AddValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.AddValue("$u", actor ?? CurrentUser);
            cmd.AddValue("$a", action);
            cmd.AddValue("$e", entity);
            cmd.AddValue("$eid", entityId);
            cmd.AddValue("$s", Truncate(summary, 500));
            cmd.AddValue("$m", Truncate(metadata, 2000));
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Auditing must never break a clinical workflow.
        }
    }

    private static string? Truncate(string? value, int max)
        => value is { Length: > 0 } && value.Length > max ? value[..max] : value;

    public Task<PagedResult<AuditEntry>> QueryAsync(string? search, DateTime? from, DateTime? to, int page = 1, int pageSize = 100, CancellationToken ct = default) => Task.Run(() =>
    {
        var where = new List<string>();
        var ps = new List<(string, object)>();

        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(action LIKE $s OR entity LIKE $s OR summary LIKE $s OR username LIKE $s)");
            ps.Add(("$s", "%" + search.Trim() + "%"));
        }

        if (from is { } f)
        {
            where.Add("timestamp_utc >= $from");
            ps.Add(("$from", f.ToUniversalTime().ToString("O")));
        }

        if (to is { } t)
        {
            where.Add("timestamp_utc <= $to");
            ps.Add(("$to", t.Date.AddDays(1).ToUniversalTime().ToString("O")));
        }

        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;
        pageSize = Math.Clamp(pageSize, 1, 1000);
        page = Math.Max(1, page);

        var total = 0;
        using (var countCmd = _db.CreateCommand($"SELECT COUNT(*) FROM audit_log {whereSql}"))
        {
            foreach (var (n, v) in ps) countCmd.AddValue(n, v);
            total = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);
        }

        var items = new List<AuditEntry>();
        using var cmd = _db.CreateCommand($"SELECT * FROM audit_log {whereSql} ORDER BY timestamp_utc DESC, id DESC LIMIT $l OFFSET $o");
        foreach (var (n, v) in ps) cmd.AddValue(n, v);
        cmd.AddValue("$l", pageSize);
        cmd.AddValue("$o", (page - 1) * pageSize);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new AuditEntry
            {
                Id = reader.GetInt64Value("id"),
                TimestampUtc = reader.GetUtcValue("timestamp_utc"),
                Username = reader.GetStringOrNull("username"),
                Action = reader.GetStringOrEmpty("action"),
                Entity = reader.GetStringOrNull("entity"),
                EntityId = reader.GetStringOrNull("entity_id"),
                Summary = reader.GetStringOrNull("summary"),
                Metadata = reader.GetStringOrNull("metadata")
            });
        }

        return new PagedResult<AuditEntry> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }, ct);
}
