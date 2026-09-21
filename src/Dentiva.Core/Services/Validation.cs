using System.Globalization;
using System.Text.RegularExpressions;

namespace Dentiva.Core.Services;

/// <summary>
/// Input validation shared by the UI and the import pipeline. Messages are user-facing and actionable.
/// </summary>
public static partial class Validation
{
    public const int MaxNameLength = 160;
    public const int MaxNotesLength = 100_000;

    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^\+?[0-9][0-9\s\-().]{4,24}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    public static ValidationResult ValidateFullName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ValidationResult.Fail("The patient's full name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length < 2)
        {
            return ValidationResult.Fail("The full name must contain at least 2 characters.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            return ValidationResult.Fail($"The full name must not exceed {MaxNameLength} characters.");
        }

        return ValidationResult.Ok();
    }

    /// <summary>
    /// Accepts Bangladeshi mobile/landline formats and international numbers without blocking clinics
    /// that treat foreign patients. Empty input is allowed because the phone field is optional.
    /// </summary>
    public static ValidationResult ValidatePhone(string? phone, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return required ? ValidationResult.Fail("A phone number is required.") : ValidationResult.Ok();
        }

        var value = phone.Trim();
        if (!PhoneRegex().IsMatch(value))
        {
            return ValidationResult.Fail("Enter a valid phone number, for example 01712345678 or +8801712345678.");
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length < 6)
        {
            return ValidationResult.Fail("The phone number is too short to be valid.");
        }

        if (digits.Length > 15)
        {
            return ValidationResult.Fail("The phone number is longer than the international maximum of 15 digits.");
        }

        return ValidationResult.Ok();
    }

    public static string NormalisePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return string.Empty;
        }

        var value = phone.Trim();
        var hasPlus = value.StartsWith('+');
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return hasPlus ? "+" + digits : digits;
    }

    public static ValidationResult ValidateEmail(string? email, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return required ? ValidationResult.Fail("An email address is required.") : ValidationResult.Ok();
        }

        var value = email.Trim();
        if (value.Length > 254 || !EmailRegex().IsMatch(value))
        {
            return ValidationResult.Fail("Enter a valid email address, for example name@example.com.");
        }

        return ValidationResult.Ok();
    }

    public static ValidationResult ValidateDateOfBirth(DateTime? dob)
    {
        if (dob is null)
        {
            return ValidationResult.Ok();
        }

        if (dob.Value.Date > DateTime.Today)
        {
            return ValidationResult.Fail("The date of birth cannot be in the future.");
        }

        if (dob.Value.Year < 1900)
        {
            return ValidationResult.Fail("Enter a date of birth from 1900 onwards.");
        }

        return ValidationResult.Ok();
    }

    public static ValidationResult ValidateAmount(decimal amount, bool allowZero = true, decimal max = 99_999_999m)
    {
        if (amount < 0m)
        {
            return ValidationResult.Fail("The amount cannot be negative.");
        }

        if (!allowZero && amount == 0m)
        {
            return ValidationResult.Fail("The amount must be greater than zero.");
        }

        if (amount > max)
        {
            return ValidationResult.Fail($"The amount must not exceed {max:N0}.");
        }

        return ValidationResult.Ok();
    }

    public static ValidationResult ValidateNotes(string? notes)
    {
        if (!string.IsNullOrEmpty(notes) && notes.Length > MaxNotesLength)
        {
            return ValidationResult.Fail($"This note exceeds the maximum supported length of {MaxNotesLength:N0} characters.");
        }

        return ValidationResult.Ok();
    }

    public static bool TryParseDate(string? text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] formats =
        {
            "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "MM/dd/yyyy", "dd-MM-yyyy",
            "yyyy/MM/dd", "d MMM yyyy", "dd MMM yyyy", "yyyy-MM-ddTHH:mm:ss", "O"
        };

        if (DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            return true;
        }

        return DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    public static bool TryParseAmount(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = text.Trim().Replace(",", string.Empty).Replace("৳", string.Empty).Replace("BDT", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Strips characters that are illegal in Windows file names and prevents traversal segments.
    /// </summary>
    public static string SanitiseFileName(string? fileName, string fallback = "document")
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return fallback;
        }

        var name = Path.GetFileName(fileName.Trim());
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        name = name.Replace("..", "_").Trim(' ', '.');
        return string.IsNullOrWhiteSpace(name) ? fallback : name.Length > 120 ? name[..120] : name;
    }
}

public readonly record struct ValidationResult(bool IsValid, string? Message)
{
    public static ValidationResult Ok() => new(true, null);
    public static ValidationResult Fail(string message) => new(false, message);
}
