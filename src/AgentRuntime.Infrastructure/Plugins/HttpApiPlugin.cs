using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;

namespace AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// Any REST API behind a base URL and one auth header: a CRM, a store, a payments provider, a
/// ticketing system, an internal service. Agents choose only the path and query; the host, scheme and
/// credentials are fixed by the connection, so an agent can't point the credential elsewhere.
/// Reads are a ReadOnly tool; writes are a separate tool that exists only when the user enabled
/// them, and send an Idempotency-Key header (honoured by many APIs). Endpoints added by hand or
/// imported from OpenAPI (<see cref="HttpApiEndpoints"/>) become typed tools beside the general ones.
/// </summary>
public sealed class HttpApiPlugin(IHttpClientFactory httpClientFactory) : IToolProviderPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = "http-api",
        Name = "HTTP API",
        Description = "Let agents call any REST API (CRM, store, payments, ticketing, your own service) with a stored credential.",
        Category = PluginCategory.Data,
        Settings =
        [
            new() { Key = "base_url", Label = "Base URL", Required = true, Placeholder = "https://api.example.com/v1" },
            new() { Key = "description", Label = "What this API is (shown to agents)", Placeholder = "Our CRM's REST API: contacts, deals, activities" },
            new() { Key = "auth_header_name", Label = "Auth header name", DefaultValue = "Authorization", Placeholder = "Authorization or X-Api-Key" },
            new() { Key = "auth_header_value", Label = "Auth header value", Secret = true },
            new() { Key = "allow_writes", Label = "Allow writes (POST/PUT/PATCH/DELETE)", Options = ["false", "true"], DefaultValue = "false" },
            new()
            {
                Key = HttpApiEndpoints.SettingKey, Label = "Endpoints",
                Description = "Optional. The API's operations as a JSON array, added by hand or imported from an OpenAPI document; each becomes a typed tool."
            }
        ],
        SetupHelp = "Use a token with the narrowest permissions that do the job (read-only unless agents must write). " +
                    "Most APIs take 'Authorization: Bearer <token>'; some use their own header (e.g. X-Api-Key, or " +
                    "X-Shopify-Access-Token for Shopify)."
    };

    public async Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken)
    {
        HttpApiEndpoints.Parse(connection.Setting(HttpApiEndpoints.SettingKey), out var problems);
        if (problems.Count > 0) return ConnectionCheck.Failure("Endpoints: " + string.Join(" ", problems.Take(5)));

        var baseUri = BaseUri(connection);
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUri);
        AddAuth(request, connection);
        using var response = await httpClientFactory.CreateClient("integrations").SendAsync(request, cancellationToken);
        return (int)response.StatusCode is 401 or 403
            ? ConnectionCheck.Failure($"The API rejected the credential ({(int)response.StatusCode}).")
            : ConnectionCheck.Success($"Reached {baseUri.Host} ({(int)response.StatusCode}).");
    }

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken)
    {
        var about = connection.Setting("description", BaseUri(connection).Host);
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "get",
                Description = $"GET a path on {about} (base {BaseUri(connection)}). Returns status and body.",
                SideEffects = ToolSideEffects.ReadOnly,
                JsonSchema = """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Relative to the base URL, e.g. /products.json" },
                    "query": { "type": "object", "additionalProperties": { "type": "string" } }
                  },
                  "required": ["path"]
                }
                """
            }
        };

        if (connection.Setting("allow_writes", "false") == "true")
        {
            tools.Add(new ToolDefinition
            {
                Name = "send",
                Description = $"POST/PUT/PATCH/DELETE on {about}. Changes real data — use deliberately.",
                SideEffects = ToolSideEffects.NonIdempotent,
                JsonSchema = """
                {
                  "type": "object",
                  "properties": {
                    "method": { "type": "string", "enum": ["POST", "PUT", "PATCH", "DELETE"] },
                    "path": { "type": "string" },
                    "body": { "type": "object", "description": "JSON body" }
                  },
                  "required": ["method", "path"]
                }
                """
            });
        }

        // The connection's own endpoints, as typed tools. Writes are offered only when writes are allowed.
        var writes = connection.Setting("allow_writes", "false") == "true";
        foreach (var e in Endpoints(connection).Where(e => writes || !HttpApiEndpoints.IsWrite(e.Method)))
        {
            tools.Add(new ToolDefinition
            {
                Name = e.Name,
                Description = HttpApiEndpoints.DescriptionOf(e, about),
                SideEffects = HttpApiEndpoints.SideEffectsOf(e.Method),
                JsonSchema = HttpApiEndpoints.SchemaOf(e)
            });
        }

        return Task.FromResult<IReadOnlyList<ToolDefinition>>(tools);
    }

    private static IReadOnlyList<ApiEndpoint> Endpoints(PluginConnection connection) =>
        HttpApiEndpoints.Parse(connection.Setting(HttpApiEndpoints.SettingKey), out _);

    public async Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request)
    {
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson);
        var root = args.RootElement;
        if (toolName is not ("get" or "send") && Endpoints(connection).FirstOrDefault(e => e.Name == toolName) is { } endpoint)
        {
            return await ExecuteEndpointAsync(connection, endpoint, root, request);
        }

        var path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/";

        HttpMethod method;
        switch (toolName)
        {
            case "get":
                method = HttpMethod.Get;
                break;
            case "send" when connection.Setting("allow_writes", "false") == "true":
                var m = root.TryGetProperty("method", out var mv) ? mv.GetString()?.ToUpperInvariant() : null;
                if (m is not ("POST" or "PUT" or "PATCH" or "DELETE")) return ToolExecutionResult.Fail("method must be POST, PUT, PATCH or DELETE.");
                method = new HttpMethod(m);
                break;
            default:
                return ToolExecutionResult.Fail($"Unknown or disabled tool '{toolName}'.");
        }

        if (!TryResolve(connection, path, root.TryGetProperty("query", out var q) ? q : default, out var uri, out var error))
        {
            return ToolExecutionResult.Fail(error);
        }

        return await SendAsync(connection, method, uri, root, request);
    }

    private async Task<ToolExecutionResult> ExecuteEndpointAsync(PluginConnection connection, ApiEndpoint endpoint, JsonElement args, ToolExecutionRequest request)
    {
        if (HttpApiEndpoints.IsWrite(endpoint.Method) && connection.Setting("allow_writes", "false") != "true")
        {
            return ToolExecutionResult.Fail($"'{endpoint.Name}' writes, and this connection doesn't allow writes.");
        }

        if (!HttpApiEndpoints.TryBuild(endpoint, args, out var path, out var query, out var error)) return ToolExecutionResult.Fail(error);
        using var queryDoc = JsonDocument.Parse(query.ToJsonString());
        if (!TryResolve(connection, path, queryDoc.RootElement, out var uri, out error)) return ToolExecutionResult.Fail(error);
        return await SendAsync(connection, new HttpMethod(endpoint.Method), uri, args, request);
    }

    private async Task<ToolExecutionResult> SendAsync(PluginConnection connection, HttpMethod method, Uri uri, JsonElement args, ToolExecutionRequest request)
    {
        using var httpRequest = new HttpRequestMessage(method, uri);
        AddAuth(httpRequest, connection);
        if (method != HttpMethod.Get)
        {
            if (!string.IsNullOrEmpty(request.IdempotencyKey)) httpRequest.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Null)
            {
                httpRequest.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
            }
        }

        using var response = await httpClientFactory.CreateClient("integrations").SendAsync(httpRequest, request.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(request.CancellationToken);
        var payload = JsonSerializer.Serialize(new { status = (int)response.StatusCode, body = Body(text) });
        return response.IsSuccessStatusCode ? ToolExecutionResult.Ok(payload) : ToolExecutionResult.Fail(payload);
    }

    /// <summary>A JSON response stays JSON (not a string of escaped JSON, which costs tokens to read); anything else is text.</summary>
    private static object Body(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                return JsonDocument.Parse(text).RootElement.Clone();
            }
            catch (JsonException)
            {
                // Not JSON after all.
            }
        }

        return text;
    }

    /// <summary>Resolves an agent-supplied relative path against the base URL, refusing anything
    /// that would leave it (absolute URLs, other hosts, "../" above the base path).</summary>
    public static bool TryResolve(PluginConnection connection, string path, JsonElement query, out Uri uri, out string error)
    {
        uri = null!;
        error = string.Empty;
        var baseUri = BaseUri(connection);

        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" || path.StartsWith("//"))
        {
            error = "Use a path relative to the connection's base URL, not a full URL.";
            return false;
        }

        // Encoded dot segments and backslashes can be decoded by the server after our check.
        if (path.Contains("%2e", StringComparison.OrdinalIgnoreCase) || path.Contains("%2f", StringComparison.OrdinalIgnoreCase) || path.Contains('\\'))
        {
            error = "Encoded path separators aren't allowed.";
            return false;
        }

        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var candidate = new Uri(baseUri, basePath + "/" + path.TrimStart('/'));
        if (candidate.Host != baseUri.Host || candidate.Scheme != baseUri.Scheme || candidate.Port != baseUri.Port ||
            !candidate.AbsolutePath.StartsWith(basePath, StringComparison.Ordinal))
        {
            error = "That path leaves the connection's base URL.";
            return false;
        }

        if (query.ValueKind == JsonValueKind.Object)
        {
            var qs = string.Join("&", query.EnumerateObject().Select(kv =>
                $"{Uri.EscapeDataString(kv.Name)}={Uri.EscapeDataString(kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString()! : kv.Value.GetRawText())}"));
            if (qs.Length > 0) candidate = new UriBuilder(candidate) { Query = qs }.Uri;
        }

        uri = candidate;
        return true;
    }

    private static Uri BaseUri(PluginConnection connection)
    {
        var raw = connection.Setting("base_url");
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("base_url must be an absolute http(s) URL.");
        }

        return uri;
    }

    private static void AddAuth(HttpRequestMessage request, PluginConnection connection)
    {
        if (!connection.Secrets.TryGetValue("auth_header_value", out var value) || string.IsNullOrWhiteSpace(value)) return;
        var name = connection.Setting("auth_header_name", "Authorization");
        if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && AuthenticationHeaderValue.TryParse(value, out var auth))
        {
            request.Headers.Authorization = auth;
        }
        else
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        request.Headers.Accept.ParseAdd("application/json");
    }
}
