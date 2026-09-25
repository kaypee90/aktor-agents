using System.Security.Cryptography;
using System.Text;

namespace AgentRuntime.Infrastructure.Identity;

/// <summary>PBKDF2-HMAC-SHA256 password hashes, stored as <c>pbkdf2-sha256$iterations$salt$hash</c>.</summary>
public static class PasswordHasher
{
    public const int Iterations = 600_000; // OWASP's current recommendation for PBKDF2-SHA256
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public const int MinLength = 10;
    public const int MaxLength = 256;

    public static string Hash(string password, int iterations = Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2-sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations) || iterations < 1)
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static bool NeedsRehash(string stored) =>
        stored.Split('$') is [_, var it, _, _] && int.TryParse(it, out var iterations) && iterations < Iterations;

    /// <summary>Why a password is unacceptable, or null.</summary>
    public static string? Validate(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinLength ? $"Use a password of at least {MinLength} characters."
        : password.Length > MaxLength ? $"Passwords can be at most {MaxLength} characters."
        : null;
}

/// <summary>Random bearer secrets (sessions, API keys, invitations). Only their SHA-256 is stored,
/// so a database leak doesn't hand out working credentials.</summary>
public static class SecretTokens
{
    public static string New(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static bool HashEquals(string token, string storedHash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(storedHash));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>API keys look like <c>ak_{keyId}_{secret}</c>: the id finds the row, the secret is checked
/// against its hash in constant time.</summary>
public static class ApiKeyFormat
{
    public const string Prefix = "ak_";

    public static (string KeyId, string FullKey) New()
    {
        var keyId = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        return (keyId, $"{Prefix}{keyId}_{SecretTokens.New()}");
    }

    public static bool TryParse(string? value, out string keyId)
    {
        keyId = string.Empty;
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = value[Prefix.Length..];
        var sep = rest.IndexOf('_');
        if (sep != 12 || rest.Length < sep + 20) return false;
        keyId = rest[..sep];
        return keyId.All(char.IsAsciiHexDigitLower);
    }

    /// <summary>What lists show: enough to recognise a key, never enough to use it.</summary>
    public static string Display(string fullKey) => fullKey[..Math.Min(fullKey.Length, Prefix.Length + 12 + 5)] + "…";
}
