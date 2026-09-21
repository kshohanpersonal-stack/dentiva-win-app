using System.Text.Json;
using Dentiva.Core.Data;
using Dentiva.Core.Models;
using Dentiva.Core.Repositories;
using Dentiva.Core.Security;

namespace Dentiva.Core.Services;

/// <summary>
/// Authentication, account lifecycle and permission resolution.
/// Repeated failures trigger a temporary lockout to blunt brute-force attempts.
/// </summary>
public sealed class UserService
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private readonly Database _db;
    private readonly AuditService _audit;

    public UserService(Database db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public AppUser? CurrentUser { get; private set; }

    public bool HasAnyUser()
    {
        using var cmd = _db.CreateCommand("SELECT COUNT(*) FROM users");
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
    }

    public Task<long> CreateUserAsync(string username, string displayName, string role, string password,
        IEnumerable<string>? permissions = null, string? actor = null, CancellationToken ct = default)
    {
        username = (username ?? string.Empty).Trim();
        if (username.Length < 3)
            throw new DentivaValidationException("The username must contain at least 3 characters.");
        if (username.Any(char.IsWhiteSpace))
            throw new DentivaValidationException("The username must not contain spaces.");
        if (string.IsNullOrWhiteSpace(displayName))
            throw new DentivaValidationException("Enter the user's display name.");
        if (PasswordHasher.Evaluate(password) == PasswordStrength.Unacceptable)
            throw new DentivaValidationException("The password must be at least 8 characters long.");
        if (!Permissions.Roles.Contains(role) && string.IsNullOrWhiteSpace(role))
            throw new DentivaValidationException("Select a role for the user.");

        return _db.WriteAsync(tx =>
        {
            using var exists = _db.CreateCommand("SELECT COUNT(*) FROM users WHERE username = $u COLLATE NOCASE");
            exists.Transaction = tx;
            exists.AddValue("$u", username);
            if (Convert.ToInt32(exists.ExecuteScalar() ?? 0) > 0)
                throw new DentivaValidationException($"The username '{username}' is already in use.");

            var (hash, salt, iterations) = PasswordHasher.Hash(password);
            var perms = (permissions ?? Permissions.ForRole(role)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            using var cmd = _db.CreateCommand("""
                INSERT INTO users(username, display_name, role, password_hash, password_salt, password_iterations,
                    permissions_json, is_active, created_utc, updated_utc)
                VALUES($u,$d,$r,$h,$s,$i,$p,1,$c,$c);
                SELECT last_insert_rowid();
                """);
            cmd.Transaction = tx;
            cmd.AddValue("$u", username);
            cmd.AddValue("$d", displayName.Trim());
            cmd.AddValue("$r", role);
            cmd.AddValue("$h", hash);
            cmd.AddValue("$s", salt);
            cmd.AddValue("$i", iterations);
            cmd.AddValue("$p", JsonSerializer.Serialize(perms));
            cmd.AddValue("$c", DateTime.UtcNow.ToString("O"));
            var id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            _audit.Log("user.created", "User", id.ToString(), $"{username} ({role})", actor);
            return Task.FromResult(id);
        }, ct);
    }

    public Task<AuthResult> AuthenticateAsync(string username, string password, CancellationToken ct = default) => Task.Run(() =>
    {
        username = (username ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return AuthResult.Failure("Enter your username and password.");
        }

        long id;
        string storedHash, storedSalt, role, displayName, permissionsJson;
        int iterations, failed;
        bool isActive;
        DateTime? lockedUntil;

        using (var cmd = _db.CreateCommand("SELECT * FROM users WHERE username = $u COLLATE NOCASE"))
        {
            cmd.AddValue("$u", username);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                return AuthResult.Failure("The username or password is incorrect.");
            }

            id = r.GetInt64Value("id");
            storedHash = r.GetStringOrEmpty("password_hash");
            storedSalt = r.GetStringOrEmpty("password_salt");
            iterations = r.GetIntValue("password_iterations");
            role = r.GetStringOrEmpty("role");
            displayName = r.GetStringOrEmpty("display_name");
            permissionsJson = r.GetStringOrEmpty("permissions_json");
            isActive = r.GetBoolValue("is_active");
            failed = r.GetIntValue("failed_attempts");
            lockedUntil = r.GetUtcOrNull("locked_until_utc");
        }

        if (!isActive)
        {
            return AuthResult.Failure("This account has been deactivated. Contact the clinic owner.");
        }

        if (lockedUntil is { } until && until > DateTime.UtcNow)
        {
            var remaining = Math.Max(1, (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes));
            return AuthResult.Failure($"Too many failed attempts. Try again in {remaining} minute(s).");
        }

        if (!PasswordHasher.Verify(password, storedHash, storedSalt, iterations))
        {
            var attempts = failed + 1;
            var lockUntil = attempts >= MaxFailedAttempts ? DateTime.UtcNow.Add(LockoutDuration) : (DateTime?)null;
            using var upd = _db.CreateCommand("UPDATE users SET failed_attempts=$f, locked_until_utc=$l WHERE id=$id");
            upd.AddValue("$f", attempts);
            upd.AddUtc("$l", lockUntil);
            upd.AddValue("$id", id);
            upd.ExecuteNonQuery();

            _audit.Log("auth.failed", "User", id.ToString(), username, username);
            return AuthResult.Failure(lockUntil is null
                ? "The username or password is incorrect."
                : $"Too many failed attempts. The account is locked for {LockoutDuration.TotalMinutes:0} minutes.");
        }

        using (var upd = _db.CreateCommand("UPDATE users SET failed_attempts=0, locked_until_utc=NULL, last_login_utc=$t WHERE id=$id"))
        {
            upd.AddValue("$t", DateTime.UtcNow.ToString("O"));
            upd.AddValue("$id", id);
            upd.ExecuteNonQuery();
        }

        var permissions = ParsePermissions(permissionsJson, role);
        var user = new AppUser
        {
            Id = id,
            Username = username,
            DisplayName = displayName,
            Role = role,
            IsActive = true,
            LastLoginUtc = DateTime.UtcNow,
            Permissions = permissions
        };

        CurrentUser = user;
        _audit.CurrentUser = username;
        _audit.Log("auth.success", "User", id.ToString(), null, username);
        return AuthResult.Success(user);
    }, ct);

    private static HashSet<string> ParsePermissions(string json, string role)
    {
        try
        {
            var list = JsonSerializer.Deserialize<string[]>(json);
            if (list is { Length: > 0 })
            {
                return new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
            // Fall back to the role defaults below.
        }

        return new HashSet<string>(Permissions.ForRole(role), StringComparer.OrdinalIgnoreCase);
    }

    public void SignOut()
    {
        if (CurrentUser is not null)
        {
            _audit.Log("auth.signout", "User", CurrentUser.Id.ToString(), null, CurrentUser.Username);
        }

        CurrentUser = null;
        _audit.CurrentUser = null;
    }

    public Task<List<AppUser>> GetAllAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<AppUser>();
        using var cmd = _db.CreateCommand("SELECT * FROM users ORDER BY display_name COLLATE NOCASE");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AppUser
            {
                Id = r.GetInt64Value("id"),
                Username = r.GetStringOrEmpty("username"),
                DisplayName = r.GetStringOrEmpty("display_name"),
                Role = r.GetStringOrEmpty("role"),
                IsActive = r.GetBoolValue("is_active"),
                LastLoginUtc = r.GetUtcOrNull("last_login_utc"),
                Permissions = ParsePermissions(r.GetStringOrEmpty("permissions_json"), r.GetStringOrEmpty("role"))
            });
        }

        return list;
    }, ct);

    public async Task UpdateUserAsync(AppUser user, string? actor, CancellationToken ct = default)
    {
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("""
                UPDATE users SET display_name=$d, role=$r, permissions_json=$p, is_active=$a, updated_utc=$u WHERE id=$id
                """);
            cmd.Transaction = tx;
            cmd.AddValue("$d", user.DisplayName.Trim());
            cmd.AddValue("$r", user.Role);
            cmd.AddValue("$p", JsonSerializer.Serialize(user.Permissions.ToArray()));
            cmd.AddBool("$a", user.IsActive);
            cmd.AddValue("$u", DateTime.UtcNow.ToString("O"));
            cmd.AddValue("$id", user.Id);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        if (CurrentUser?.Id == user.Id)
        {
            CurrentUser.DisplayName = user.DisplayName;
            CurrentUser.Role = user.Role;
            CurrentUser.Permissions = user.Permissions;
        }

        _audit.Log("user.updated", "User", user.Id.ToString(), user.Username, actor);
    }

    public async Task ChangePasswordAsync(long userId, string? currentPassword, string newPassword, bool requireCurrent, string? actor, CancellationToken ct = default)
    {
        if (PasswordHasher.Evaluate(newPassword) == PasswordStrength.Unacceptable)
            throw new DentivaValidationException("The new password must be at least 8 characters long.");

        if (requireCurrent)
        {
            var ok = await Task.Run(() =>
            {
                using var cmd = _db.CreateCommand("SELECT password_hash, password_salt, password_iterations FROM users WHERE id=$id");
                cmd.AddValue("$id", userId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return false;
                return PasswordHasher.Verify(currentPassword ?? string.Empty, r.GetStringOrEmpty("password_hash"),
                    r.GetStringOrEmpty("password_salt"), r.GetIntValue("password_iterations"));
            }, ct).ConfigureAwait(false);

            if (!ok) throw new DentivaValidationException("The current password is incorrect.");
        }

        var (hash, salt, iterations) = PasswordHasher.Hash(newPassword);
        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("""
                UPDATE users SET password_hash=$h, password_salt=$s, password_iterations=$i,
                    failed_attempts=0, locked_until_utc=NULL, updated_utc=$u WHERE id=$id
                """);
            cmd.Transaction = tx;
            cmd.AddValue("$h", hash);
            cmd.AddValue("$s", salt);
            cmd.AddValue("$i", iterations);
            cmd.AddValue("$u", DateTime.UtcNow.ToString("O"));
            cmd.AddValue("$id", userId);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        _audit.Log("user.password_changed", "User", userId.ToString(), null, actor);
    }

    public async Task DeleteUserAsync(long userId, string? actor, CancellationToken ct = default)
    {
        var owners = await Task.Run(() =>
        {
            using var cmd = _db.CreateCommand("SELECT COUNT(*) FROM users WHERE role='Owner' AND is_active=1 AND id <> $id");
            cmd.AddValue("$id", userId);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }, ct).ConfigureAwait(false);

        if (owners == 0)
            throw new DentivaValidationException("At least one active owner account must remain. Create another owner before removing this one.");

        await _db.WriteAsync(tx =>
        {
            using var cmd = _db.CreateCommand("DELETE FROM users WHERE id=$id");
            cmd.Transaction = tx;
            cmd.AddValue("$id", userId);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        _audit.Log("user.deleted", "User", userId.ToString(), null, actor);
    }

    public Task<bool> VerifyCurrentUserPasswordAsync(string password, CancellationToken ct = default) => Task.Run(() =>
    {
        if (CurrentUser is null) return false;
        using var cmd = _db.CreateCommand("SELECT password_hash, password_salt, password_iterations FROM users WHERE id=$id");
        cmd.AddValue("$id", CurrentUser.Id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return false;
        return PasswordHasher.Verify(password, r.GetStringOrEmpty("password_hash"), r.GetStringOrEmpty("password_salt"), r.GetIntValue("password_iterations"));
    }, ct);
}

public sealed record AuthResult(bool IsSuccess, AppUser? User, string? Error)
{
    public static AuthResult Success(AppUser user) => new(true, user, null);
    public static AuthResult Failure(string error) => new(false, null, error);
}
