using System.Security.Cryptography;
using System.Text;

namespace AgentRuntime.Contracts;

/// <summary>
/// Ids derived from an idempotency key, so a step replayed after a crash produces the same id
/// (the same spawned agent, the same message) instead of a duplicate.
/// </summary>
public static class DeterministicId
{
    public static string From(string prefix, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return prefix + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>A fresh random id when there is no key (a first, non-replayable call).</summary>
    public static string FromOrNew(string prefix, string? key) =>
        string.IsNullOrEmpty(key) ? prefix + Guid.NewGuid().ToString("n")[..12] : From(prefix, key);
}
