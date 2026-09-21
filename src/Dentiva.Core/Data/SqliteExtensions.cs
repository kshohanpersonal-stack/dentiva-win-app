using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Dentiva.Core.Data;

/// <summary>
/// Null-safe, culture-invariant readers so that dates and decimals round-trip identically on every machine.
/// </summary>
public static class SqliteExtensions
{
    public const string DateFormat = "yyyy-MM-dd";
    public const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

    public static string? GetStringOrNull(this SqliteDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : reader.GetString(i);
    }

    public static string GetStringOrEmpty(this SqliteDataReader reader, string column)
        => reader.GetStringOrNull(column) ?? string.Empty;

    public static long GetInt64Value(this SqliteDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? 0L : reader.GetInt64(i);
    }

    public static long? GetInt64OrNull(this SqliteDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : reader.GetInt64(i);
    }

    public static int GetIntValue(this SqliteDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? 0 : reader.GetInt32(i);
    }

    public static int? GetIntOrNull(this SqliteDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : reader.GetInt32(i);
    }

    public static bool GetBoolValue(this SqliteDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return !reader.IsDBNull(i) && reader.GetInt64(i) != 0;
    }

    public static decimal GetDecimalValue(this SqliteDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        if (reader.IsDBNull(i))
        {
            return 0m;
        }

        return Convert.ToDecimal(reader.GetDouble(i), CultureInfo.InvariantCulture);
    }

    public static DateTime GetDateValue(this SqliteDataReader reader, string column)
        => reader.GetDateOrNull(column) ?? default;

    public static DateTime? GetDateOrNull(this SqliteDataReader reader, string column)
    {
        var raw = reader.GetStringOrNull(column);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTime.TryParseExact(raw, new[] { DateFormat, DateTimeFormat, "O", "yyyy-MM-ddTHH:mm:ss" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            return value;
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out value) ? value : null;
    }

    public static DateTime GetUtcValue(this SqliteDataReader reader, string column)
    {
        var raw = reader.GetStringOrNull(column);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DateTime.UtcNow;
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal, out var value)
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : DateTime.UtcNow;
    }

    public static DateTime? GetUtcOrNull(this SqliteDataReader reader, string column)
    {
        var raw = reader.GetStringOrNull(column);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal, out var value)
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : null;
    }

    public static TimeSpan GetTimeValue(this SqliteDataReader reader, string column)
    {
        var raw = reader.GetStringOrNull(column);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var value) ? value : TimeSpan.Zero;
    }

    public static SqliteParameter AddValue(this SqliteCommand cmd, string name, object? value)
        => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    public static SqliteParameter AddDate(this SqliteCommand cmd, string name, DateTime? value)
        => cmd.Parameters.AddWithValue(name, value?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? (object)DBNull.Value);

    public static SqliteParameter AddDateTime(this SqliteCommand cmd, string name, DateTime? value)
        => cmd.Parameters.AddWithValue(name, value?.ToString(DateTimeFormat, CultureInfo.InvariantCulture) ?? (object)DBNull.Value);

    public static SqliteParameter AddUtc(this SqliteCommand cmd, string name, DateTime? value)
        => cmd.Parameters.AddWithValue(name, value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);

    public static SqliteParameter AddTime(this SqliteCommand cmd, string name, TimeSpan? value)
        => cmd.Parameters.AddWithValue(name, value?.ToString(@"hh\:mm", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);

    public static SqliteParameter AddMoney(this SqliteCommand cmd, string name, decimal value)
        => cmd.Parameters.AddWithValue(name, (double)value);

    public static SqliteParameter AddBool(this SqliteCommand cmd, string name, bool value)
        => cmd.Parameters.AddWithValue(name, value ? 1 : 0);

    public static string ToDbDate(this DateTime value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);
}
