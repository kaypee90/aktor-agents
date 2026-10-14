using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;

namespace AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// One Airtable base through a personal access token: list tables, read records (with Airtable
/// formulas to filter), and, when writes are allowed, create, update, upsert and delete records.
/// Batches are split into Airtable's 10 records per request.
/// </summary>
public sealed class AirtablePlugin(IHttpClientFactory httpClientFactory) : IToolProviderPlugin
{
    private const string Api = "https://api.airtable.com/v0";
    private const int Batch = 10;

    public PluginManifest Manifest { get; } = new()
    {
        Id = "airtable",
        Name = "Airtable",
        Description = "Read records from an Airtable base and (optionally) create, update, upsert or delete them.",
        Category = PluginCategory.Data,
        Settings =
        [
            new() { Key = "base_id", Label = "Base id", Required = true, Placeholder = "appXXXXXXXXXXXXXX (from the base's URL)" },
            new() { Key = "allow_writes", Label = "Allow writes (create, update, delete)", Options = ["false", "true"], DefaultValue = "false" },
            new() { Key = "api_token", Label = "Personal access token", Secret = true, Required = true, Placeholder = "pat…" }
        ],
        SetupHelp = "Create a personal access token at airtable.com/create/tokens with the scopes data.records:read and schema.bases:read " +
                    "(plus data.records:write to write), and give it access to this base only."
    };

    public Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken) => Connector.Check(async () =>
    {
        var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, $"{Api}/meta/bases/{Base(connection)}/tables", Auth(connection), null, cancellationToken);
        return status switch
        {
            200 => ConnectionCheck.Success($"Reached the base ({(body?["tables"] as JsonArray)?.Count ?? 0} tables)."),
            403 => ConnectionCheck.Success("The token works, but lacks schema.bases:read, so list_tables won't."),
            _ => ConnectionCheck.Failure(Connector.Fail("Airtable", status, body, text).ErrorMessage!)
        };
    });

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken)
    {
        const string table = """ "table": { "type": "string", "description": "The table's name or id (tbl…)." } """;
        var tools = new List<ToolDefinition>
        {
            Connector.Tool("list_tables", "The base's tables with their fields (name and type).", ToolSideEffects.ReadOnly, """{ "type": "object", "properties": {} }"""),
            Connector.Tool("list_records", "Read records from a table, optionally filtered with an Airtable formula and sorted.", ToolSideEffects.ReadOnly, $$"""
                {
                  "type": "object",
                  "properties": {
                    {{table}},
                    "filter_formula": { "type": "string", "description": "e.g. AND({Status} = 'Open', {Amount} > 100)" },
                    "view": { "type": "string" },
                    "fields": { "type": "array", "items": { "type": "string" }, "description": "Only these fields." },
                    "sort": { "type": "array", "items": { "type": "object", "properties": { "field": { "type": "string" }, "direction": { "type": "string", "enum": ["asc", "desc"] } } } },
                    "max_records": { "type": "integer", "description": "Default 100, at most 1000." }
                  },
                  "required": ["table"]
                }
                """),
            Connector.Tool("get_record", "One record by id.", ToolSideEffects.ReadOnly,
                $$"""{ "type": "object", "properties": { {{table}}, "record_id": { "type": "string" } }, "required": ["table", "record_id"] }""")
        };

        if (Connector.WritesAllowed(connection))
        {
            const string typecast = """ "typecast": { "type": "boolean", "description": "Let Airtable convert values (e.g. create missing select options)." } """;
            tools.Add(Connector.Tool("create_records", "Create records; each is an object of field values (or {\"fields\": {…}}).", ToolSideEffects.NonIdempotent, $$"""
                { "type": "object", "properties": { {{table}}, "records": { "type": "array", "items": { "type": "object" } }, {{typecast}} }, "required": ["table", "records"] }
                """));
            tools.Add(Connector.Tool("update_records", "Change fields of existing records: [{\"id\": \"rec…\", \"fields\": {…}}]. Fields not given stay as they are.",
                ToolSideEffects.Idempotent, $$"""
                { "type": "object", "properties": { {{table}}, "records": { "type": "array", "items": { "type": "object" } }, {{typecast}} }, "required": ["table", "records"] }
                """));
            tools.Add(Connector.Tool("upsert_records",
                "Update records matched on merge_on fields, creating those that don't match. Safe to repeat.", ToolSideEffects.Idempotent, $$"""
                {
                  "type": "object",
                  "properties": {
                    {{table}},
                    "records": { "type": "array", "items": { "type": "object" }, "description": "Objects of field values." },
                    "merge_on": { "type": "array", "items": { "type": "string" }, "description": "Fields that identify a record, e.g. [\"Email\"]." },
                    {{typecast}}
                  },
                  "required": ["table", "records", "merge_on"]
                }
                """));
            tools.Add(Connector.Tool("delete_records", "Delete records by id.", ToolSideEffects.Idempotent,
                $$"""{ "type": "object", "properties": { {{table}}, "record_ids": { "type": "array", "items": { "type": "string" } } }, "required": ["table", "record_ids"] }"""));
        }

        return Task.FromResult<IReadOnlyList<ToolDefinition>>(tools);
    }

    public Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request) => Connector.Guard(async () =>
    {
        var args = Connector.Args(request);
        var ct = request.CancellationToken;
        if (toolName is "create_records" or "update_records" or "upsert_records" or "delete_records" && !Connector.WritesAllowed(connection))
        {
            return ToolExecutionResult.Fail("This connection doesn't allow writes.");
        }

        var auth = Auth(connection);
        string TableUrl() => $"{Api}/{Base(connection)}/{Uri.EscapeDataString(Connector.Required(args, "table"))}";

        switch (toolName)
        {
            case "list_tables":
            {
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, $"{Api}/meta/bases/{Base(connection)}/tables", auth, null, ct);
                if (status != 200) return Connector.Fail("Airtable", status, body, text);
                return Connector.Ok(new JsonObject
                {
                    ["tables"] = new JsonArray([.. (body?["tables"] as JsonArray ?? []).Select(t => (JsonNode?)new JsonObject
                    {
                        ["id"] = Connector.At(t, "id"),
                        ["name"] = Connector.At(t, "name"),
                        ["fields"] = new JsonArray([.. (t?["fields"] as JsonArray ?? []).Select(f => (JsonNode?)new JsonObject
                        {
                            ["name"] = Connector.At(f, "name"),
                            ["type"] = Connector.At(f, "type")
                        })])
                    })])
                });
            }

            case "list_records":
            {
                var max = Connector.Int(args, "max_records", 100, 1, 1000);
                var query = new StringBuilder($"pageSize={Math.Min(100, max)}&maxRecords={max}");
                if (Connector.Str(args, "filter_formula") is { Length: > 0 } formula) query.Append("&filterByFormula=").Append(Uri.EscapeDataString(formula));
                if (Connector.Str(args, "view") is { Length: > 0 } view) query.Append("&view=").Append(Uri.EscapeDataString(view));
                foreach (var field in (args["fields"] as JsonArray ?? []).Select(f => f?.ToString()).OfType<string>())
                {
                    query.Append("&fields%5B%5D=").Append(Uri.EscapeDataString(field));
                }

                var i = 0;
                foreach (var sort in (args["sort"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    query.Append($"&sort%5B{i}%5D%5Bfield%5D=").Append(Uri.EscapeDataString(Connector.Str(sort, "field") ?? ""));
                    query.Append($"&sort%5B{i}%5D%5Bdirection%5D=").Append(Connector.Str(sort, "direction") == "desc" ? "desc" : "asc");
                    i++;
                }

                var records = new JsonArray();
                string? offset = null;
                do
                {
                    var url = $"{TableUrl()}?{query}" + (offset is null ? "" : $"&offset={Uri.EscapeDataString(offset)}");
                    var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, url, auth, null, ct);
                    if (status != 200) return Connector.Fail("Airtable", status, body, text);
                    foreach (var r in body?["records"] as JsonArray ?? []) records.Add(Flatten(r));
                    offset = Connector.At(body, "offset");
                } while (offset is not null && records.Count < max);

                return Connector.Ok(new JsonObject { ["records"] = records, ["count"] = records.Count, ["more"] = offset is not null });
            }

            case "get_record":
            {
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get,
                    $"{TableUrl()}/{Uri.EscapeDataString(Connector.Required(args, "record_id"))}", auth, null, ct);
                return status == 200 ? Connector.Ok(Flatten(body)) : Connector.Fail("Airtable", status, body, text);
            }

            case "create_records":
            case "update_records":
            case "upsert_records":
            {
                var records = args["records"] as JsonArray ?? throw new ArgumentException("'records' must be a list.");
                if (records.Count > 1000) throw new ArgumentException("At most 1000 records per call.");
                var update = toolName != "create_records";
                var mergeOn = toolName == "upsert_records"
                    ? args["merge_on"] as JsonArray is { Count: > 0 } m ? m : throw new ArgumentException("'merge_on' must name the fields that identify a record.")
                    : null;

                var results = new JsonArray();
                foreach (var chunk in records.Chunk(Batch))
                {
                    var payload = new JsonObject
                    {
                        ["records"] = new JsonArray([.. chunk.Select(r => (JsonNode?)Shape(r, requireId: toolName == "update_records"))]),
                        ["typecast"] = Connector.Bool(args, "typecast", false)
                    };
                    if (mergeOn is not null) payload["performUpsert"] = new JsonObject { ["fieldsToMergeOn"] = mergeOn.DeepClone() };
                    var (status, body, text) = await Connector.SendAsync(Http, update ? HttpMethod.Patch : HttpMethod.Post, TableUrl(), auth, payload, ct);
                    if (status != 200)
                    {
                        var failed = Connector.Fail("Airtable", status, body, text);
                        return results.Count == 0 ? failed : ToolExecutionResult.Fail($"{failed.ErrorMessage} ({results.Count} record(s) before this batch went through.)");
                    }

                    foreach (var r in body?["records"] as JsonArray ?? []) results.Add(Flatten(r));
                }

                return Connector.Ok(new JsonObject { ["records"] = results, ["count"] = results.Count });
            }

            case "delete_records":
            {
                var ids = (args["record_ids"] as JsonArray ?? []).Select(x => x?.ToString()).OfType<string>().ToList();
                var deleted = 0;
                foreach (var chunk in ids.Chunk(Batch))
                {
                    var url = $"{TableUrl()}?" + string.Join("&", chunk.Select(id => $"records%5B%5D={Uri.EscapeDataString(id)}"));
                    var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Delete, url, auth, null, ct);
                    // Already gone counts as deleted: deleting is safe to repeat.
                    if (status is not (200 or 404)) return Connector.Fail("Airtable", status, body, text);
                    deleted += chunk.Length;
                }

                return Connector.Ok(new JsonObject { ["deleted"] = deleted });
            }

            default:
                return ToolExecutionResult.Fail($"Unknown tool '{toolName}'.");
        }
    });

    /// <summary>A record to send: <c>{fields}</c>, plus its id for updates. Plain objects are taken as the fields.</summary>
    private static JsonObject Shape(JsonNode? record, bool requireId)
    {
        if (record is not JsonObject o) throw new ArgumentException("Each record must be an object.");
        var fields = o["fields"] is JsonObject f ? f.DeepClone() : new JsonObject(o.Where(kv => kv.Key != "id").Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));
        var shaped = new JsonObject { ["fields"] = fields };
        if (Connector.Str(o, "id") is { Length: > 0 } id) shaped["id"] = id;
        else if (requireId) throw new ArgumentException("Each record to update needs its id (rec…).");
        return shaped;
    }

    /// <summary>A record as later stages want it: its id and fields side by side.</summary>
    private static JsonObject Flatten(JsonNode? record)
    {
        var flat = new JsonObject { ["id"] = Connector.At(record, "id") };
        foreach (var (key, value) in record?["fields"] as JsonObject ?? []) flat[key] = value?.DeepClone();
        return flat;
    }

    private static string Base(PluginConnection connection) =>
        connection.Setting("base_id") is { Length: > 0 } id && id.All(c => char.IsAsciiLetterOrDigit(c))
            ? id
            : throw new ArgumentException("The base id must look like appXXXXXXXXXXXXXX.");

    private static Action<HttpRequestMessage> Auth(PluginConnection connection)
    {
        var token = connection.Secret("api_token");
        return r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private HttpClient Http => httpClientFactory.CreateClient("integrations");
}
