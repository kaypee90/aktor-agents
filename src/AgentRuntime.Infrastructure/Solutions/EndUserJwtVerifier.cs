using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Solutions;
using Microsoft.Extensions.Logging;

namespace AgentRuntime.Infrastructure.Solutions;

/// <summary>
/// Checks a builder's own sign-in tokens (docs/solutions.md#your-own-sign-in): a JWT signed with
/// one of the keys the builder's identity provider publishes (JWKS), for the configured issuer and
/// audience, unexpired. RS256/384/512, PS256/384/512 and ES256/384/512; "none" and symmetric keys
/// are refused, since anyone holding a shared secret could forge any user. Keys are cached for ten
/// minutes and fetched again (at most once a minute) when a token names a key not seen yet, so a
/// key rotation is picked up without a restart.
/// </summary>
public sealed class EndUserJwtVerifier(IHttpClientFactory httpFactory, TimeProvider clock, ILogger<EndUserJwtVerifier> logger)
{
    public static readonly TimeSpan KeyCacheTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    private sealed record KeySet(List<JsonObject> Keys, DateTimeOffset FetchedAt);

    private readonly ConcurrentDictionary<string, KeySet> _keys = new();

    /// <summary>Whether a token looks like a JWT (three dot-separated parts).</summary>
    public static bool LooksLikeJwt(string? token) => token is not null && token.Count(c => c == '.') == 2 && !token.StartsWith(SolutionTokens.EndUserPrefix, StringComparison.Ordinal);

    /// <summary>The end user the token identifies, or null when it isn't valid for this solution.</summary>
    public async Task<EndUserClaims?> VerifyAsync(string token, InstalledSolution solution, CancellationToken ct)
    {
        if (solution.Manifest.EndUsers?.Jwt is not { } config) return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        JsonObject header, payload;
        byte[] signature;
        try
        {
            header = JsonNode.Parse(Base64Url.Decode(parts[0])) as JsonObject ?? throw new FormatException();
            payload = JsonNode.Parse(Base64Url.Decode(parts[1])) as JsonObject ?? throw new FormatException();
            signature = Base64Url.DecodeBytes(parts[2]);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }

        var alg = Str(header["alg"]);
        if (alg is null || !(alg.StartsWith("RS") || alg.StartsWith("PS") || alg.StartsWith("ES"))) return null;
        var kid = Str(header["kid"]);
        var key = await FindKeyAsync(config.JwksUrl, kid, alg, ct);
        if (key is null || !VerifySignature(key, alg, Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), signature)) return null;

        var now = clock.GetUtcNow();
        if (Str(payload["iss"]) != config.Issuer) return null;
        var audiences = payload["aud"] switch
        {
            JsonArray a => a.Select(Str).OfType<string>().ToList(),
            var single => Str(single) is { } s ? [s] : []
        };
        if (!audiences.Contains(config.Audience)) return null;
        if (Num(payload["exp"]) is not { } exp || DateTimeOffset.FromUnixTimeSeconds(exp) + ClockSkew < now) return null;
        if (Num(payload["nbf"]) is { } nbf && DateTimeOffset.FromUnixTimeSeconds(nbf) - ClockSkew > now) return null;
        var user = Str(payload[config.UserClaim]);
        if (string.IsNullOrWhiteSpace(user) || user.Length > 200 || user.Contains(':')) return null;
        var name = config.NameClaim is { } n ? Str(payload[n]) : null;
        return new EndUserClaims(solution.TenantId, solution.InstallId, user, name is { Length: > 100 } ? name[..100] : name, exp);
    }

    private async Task<JsonObject?> FindKeyAsync(string jwksUrl, string? kid, string alg, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        _keys.TryGetValue(jwksUrl, out var set);
        JsonObject? Pick(KeySet? s) => s?.Keys.FirstOrDefault(k =>
            (kid is null || Str(k["kid"]) == kid) &&
            (Str(k["alg"]) is null || Str(k["alg"]) == alg) &&
            Str(k["use"]) is null or "sig" &&
            Str(k["kty"]) == (alg.StartsWith("ES") ? "EC" : "RSA"));

        if (set is not null && now - set.FetchedAt < KeyCacheTtl && Pick(set) is { } cached) return cached;
        // Unknown key or stale cache: fetch, but not more than once a minute (a flood of tokens
        // naming made-up keys mustn't turn into a flood of requests to the provider).
        if (set is null || now - set.FetchedAt > TimeSpan.FromMinutes(1))
        {
            try
            {
                using var response = await httpFactory.CreateClient("integrations").GetAsync(jwksUrl, ct);
                response.EnsureSuccessStatusCode();
                var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
                set = new KeySet(doc?["keys"]?.AsArray().OfType<JsonObject>().ToList() ?? [], now);
                _keys[jwksUrl] = set;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                logger.LogWarning("Couldn't fetch end-user signing keys from {Url}: {Error}", jwksUrl, ex.Message);
            }
        }

        return Pick(set);
    }

    private static bool VerifySignature(JsonObject jwk, string alg, byte[] data, byte[] signature)
    {
        var hash = alg[2..] switch
        {
            "256" => HashAlgorithmName.SHA256,
            "384" => HashAlgorithmName.SHA384,
            "512" => HashAlgorithmName.SHA512,
            _ => default
        };
        if (hash == default) return false;
        try
        {
            if (alg.StartsWith("ES"))
            {
                var curve = Str(jwk["crv"]) switch
                {
                    "P-256" => ECCurve.NamedCurves.nistP256,
                    "P-384" => ECCurve.NamedCurves.nistP384,
                    "P-521" => ECCurve.NamedCurves.nistP521,
                    _ => throw new CryptographicException("Unsupported curve.")
                };
                using var ec = ECDsa.Create(new ECParameters
                {
                    Curve = curve,
                    Q = new ECPoint { X = Base64Url.DecodeBytes(Str(jwk["x"])!), Y = Base64Url.DecodeBytes(Str(jwk["y"])!) }
                });
                return ec.VerifyData(data, signature, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }

            using var rsa = RSA.Create(new RSAParameters { Modulus = Base64Url.DecodeBytes(Str(jwk["n"])!), Exponent = Base64Url.DecodeBytes(Str(jwk["e"])!) });
            return rsa.VerifyData(data, signature, hash, alg.StartsWith("PS") ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or NullReferenceException)
        {
            return false;
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Num(JsonNode? node) =>
        node is JsonValue && node.GetValueKind() == JsonValueKind.Number &&
        decimal.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? (long)n : null;
}
