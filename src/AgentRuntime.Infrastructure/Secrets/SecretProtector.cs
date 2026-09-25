using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Secrets;

public sealed class SecretsOptions
{
    public const string SectionName = "Secrets";

    /// <summary>Base64 256-bit key (e.g. <c>openssl rand -base64 32</c>). Set it in production and
    /// keep it safe: without it the stored connection secrets can't be decrypted.</summary>
    public string? MasterKey { get; set; }

    /// <summary>Used only when <see cref="MasterKey"/> isn't set: the key is generated once and
    /// kept in this file (mount it on a persistent volume).</summary>
    public string KeyFile { get; set; } = "./keys/secrets-master.key";
}

/// <summary>
/// AES-256-GCM encryption for connection secrets. Each value is bound to its scope and key as
/// associated data, so an encrypted row can't be copied onto another connection or field and
/// still decrypt. Stored form: 12-byte nonce | 16-byte tag | ciphertext.
/// </summary>
public sealed class SecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public SecretProtector(IOptions<SecretsOptions> options, ILogger<SecretProtector> logger)
        : this(LoadOrCreateKey(options.Value, logger))
    {
    }

    public SecretProtector(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("The secrets master key must be 32 bytes (256 bits).");
        _key = key;
    }

    public byte[] Protect(string plaintext, string scope, string name)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, cipher, tag, AssociatedData(scope, name));
        return [.. nonce, .. tag, .. cipher];
    }

    public string Unprotect(byte[] stored, string scope, string name)
    {
        if (stored.Length < NonceSize + TagSize) throw new CryptographicException("Stored secret is corrupt.");
        var nonce = stored.AsSpan(0, NonceSize);
        var tag = stored.AsSpan(NonceSize, TagSize);
        var cipher = stored.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain, AssociatedData(scope, name));
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] AssociatedData(string scope, string name) => Encoding.UTF8.GetBytes($"aktor-secret\n{scope}\n{name}");

    private static byte[] LoadOrCreateKey(SecretsOptions options, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.MasterKey))
        {
            return Convert.FromBase64String(options.MasterKey.Trim());
        }

        var path = Path.GetFullPath(options.KeyFile);
        if (File.Exists(path))
        {
            return Convert.FromBase64String(File.ReadAllText(path).Trim());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToBase64String(key));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        logger.LogWarning("No Secrets:MasterKey configured; generated one at {Path}. Back it up, or set Secrets__MasterKey " +
                          "in production — without it, stored connection secrets can't be decrypted.", path);
        return key;
    }
}
