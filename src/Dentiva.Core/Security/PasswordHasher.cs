using System.Security.Cryptography;

namespace Dentiva.Core.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing with per-user random salt and constant-time verification.
/// </summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 210_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    public static (string Hash, string Salt, int Iterations) Hash(string password, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Password must not be empty.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
        return (Convert.ToBase64String(key), Convert.ToBase64String(salt), iterations);
    }

    public static bool Verify(string password, string hash, string salt, int iterations)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt))
        {
            return false;
        }

        try
        {
            var saltBytes = Convert.FromBase64String(salt);
            var expected = Convert.FromBase64String(hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static PasswordStrength Evaluate(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return PasswordStrength.Unacceptable;
        }

        var score = 0;
        if (password.Length >= 8) score++;
        if (password.Length >= 12) score++;
        if (password.Any(char.IsUpper) && password.Any(char.IsLower)) score++;
        if (password.Any(char.IsDigit)) score++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) score++;

        if (password.Length < 8)
        {
            return PasswordStrength.Unacceptable;
        }

        return score switch
        {
            <= 2 => PasswordStrength.Weak,
            3 => PasswordStrength.Fair,
            4 => PasswordStrength.Strong,
            _ => PasswordStrength.Excellent
        };
    }
}

public enum PasswordStrength
{
    Unacceptable,
    Weak,
    Fair,
    Strong,
    Excellent
}
