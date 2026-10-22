using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRuntime.Infrastructure.Secrets;

namespace AgentRuntime.Infrastructure.Solutions;

/// <summary>What a records connection is bound to: set by the runtime when it creates the
/// connection, signed, so nobody can point a connection at another organization's or end user's
/// records by editing its settings.</summary>
public sealed record RecordsBinding(
    [property: JsonPropertyName("t")] string TenantId,
    [property: JsonPropertyName("i")] string InstallId,
    [property: JsonPropertyName("u")] string? EndUserId,
    /// <summary>Entity → "read" / "write".</summary>
    [property: JsonPropertyName("a")] Dictionary<string, string> Access);

/// <summary>Who an end-user token was issued to, by the builder's backend (docs/solutions.md#end-users).</summary>
public sealed record EndUserClaims(
    [property: JsonPropertyName("t")] string TenantId,
    [property: JsonPropertyName("i")] string InstallId,
    [property: JsonPropertyName("u")] string UserId,
    [property: JsonPropertyName("n")] string? Name,
    [property: JsonPropertyName("exp")] long ExpiresAt);

/// <summary>
/// Signs and checks the two credentials solutions add: records bindings and end-user tokens.
/// Both are HMACs under keys derived from the secrets master key, each for its own purpose.
/// </summary>
public sealed class SolutionTokens(SecretProtector protector, TimeProvider clock)
{
    public const string EndUserPrefix = "aeu_";
    private const string BindingPurpose = "solution-records-binding/v1";
    private const string EndUserPurpose = "solution-end-user-token/v1";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string SignBinding(RecordsBinding binding)
    {
        var payload = Base64Url.Encode(JsonSerializer.Serialize(binding, Json));
        return $"v1.{payload}.{Base64Url.Encode(protector.Sign(BindingPurpose, payload))}";
    }

    public RecordsBinding? VerifyBinding(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not ["v1", var payload, var signature]) return null;
        if (!protector.Verify(BindingPurpose, payload, Decode(signature))) return null;
        try
        {
            return JsonSerializer.Deserialize<RecordsBinding>(Base64Url.Decode(payload), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string IssueEndUserToken(string tenantId, string installId, string userId, string? name, TimeSpan lifetime)
    {
        var claims = new EndUserClaims(tenantId, installId, userId, name, clock.GetUtcNow().Add(lifetime).ToUnixTimeSeconds());
        var payload = Base64Url.Encode(JsonSerializer.Serialize(claims, Json));
        return $"{EndUserPrefix}{payload}.{Base64Url.Encode(protector.Sign(EndUserPurpose, payload))}";
    }

    /// <summary>The claims of a valid, unexpired token; null otherwise.</summary>
    public EndUserClaims? VerifyEndUserToken(string? token)
    {
        if (token is null || !token.StartsWith(EndUserPrefix, StringComparison.Ordinal)) return null;
        var parts = token[EndUserPrefix.Length..].Split('.');
        if (parts is not [var payload, var signature]) return null;
        if (!protector.Verify(EndUserPurpose, payload, Decode(signature))) return null;
        try
        {
            var claims = JsonSerializer.Deserialize<EndUserClaims>(Base64Url.Decode(payload), Json);
            return claims is not null && claims.ExpiresAt > clock.GetUtcNow().ToUnixTimeSeconds() ? claims : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    private static byte[] Decode(string value)
    {
        try
        {
            return Base64Url.DecodeBytes(value);
        }
        catch (FormatException)
        {
            return [];
        }
    }
}

/// <summary>App keys look like <c>aks_{keyId}_{secret}</c>, like API keys (<c>ak_…</c>) but for one
/// solution's apps API only: the id finds the row, the secret is checked against its hash.</summary>
public static class AppKeyFormat
{
    public const string Prefix = "aks_";

    public static (string KeyId, string FullKey) New() => Identity.ApiKeyFormat.New(Prefix);

    public static bool LooksLike(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static bool TryParse(string? value, out string keyId) => Identity.ApiKeyFormat.TryParse(Prefix, value, out keyId);

    /// <summary>What lists show: enough to recognise a key, never enough to use it.</summary>
    public static string Display(string fullKey) => Identity.ApiKeyFormat.Display(Prefix, fullKey);
}

/// <summary>Base64url without padding (RFC 4648 §5).</summary>
internal static class Base64Url
{
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

    public static byte[] DecodeBytes(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    public static string Decode(string value) => Encoding.UTF8.GetString(DecodeBytes(value));
}
