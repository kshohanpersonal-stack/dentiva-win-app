using System.Data;
using Microsoft.Data.Sqlite;

namespace Dentiva.Core.Data;

/// <summary>
/// Owns the SQLite connection lifecycle, pragmas, migrations and integrity checking.
/// All access is parameterised; no dynamic SQL is built from user input.
/// </summary>
public sealed class Database : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SqliteConnection? _connection;
    private bool _disposed;

    public Database(AppPaths paths)
    {
        Paths = paths;
    }

    public AppPaths Paths { get; }

    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = Paths.DatabaseFile,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        ForeignKeys = true,
        Pooling = false
    }.ToString();

    public bool IsOpen => _connection is { State: ConnectionState.Open };

    public void Open()
    {
        if (IsOpen)
        {
            return;
        }

        Paths.EnsureCreated();
        _connection = new SqliteConnection(ConnectionString);
        _connection.Open();
        ApplyPragmas(_connection);
        Migrate();
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=10000;
            PRAGMA temp_store=MEMORY;
            """;
        cmd.ExecuteNonQuery();
    }

    public SqliteConnection Connection => _connection ?? throw new InvalidOperationException("The database is not open.");

    public int SchemaVersion
    {
        get
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations";
            try
            {
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
            catch (SqliteException)
            {
                return 0;
            }
        }
    }

    private void Migrate()
    {
        using (var create = Connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version     INTEGER PRIMARY KEY,
                    description TEXT NOT NULL,
                    applied_utc TEXT NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        var current = SchemaVersion;
        foreach (var migration in DatabaseSchema.Migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            using var tx = Connection.BeginTransaction();
            using (var cmd = Connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = migration.Sql;
                cmd.ExecuteNonQuery();
            }

            using (var record = Connection.CreateCommand())
            {
                record.Transaction = tx;
                record.CommandText = "INSERT INTO schema_migrations(version, description, applied_utc) VALUES ($v, $d, $t)";
                record.Parameters.AddWithValue("$v", migration.Version);
                record.Parameters.AddWithValue("$d", migration.Description);
                record.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
                record.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public SqliteCommand CreateCommand(string sql)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    public async Task<T> WriteAsync<T>(Func<SqliteTransaction, Task<T>> work, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = Connection.BeginTransaction();
            try
            {
                var result = await work(tx).ConfigureAwait(false);
                tx.Commit();
                return result;
            }
            catch
            {
                try { tx.Rollback(); } catch { /* connection already broken */ }
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task WriteAsync(Func<SqliteTransaction, Task> work, CancellationToken ct = default)
        => WriteAsync<bool>(async tx => { await work(tx).ConfigureAwait(false); return true; }, ct);

    public string IntegrityCheck()
    {
        using var cmd = CreateCommand("PRAGMA integrity_check;");
        var result = cmd.ExecuteScalar()?.ToString() ?? "unknown";
        return result;
    }

    public string ForeignKeyCheck()
    {
        using var cmd = CreateCommand("PRAGMA foreign_key_check;");
        using var reader = cmd.ExecuteReader();
        var issues = new List<string>();
        while (reader.Read())
        {
            issues.Add($"{reader.GetValue(0)} -> {reader.GetValue(2)}");
        }

        return issues.Count == 0 ? "ok" : string.Join("; ", issues);
    }

    public void Checkpoint()
    {
        using var cmd = CreateCommand("PRAGMA wal_checkpoint(TRUNCATE);");
        cmd.ExecuteNonQuery();
    }

    public void Close()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            Checkpoint();
        }
        catch
        {
            // best effort
        }

        _connection.Close();
        _connection.Dispose();
        _connection = null;
        SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
        _writeLock.Dispose();
    }
}
