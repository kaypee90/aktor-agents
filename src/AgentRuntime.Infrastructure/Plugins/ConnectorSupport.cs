using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;

namespace AgentRuntime.Infrastructure.Plugins;

/// <summary>Shared plumbing for the SaaS connectors (Google Sheets, BigQuery, Airtable, Zoho CRM):
/// reading a tool's arguments and calling a JSON API.</summary>
internal static class Connector
{
    public static JsonObject Args(ToolExecutionRequest request)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string? Str(JsonObject args, string key) =>
        args[key] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : null;

    public static string Required(JsonObject args, string key) =>
        Str(args, key) is { Length: > 0 } s ? s : throw new ArgumentException($"'{key}' is required.");

    public static int Int(JsonObject args, string key, int fallback, int min, int max)
    {
        var value = args[key] is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<string>(out var s) && int.TryParse(s, out i))) ? i : fallback;
        return Math.Clamp(value, min, max);
    }

    public static bool Bool(JsonObject args, string key, bool fallback) =>
        args[key] is JsonValue v ? (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) ? s is "true" or "yes" : fallback) : fallback;

    public static bool WritesAllowed(PluginConnection connection) => connection.Setting("allow_writes", "false") == "true";

    /// <summary>Sends a JSON request; the response body comes back as JSON when it is JSON.</summary>
    public static async Task<(int Status, JsonNode? Body, string Text)> SendAsync(HttpClient http, HttpMethod method, string url,
        Action<HttpRequestMessage> authorize, JsonNode? body, CancellationToken ct, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, url);
        authorize(request);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (!string.IsNullOrEmpty(idempotencyKey)) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? parsed = null;
        if (text.TrimStart() is ['{', ..] or ['[', ..])
        {
            try { parsed = JsonNode.Parse(text); } catch (JsonException) { /* not JSON after all */ }
        }

        return ((int)response.StatusCode, parsed, text);
    }

    /// <summary>A failed call, with the service's own error message when it gave one.</summary>
    public static ToolExecutionResult Fail(string service, int status, JsonNode? body, string text)
    {
        var message = At(body, "error", "message")      // Google, Airtable
                      ?? At(body, "error", "type")      // Airtable (no message)
                      ?? At(body, "error")              // OAuth errors
                      ?? At(body, "message")            // Zoho
                      ?? (body is JsonObject o && o["data"] is JsonArray { Count: > 0 } d ? At(d[0], "message") : null) // Zoho per-record
                      ?? (text.Length > 300 ? text[..300] : text);
        var hint = status switch
        {
            401 => " Check the connection's credential.",
            403 => " The credential lacks permission (for Google, share the sheet or dataset with the service account's email).",
            404 => " Check the ids and names.",
            429 => " The service is rate-limiting; try again shortly.",
            _ => ""
        };
        return ToolExecutionResult.Fail($"{service} answered {status}: {message}.{hint}");
    }

    /// <summary>A value by property path, as text; null when any step isn't an object with that property.</summary>
    public static string? At(JsonNode? node, params string[] path)
    {
        foreach (var key in path)
        {
            if (node is not JsonObject o || !o.TryGetPropertyValue(key, out node)) return null;
        }

        return node switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => node.ToJsonString()
        };
    }

    public static ToolExecutionResult Ok(JsonNode? node) => ToolExecutionResult.Ok(node?.ToJsonString() ?? "null");

    public static ToolDefinition Tool(string name, string description, ToolSideEffects sideEffects, string schema) =>
        new() { Name = name, Description = description, SideEffects = sideEffects, JsonSchema = schema };

    /// <summary>A plugin call: argument mistakes become failures the caller can read, not exceptions.</summary>
    public static async Task<ToolExecutionResult> Guard(Func<Task<ToolExecutionResult>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (IsUserError(ex))
        {
            return ToolExecutionResult.Fail(ex.Message);
        }
    }

    /// <summary>Mistakes in arguments or the connection's settings, as opposed to bugs.</summary>
    public static bool IsUserError(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or CryptographicException or FormatException;

    /// <summary>A connection check that reports bad settings as a failed check.</summary>
    public static async Task<ConnectionCheck> Check(Func<Task<ConnectionCheck>> check)
    {
        try
        {
            return await check();
        }
        catch (Exception ex) when (IsUserError(ex))
        {
            return ConnectionCheck.Failure(ex.Message);
        }
    }
}

/// <summary>
/// Access tokens for Google service accounts (Sheets, BigQuery): a JWT signed with the account's
/// private key is exchanged for an hour-long token, cached here per account and scope. The key
/// itself comes from the connection's secret for each call and is never kept.
/// </summary>
public sealed class GoogleServiceAccountTokens(IHttpClientFactory httpClientFactory)
{
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _cache = new();

    public sealed record ServiceAccount(string ClientEmail, string PrivateKey, string TokenUri, string? ProjectId);

    public static ServiceAccount Parse(string json)
    {
        JsonObject? key;
        try
        {
            key = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            key = null;
        }

        if (key?["client_email"]?.ToString() is not { Length: > 0 } email || key["private_key"]?.ToString() is not { Length: > 0 } privateKey)
        {
            throw new ArgumentException("The service account key must be the JSON key file from Google Cloud (with client_email and private_key).");
        }

        return new ServiceAccount(email, privateKey, key["token_uri"]?.ToString() ?? "https://oauth2.googleapis.com/token", key["project_id"]?.ToString());
    }

    public async Task<string> GetTokenAsync(ServiceAccount account, string scope, CancellationToken ct)
    {
        var cacheKey = $"{account.ClientEmail}|{scope}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.PrivateKey)))[..16]}";
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(2)) return cached.Token;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = account.ClientEmail, scope, aud = account.TokenUri, iat = now, exp = now + 3600 }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(account.PrivateKey);
        var signature = Base64Url(rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        using var response = await httpClientFactory.CreateClient("integrations").PostAsync(account.TokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = $"{header}.{claims}.{signature}"
        }), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? body;
        try { body = JsonNode.Parse(text); } catch (JsonException) { body = null; }
        if (!response.IsSuccessStatusCode || Connector.At(body, "access_token") is not { Length: > 0 } token)
        {
            throw new ArgumentException($"Google refused the service account key: {Connector.At(body, "error_description") ?? Connector.At(body, "error") ?? ((int)response.StatusCode).ToString()}.");
        }

        var expires = DateTimeOffset.UtcNow.AddSeconds(int.TryParse(Connector.At(body, "expires_in"), out var seconds) ? seconds : 3600);
        _cache[cacheKey] = (token, expires);
        return token;
    }

    public static Action<HttpRequestMessage> Bearer(string token) => r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
