using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Infrastructure.Solutions;
using AgentRuntime.Solutions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentRuntime.Tests;

/// <summary>Builders' own sign-in tokens on the apps API (docs/solutions.md#your-own-sign-in).</summary>
public class EndUserJwtTests
{
    private sealed class Jwks(Func<string> body) : HttpMessageHandler
    {
        public int Fetches { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Fetches++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(), Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Jwt(object header, object payload, Func<byte[], byte[]> sign)
    {
        var h = B64(JsonSerializer.SerializeToUtf8Bytes(header));
        var p = B64(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{h}.{p}.{B64(sign(Encoding.ASCII.GetBytes($"{h}.{p}")))}";
    }

    private static readonly InstalledSolution Solution = new()
    {
        InstallId = "sol-1",
        TenantId = "t1",
        Status = SolutionInstallStatus.Active,
        Manifest = new SolutionManifest
        {
            Name = "app",
            Version = "1.0.0",
            EndUsers = new SolutionEndUsers { Jwt = new SolutionJwt { Issuer = "https://auth.example.com/", Audience = "my-app", JwksUrl = "https://auth.example.com/jwks" } }
        }
    };

    [Fact]
    public async Task Valid_tokens_identify_the_user_and_everything_else_is_refused()
    {
        using var rsa = RSA.Create(2048);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rp = rsa.ExportParameters(false);
        var ep = ec.ExportParameters(false);
        var keys = new JsonObject
        {
            ["keys"] = new JsonArray(
                new JsonObject { ["kty"] = "RSA", ["kid"] = "r1", ["use"] = "sig", ["n"] = B64(rp.Modulus!), ["e"] = B64(rp.Exponent!) },
                new JsonObject { ["kty"] = "EC", ["kid"] = "e1", ["crv"] = "P-256", ["x"] = B64(ep.Q.X!), ["y"] = B64(ep.Q.Y!) })
        };
        var jwks = new Jwks(() => keys.ToJsonString());
        var clock = new Clock(DateTimeOffset.UtcNow);
        var verifier = new EndUserJwtVerifier(new Factory(jwks), clock, NullLogger<EndUserJwtVerifier>.Instance);
        var exp = clock.Now.AddMinutes(10).ToUnixTimeSeconds();
        object Claims(string aud = "my-app", string iss = "https://auth.example.com/", long? expires = null) =>
            new { iss, aud, sub = "user-42", name = "Sam", exp = expires ?? exp };
        byte[] Rs(byte[] d) => rsa.SignData(d, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var claims = await verifier.VerifyAsync(Jwt(new { alg = "RS256", kid = "r1" }, Claims(), Rs), Solution, default);
        Assert.Equal(("t1", "sol-1", "user-42", "Sam"), (claims!.TenantId, claims.InstallId, claims.UserId, claims.Name));
        // ES256 and audience lists work too.
        var es = Jwt(new { alg = "ES256", kid = "e1" }, new { iss = "https://auth.example.com/", aud = new[] { "other", "my-app" }, sub = "u-2", exp },
            d => ec.SignData(d, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Equal("u-2", (await verifier.VerifyAsync(es, Solution, default))!.UserId);

        // Refused: wrong audience or issuer, expired, unsigned, symmetric, tampered, another key.
        Assert.Null(await verifier.VerifyAsync(Jwt(new { alg = "RS256", kid = "r1" }, Claims(aud: "someone-else"), Rs), Solution, default));
        Assert.Null(await verifier.VerifyAsync(Jwt(new { alg = "RS256", kid = "r1" }, Claims(iss: "https://evil.example.com/"), Rs), Solution, default));
        Assert.Null(await verifier.VerifyAsync(Jwt(new { alg = "RS256", kid = "r1" }, Claims(expires: clock.Now.AddMinutes(-5).ToUnixTimeSeconds()), Rs), Solution, default));
        Assert.Null(await verifier.VerifyAsync(Jwt(new { alg = "none" }, Claims(), _ => []), Solution, default));
        Assert.Null(await verifier.VerifyAsync(Jwt(new { alg = "HS256" }, Claims(), d => HMACSHA256.HashData("secret"u8.ToArray(), d)), Solution, default));
        var good = Jwt(new { alg = "RS256", kid = "r1" }, Claims(), Rs).Split('.');
        var forgedPayload = B64(JsonSerializer.SerializeToUtf8Bytes(new { iss = "https://auth.example.com/", aud = "my-app", sub = "admin", exp }));
        Assert.Null(await verifier.VerifyAsync($"{good[0]}.{forgedPayload}.{good[2]}", Solution, default));
        using var other = RSA.Create(2048);
        Assert.Null(await verifier.VerifyAsync(Jwt(new { alg = "RS256", kid = "r1" }, Claims(), d => other.SignData(d, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)), Solution, default));

        // Keys are cached; an unknown key id fetches again, but not more than once a minute.
        var fetched = jwks.Fetches;
        Assert.Null(await verifier.VerifyAsync(Jwt(new { alg = "RS256", kid = "rotated" }, Claims(), Rs), Solution, default));
        Assert.Null(await verifier.VerifyAsync(Jwt(new { alg = "RS256", kid = "rotated" }, Claims(), Rs), Solution, default));
        Assert.True(jwks.Fetches <= fetched + 1);
        clock.Now = clock.Now.AddMinutes(2);
        using var rotated = RSA.Create(2048);
        var np = rotated.ExportParameters(false);
        keys["keys"]!.AsArray().Add(new JsonObject { ["kty"] = "RSA", ["kid"] = "rotated", ["n"] = B64(np.Modulus!), ["e"] = B64(np.Exponent!) });
        var afterRotation = Jwt(new { alg = "RS256", kid = "rotated" }, Claims(expires: clock.Now.AddMinutes(10).ToUnixTimeSeconds()),
            d => rotated.SignData(d, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.NotNull(await verifier.VerifyAsync(afterRotation, Solution, default));
    }

    [Fact]
    public void LooksLikeJwt_ExcludesAktorTokensAndApiKeys()
    {
        Assert.True(EndUserJwtVerifier.LooksLikeJwt("a.b.c"));
        Assert.False(EndUserJwtVerifier.LooksLikeJwt("aeu_a.b"));
        Assert.False(EndUserJwtVerifier.LooksLikeJwt("ak_123"));
    }
}
