using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;

namespace AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// Zoho CRM through a self-client's refresh token: search and read records of any module (Leads,
/// Contacts, Deals…), and, when writes are allowed, create, update and upsert them. Access tokens
/// are refreshed as needed and cached per connection credential; the API host comes from Zoho's
/// token response, so every data center works.
/// </summary>
public sealed class ZohoCrmPlugin(IHttpClientFactory httpClientFactory) : IToolProviderPlugin
{
    private const int Batch = 100;

    /// <summary>Each data center's accounts (token) server. Not all follow accounts.zoho.&lt;dc&gt;:
    /// Canada's is on zohocloud.ca.</summary>
    internal static readonly IReadOnlyDictionary<string, string> AccountsHosts = new Dictionary<string, string>
    {
        ["com"] = "https://accounts.zoho.com",
        ["eu"] = "https://accounts.zoho.eu",
        ["in"] = "https://accounts.zoho.in",
        ["com.au"] = "https://accounts.zoho.com.au",
        ["jp"] = "https://accounts.zoho.jp",
        ["ca"] = "https://accounts.zohocloud.ca",
        ["sa"] = "https://accounts.zoho.sa",
        ["com.cn"] = "https://accounts.zoho.com.cn"
    };
    private readonly ConcurrentDictionary<string, (string Token, string ApiDomain, DateTimeOffset Expires)> _tokens = new();

    public PluginManifest Manifest { get; } = new()
    {
        Id = "zoho-crm",
        Name = "Zoho CRM",
        Description = "Search and read Zoho CRM records (leads, contacts, deals, any module) and (optionally) create, update or upsert them.",
        Category = PluginCategory.Data,
        Settings =
        [
            new() { Key = "data_center", Label = "Data center", Options = ["com", "eu", "in", "com.au", "jp", "ca", "sa", "com.cn"], DefaultValue = "com",
                    Description = "Where your Zoho account lives, as in your API Console's address: api-console.zoho.com → com, .eu → eu, api-console.zohocloud.ca → ca…" },
            new() { Key = "api_version", Label = "API version", DefaultValue = "v8" },
            new() { Key = "allow_writes", Label = "Allow writes (create, update, upsert)", Options = ["false", "true"], DefaultValue = "false" },
            new() { Key = "client_id", Label = "Client id", Secret = true, Required = true },
            new() { Key = "client_secret", Label = "Client secret", Secret = true, Required = true },
            new() { Key = "refresh_token", Label = "Refresh token", Secret = true, Required = true }
        ],
        SetupHelp = "In the Zoho API Console add a Self Client (it shows the client id and secret). On its Generate Code tab, create a grant code " +
                    "with the scopes ZohoCRM.modules.ALL,ZohoCRM.settings.READ (ZohoCRM.modules.READ to only read), then within its time limit exchange it " +
                    "for a refresh token: POST <accounts server>/oauth/v2/token with grant_type=authorization_code, client_id, client_secret and code. " +
                    "The accounts server matches your data center: accounts.zoho.com, accounts.zoho.eu, accounts.zohocloud.ca (Canada)…"
    };

    public Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken) => Connector.Check(async () =>
    {
        var (baseUrl, auth) = await PrepareAsync(connection, cancellationToken);
        var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, $"{baseUrl}/settings/modules", auth, null, cancellationToken);
        return status == 200
            ? ConnectionCheck.Success($"Reached Zoho CRM ({(body?["modules"] as JsonArray)?.Count ?? 0} modules).")
            : ConnectionCheck.Failure(Connector.Fail("Zoho CRM", status, body, text).ErrorMessage!);
    });

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken)
    {
        const string module = """ "module": { "type": "string", "description": "The module's API name: Leads, Contacts, Accounts, Deals, or a custom one." } """;
        const string fields = """ "fields": { "type": "string", "description": "Comma-separated field API names, e.g. Last_Name,Email,Phone." } """;
        const string paging = """ "page": { "type": "integer" }, "per_page": { "type": "integer", "description": "At most 200." } """;
        var tools = new List<ToolDefinition>
        {
            Connector.Tool("list_modules", "The CRM's modules (API names to use in other tools).", ToolSideEffects.ReadOnly, """{ "type": "object", "properties": {} }"""),
            Connector.Tool("list_fields", "A module's fields: API name, label, type, whether required.", ToolSideEffects.ReadOnly,
                $$"""{ "type": "object", "properties": { {{module}} }, "required": ["module"] }"""),
            Connector.Tool("search_records", "Find records by criteria, email, phone or a word.", ToolSideEffects.ReadOnly, $$"""
                {
                  "type": "object",
                  "properties": {
                    {{module}},
                    "criteria": { "type": "string", "description": "e.g. ((Last_Name:equals:Burns)and(Lead_Source:equals:Web))" },
                    "email": { "type": "string" },
                    "phone": { "type": "string" },
                    "word": { "type": "string" },
                    {{fields}},
                    {{paging}}
                  },
                  "required": ["module"]
                }
                """),
            Connector.Tool("get_records", "List a module's records (newest first unless sorted).", ToolSideEffects.ReadOnly, $$"""
                {
                  "type": "object",
                  "properties": {
                    {{module}}, {{fields}}, {{paging}},
                    "sort_by": { "type": "string", "enum": ["id", "Created_Time", "Modified_Time"] },
                    "sort_order": { "type": "string", "enum": ["asc", "desc"] }
                  },
                  "required": ["module", "fields"]
                }
                """),
            Connector.Tool("get_record", "One record by id.", ToolSideEffects.ReadOnly,
                $$"""{ "type": "object", "properties": { {{module}}, "id": { "type": "string" }, {{fields}} }, "required": ["module", "id"] }""")
        };

        if (Connector.WritesAllowed(connection))
        {
            const string records = """ "records": { "type": "array", "items": { "type": "object" }, "description": "Objects of field API names to values." } """;
            tools.Add(Connector.Tool("create_records", "Create records. Required fields depend on the module (e.g. Last_Name for Leads).", ToolSideEffects.NonIdempotent,
                $$"""{ "type": "object", "properties": { {{module}}, {{records}} }, "required": ["module", "records"] }"""));
            tools.Add(Connector.Tool("update_records", "Change existing records; each needs its id. Fields not given stay as they are.", ToolSideEffects.Idempotent,
                $$"""{ "type": "object", "properties": { {{module}}, {{records}} }, "required": ["module", "records"] }"""));
            tools.Add(Connector.Tool("upsert_records",
                "Update records that match on the duplicate check fields (the module's default, e.g. Email for Leads, unless given), creating the rest. Safe to repeat.",
                ToolSideEffects.Idempotent, $$"""
                {
                  "type": "object",
                  "properties": { {{module}}, {{records}}, "duplicate_check_fields": { "type": "array", "items": { "type": "string" } } },
                  "required": ["module", "records"]
                }
                """));
        }

        return Task.FromResult<IReadOnlyList<ToolDefinition>>(tools);
    }

    public Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request) => Connector.Guard(async () =>
    {
        var args = Connector.Args(request);
        var ct = request.CancellationToken;
        if (toolName is "create_records" or "update_records" or "upsert_records" && !Connector.WritesAllowed(connection))
        {
            return ToolExecutionResult.Fail("This connection doesn't allow writes.");
        }

        var (baseUrl, auth) = await PrepareAsync(connection, ct);
        string Module() => Connector.Required(args, "module") is var m && m.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? m
            : throw new ArgumentException("module must be a module's API name, e.g. Leads.");
        string Paging() => $"page={Connector.Int(args, "page", 1, 1, 10_000)}&per_page={Connector.Int(args, "per_page", 50, 1, 200)}";
        string Fields() => Connector.Str(args, "fields") is { Length: > 0 } f ? $"&fields={Uri.EscapeDataString(f)}" : "";

        switch (toolName)
        {
            case "list_modules":
            {
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, $"{baseUrl}/settings/modules", auth, null, ct);
                if (status != 200) return Connector.Fail("Zoho CRM", status, body, text);
                return Connector.Ok(new JsonObject
                {
                    ["modules"] = new JsonArray([.. (body?["modules"] as JsonArray ?? [])
                        .Where(m => Connector.At(m, "api_supported") != "false")
                        .Select(m => (JsonNode?)new JsonObject { ["api_name"] = Connector.At(m, "api_name"), ["label"] = Connector.At(m, "plural_label") })])
                });
            }

            case "list_fields":
            {
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, $"{baseUrl}/settings/fields?module={Module()}", auth, null, ct);
                if (status != 200) return Connector.Fail("Zoho CRM", status, body, text);
                return Connector.Ok(new JsonObject
                {
                    ["fields"] = new JsonArray([.. (body?["fields"] as JsonArray ?? []).Select(f => (JsonNode?)new JsonObject
                    {
                        ["api_name"] = Connector.At(f, "api_name"),
                        ["label"] = Connector.At(f, "field_label"),
                        ["type"] = Connector.At(f, "data_type"),
                        ["required"] = Connector.At(f, "system_mandatory") == "true"
                    })])
                });
            }

            case "search_records":
            {
                var by = Connector.Str(args, "criteria") is { Length: > 0 } c ? $"criteria={Uri.EscapeDataString(c)}"
                    : Connector.Str(args, "email") is { Length: > 0 } e ? $"email={Uri.EscapeDataString(e)}"
                    : Connector.Str(args, "phone") is { Length: > 0 } p ? $"phone={Uri.EscapeDataString(p)}"
                    : Connector.Str(args, "word") is { Length: > 0 } w ? $"word={Uri.EscapeDataString(w)}"
                    : throw new ArgumentException("Give criteria, email, phone or word to search by.");
                return await ListAsync($"{baseUrl}/{Module()}/search?{by}&{Paging()}{Fields()}", auth, ct);
            }

            case "get_records":
            {
                var sort = Connector.Str(args, "sort_by") is { Length: > 0 } s ? $"&sort_by={Uri.EscapeDataString(s)}&sort_order={(Connector.Str(args, "sort_order") == "asc" ? "asc" : "desc")}" : "";
                if (Fields().Length == 0) throw new ArgumentException("fields is required: name the fields to read, e.g. Last_Name,Email.");
                return await ListAsync($"{baseUrl}/{Module()}?{Paging()}{Fields()}{sort}", auth, ct);
            }

            case "get_record":
            {
                var id = Connector.Required(args, "id");
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get,
                    $"{baseUrl}/{Module()}/{Uri.EscapeDataString(id)}" + (Fields() is { Length: > 0 } f ? "?" + f[1..] : ""), auth, null, ct);
                if (status == 204) return ToolExecutionResult.Fail($"No {Module()} record {id}.");
                return status == 200 ? Connector.Ok((body?["data"] as JsonArray)?.FirstOrDefault()?.DeepClone()) : Connector.Fail("Zoho CRM", status, body, text);
            }

            case "create_records":
            case "update_records":
            case "upsert_records":
            {
                var records = args["records"] as JsonArray ?? throw new ArgumentException("'records' must be a list.");
                if (records.Count > 1000) throw new ArgumentException("At most 1000 records per call.");
                if (records.Any(r => r is not JsonObject)) throw new ArgumentException("Each record must be an object of field values.");
                if (toolName == "update_records" && records.Any(r => Connector.Str(r!.AsObject(), "id") is not { Length: > 0 }))
                {
                    throw new ArgumentException("Each record to update needs its id.");
                }

                var (method, url) = toolName switch
                {
                    "create_records" => (HttpMethod.Post, $"{baseUrl}/{Module()}"),
                    "update_records" => (HttpMethod.Put, $"{baseUrl}/{Module()}"),
                    _ => (HttpMethod.Post, $"{baseUrl}/{Module()}/upsert")
                };

                var done = new JsonArray();
                var failures = new List<string>();
                foreach (var chunk in records.Chunk(Batch))
                {
                    var payload = new JsonObject { ["data"] = new JsonArray([.. chunk.Select(r => r?.DeepClone())]) };
                    if (toolName == "upsert_records" && args["duplicate_check_fields"] is JsonArray { Count: > 0 } dup) payload["duplicate_check_fields"] = dup.DeepClone();
                    var (status, body, text) = await Connector.SendAsync(Http, method, url, auth, payload, ct);
                    if (body?["data"] is not JsonArray results)
                    {
                        var failed = Connector.Fail("Zoho CRM", status, body, text);
                        return done.Count == 0 ? failed : ToolExecutionResult.Fail($"{failed.ErrorMessage} ({done.Count} record(s) before this batch went through.)");
                    }

                    foreach (var r in results)
                    {
                        if (Connector.At(r, "status") == "success")
                        {
                            done.Add(new JsonObject { ["id"] = Connector.At(r, "details", "id"), ["action"] = Connector.At(r, "action") ?? Connector.At(r, "code") });
                        }
                        else
                        {
                            failures.Add($"{Connector.At(r, "code")}: {Connector.At(r, "message")} {Connector.At(r, "details")}".Trim());
                        }
                    }
                }

                if (failures.Count > 0)
                {
                    return ToolExecutionResult.Fail($"{failures.Count} record(s) failed ({done.Count} succeeded): {string.Join("; ", failures.Take(5))}");
                }

                return Connector.Ok(new JsonObject { ["records"] = done, ["count"] = done.Count });
            }

            default:
                return ToolExecutionResult.Fail($"Unknown tool '{toolName}'.");
        }
    });

    private async Task<ToolExecutionResult> ListAsync(string url, Action<HttpRequestMessage> auth, CancellationToken ct)
    {
        var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, url, auth, null, ct);
        // 204: nothing matched.
        if (status == 204) return Connector.Ok(new JsonObject { ["records"] = new JsonArray(), ["count"] = 0, ["more"] = false });
        if (status != 200) return Connector.Fail("Zoho CRM", status, body, text);
        var records = body?["data"] as JsonArray ?? [];
        return Connector.Ok(new JsonObject
        {
            ["records"] = records.DeepClone(),
            ["count"] = records.Count,
            ["more"] = Connector.At(body, "info", "more_records") == "true"
        });
    }

    private async Task<(string BaseUrl, Action<HttpRequestMessage> Auth)> PrepareAsync(PluginConnection connection, CancellationToken ct)
    {
        var dc = connection.Setting("data_center", "com");
        if (!AccountsHosts.TryGetValue(dc, out var accounts)) throw new ArgumentException($"Unknown data center '{dc}'.");
        var version = connection.Setting("api_version", "v8");
        if (version is not ("v2" or "v3" or "v4" or "v5" or "v6" or "v7" or "v8")) throw new ArgumentException("API version must be v2 to v8.");

        var clientId = connection.Secret("client_id");
        var refresh = connection.Secret("refresh_token");
        var key = $"{dc}|{clientId}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refresh)))[..16]}";
        if (!_tokens.TryGetValue(key, out var cached) || cached.Expires <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            using var response = await Http.PostAsync($"{accounts}/oauth/v2/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["refresh_token"] = refresh,
                ["client_id"] = clientId,
                ["client_secret"] = connection.Secret("client_secret"),
                ["grant_type"] = "refresh_token"
            }), ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonNode? body;
            try { body = JsonNode.Parse(text); } catch (JsonException) { body = null; }
            if (Connector.At(body, "access_token") is not { Length: > 0 } token)
            {
                throw new ArgumentException($"Zoho refused the credentials: {Connector.At(body, "error") ?? ((int)response.StatusCode).ToString()}. " +
                                            "Check the data center and that the refresh token belongs to this client.");
            }

            var domain = Connector.At(body, "api_domain") is { Length: > 0 } d && d.StartsWith("https://", StringComparison.Ordinal) ? d : $"https://www.zohoapis.{dc}";
            var seconds = int.TryParse(Connector.At(body, "expires_in"), out var s) ? s : 3600;
            cached = (token, domain.TrimEnd('/'), DateTimeOffset.UtcNow.AddSeconds(seconds));
            _tokens[key] = cached;
        }

        var accessToken = cached.Token;
        return ($"{cached.ApiDomain}/crm/{version}", r => r.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", accessToken));
    }

    private HttpClient Http => httpClientFactory.CreateClient("integrations");
}
