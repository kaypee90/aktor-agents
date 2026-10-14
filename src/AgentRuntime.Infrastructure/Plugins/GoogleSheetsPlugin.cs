using System.Text.Json.Nodes;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;

namespace AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// Google Sheets through a service account: read rows (as objects keyed by the header row), and,
/// when writes are allowed, append, update and clear. The sheet must be shared with the service
/// account's email; that sharing is what decides which sheets agents and pipelines can reach.
/// </summary>
public sealed class GoogleSheetsPlugin(IHttpClientFactory httpClientFactory, GoogleServiceAccountTokens tokens) : IToolProviderPlugin
{
    private const string Api = "https://sheets.googleapis.com/v4/spreadsheets";

    public PluginManifest Manifest { get; } = new()
    {
        Id = "google-sheets",
        Name = "Google Sheets",
        Description = "Read rows from Google Sheets and (optionally) append, update or clear them.",
        Category = PluginCategory.Data,
        Settings =
        [
            new() { Key = "spreadsheet_id", Label = "Default spreadsheet (link or id)", Placeholder = "https://docs.google.com/spreadsheets/d/…/edit, or just the id",
                    Description = "Used when a call doesn't name a spreadsheet." },
            new() { Key = "allow_writes", Label = "Allow writes (append, update, clear)", Options = ["false", "true"], DefaultValue = "false" },
            new() { Key = "service_account_json", Label = "Service account key (JSON)", Secret = true, Required = true }
        ],
        SetupHelp = "In Google Cloud: enable the Google Sheets API, create a service account, and add a JSON key. Paste the whole key file here. " +
                    "Then share each spreadsheet with the service account's email (Viewer to read, Editor to write)."
    };

    public Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken) => Connector.Check(async () =>
    {
        var token = await TokenAsync(connection, cancellationToken);
        if (SpreadsheetId(connection.Setting("spreadsheet_id")) is not { Length: > 0 } id) return ConnectionCheck.Success("Service account key accepted.");
        var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, $"{Api}/{Uri.EscapeDataString(id)}?fields=properties.title",
            GoogleServiceAccountTokens.Bearer(token), null, cancellationToken);
        return status == 200
            ? ConnectionCheck.Success($"Reached '{Connector.At(body, "properties", "title")}'.")
            : ConnectionCheck.Failure(Failure(status, body, text, id, connection).ErrorMessage!);
    });

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken)
    {
        const string spreadsheet = """ "spreadsheet_id": { "type": "string", "description": "Optional: the spreadsheet's link or id; defaults to the connection's spreadsheet." } """;
        var tools = new List<ToolDefinition>
        {
            Connector.Tool("list_sheets", "List the tabs of a spreadsheet, with their sizes.", ToolSideEffects.ReadOnly,
                $$"""{ "type": "object", "properties": { {{spreadsheet}} } }"""),
            Connector.Tool("read_rows", "Read rows from a range. With header_row (default), each row is an object keyed by the first row's column names.",
                ToolSideEffects.ReadOnly, $$"""
                {
                  "type": "object",
                  "properties": {
                    {{spreadsheet}},
                    "range": { "type": "string", "description": "A tab name ('Orders') or A1 range ('Orders!A1:F200')." },
                    "header_row": { "type": "boolean", "description": "Treat the range's first row as column names (default true)." },
                    "limit": { "type": "integer", "description": "Most rows returned (default 500, at most 5000)." }
                  },
                  "required": ["range"]
                }
                """)
        };

        if (Connector.WritesAllowed(connection))
        {
            tools.Add(Connector.Tool("append_rows",
                "Append rows after the last row of a table. Rows are arrays of cell values, or objects keyed by the header row's column names.",
                ToolSideEffects.NonIdempotent, $$"""
                {
                  "type": "object",
                  "properties": {
                    {{spreadsheet}},
                    "range": { "type": "string", "description": "The tab (or table range) to append to, e.g. 'Leads'." },
                    "rows": { "type": "array", "items": {}, "description": "[[\"Ada\", \"ada@example.com\"]] or [{\"Name\": \"Ada\", \"Email\": \"ada@example.com\"}]" }
                  },
                  "required": ["range", "rows"]
                }
                """));
            tools.Add(Connector.Tool("update_range", "Overwrite the cells of a range with values (rows of cells). Formulas and dates are entered as if typed.",
                ToolSideEffects.Idempotent, $$"""
                {
                  "type": "object",
                  "properties": {
                    {{spreadsheet}},
                    "range": { "type": "string", "description": "A1 range, e.g. 'Summary!B2:D4'." },
                    "values": { "type": "array", "items": { "type": "array", "items": {} } }
                  },
                  "required": ["range", "values"]
                }
                """));
            tools.Add(Connector.Tool("clear_range", "Clear the values in a range (formatting stays).", ToolSideEffects.Idempotent, $$"""
                {
                  "type": "object",
                  "properties": { {{spreadsheet}}, "range": { "type": "string" } },
                  "required": ["range"]
                }
                """));
        }

        return Task.FromResult<IReadOnlyList<ToolDefinition>>(tools);
    }

    public Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request) => Connector.Guard(async () =>
    {
        var args = Connector.Args(request);
        var ct = request.CancellationToken;
        var writes = toolName is "append_rows" or "update_range" or "clear_range";
        if (writes && !Connector.WritesAllowed(connection)) return ToolExecutionResult.Fail("This connection doesn't allow writes.");

        var id = SpreadsheetId(Connector.Str(args, "spreadsheet_id") is { Length: > 0 } given ? given : connection.Setting("spreadsheet_id"));
        if (id.Length == 0) throw new ArgumentException("No spreadsheet_id given, and the connection has no default spreadsheet.");
        var token = await TokenAsync(connection, ct);
        var auth = GoogleServiceAccountTokens.Bearer(token);
        var sheet = $"{Api}/{Uri.EscapeDataString(id)}";

        switch (toolName)
        {
            case "list_sheets":
            {
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get,
                    $"{sheet}?fields=properties.title,sheets.properties(sheetId,title,gridProperties(rowCount,columnCount))", auth, null, ct);
                if (status != 200) return Failure(status, body, text, id, connection);
                var tabs = new JsonArray();
                foreach (var s in body?["sheets"]?.AsArray() ?? [])
                {
                    tabs.Add(new JsonObject
                    {
                        ["title"] = Connector.At(s, "properties", "title"),
                        ["rows"] = s?["properties"]?["gridProperties"]?["rowCount"]?.DeepClone(),
                        ["columns"] = s?["properties"]?["gridProperties"]?["columnCount"]?.DeepClone()
                    });
                }

                return Connector.Ok(new JsonObject { ["spreadsheet"] = Connector.At(body, "properties", "title"), ["sheets"] = tabs });
            }

            case "read_rows":
            {
                var range = Connector.Required(args, "range");
                var limit = Connector.Int(args, "limit", 500, 1, 5000);
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get,
                    $"{sheet}/values/{Uri.EscapeDataString(range)}?valueRenderOption=UNFORMATTED_VALUE&dateTimeRenderOption=FORMATTED_STRING", auth, null, ct);
                if (status != 200) return Failure(status, body, text, id, connection);
                var values = body?["values"] as JsonArray ?? [];
                if (!Connector.Bool(args, "header_row", true) || values.Count == 0)
                {
                    return Connector.Ok(new JsonObject { ["range"] = Connector.At(body, "range"), ["rows"] = new JsonArray([.. values.Take(limit).Select(v => v?.DeepClone())]), ["row_count"] = values.Count });
                }

                var headers = (values[0] as JsonArray ?? []).Select((h, i) => h?.ToString() is { Length: > 0 } name ? name : $"column_{i + 1}").ToList();
                var rows = new JsonArray();
                foreach (var row in values.Skip(1).Take(limit).OfType<JsonArray>())
                {
                    var obj = new JsonObject();
                    for (var i = 0; i < headers.Count; i++) obj[headers[i]] = i < row.Count ? row[i]?.DeepClone() : null;
                    rows.Add(obj);
                }

                return Connector.Ok(new JsonObject
                {
                    ["range"] = Connector.At(body, "range"),
                    ["headers"] = new JsonArray([.. headers.Select(h => (JsonNode?)h)]),
                    ["rows"] = rows,
                    ["row_count"] = values.Count - 1,
                    ["truncated"] = values.Count - 1 > limit
                });
            }

            case "append_rows":
            {
                var range = Connector.Required(args, "range");
                var rows = args["rows"] as JsonArray ?? throw new ArgumentException("'rows' must be a list.");
                if (rows.Count == 0) return Connector.Ok(new JsonObject { ["appended"] = 0 });
                if (rows.Count > 5000) throw new ArgumentException("At most 5000 rows per call.");

                JsonArray cells;
                if (rows.Any(r => r is JsonObject))
                {
                    // Objects go in the header row's column order; names not in it are reported, not dropped silently.
                    var tab = range.Contains('!') ? range[..range.IndexOf('!')] : range;
                    var (hs, hb, ht) = await Connector.SendAsync(Http, HttpMethod.Get, $"{sheet}/values/{Uri.EscapeDataString($"{tab}!1:1")}", auth, null, ct);
                    if (hs != 200) return Failure(hs, hb, ht, id, connection);
                    var headers = (hb?["values"]?[0] as JsonArray ?? []).Select(h => h?.ToString() ?? "").ToList();
                    if (headers.Count == 0) throw new ArgumentException($"'{tab}' has no header row to match the rows' keys to; send rows as arrays instead.");
                    var unknown = rows.OfType<JsonObject>().SelectMany(o => o.Select(kv => kv.Key)).Distinct().Where(k => !headers.Contains(k)).ToList();
                    if (unknown.Count > 0) throw new ArgumentException($"Not columns of '{tab}': {string.Join(", ", unknown)}. Its columns: {string.Join(", ", headers)}.");
                    cells = new JsonArray([.. rows.Select(r => r is JsonObject o
                        ? (JsonNode)new JsonArray([.. headers.Select(h => Cell(o[h]))])
                        : r?.DeepClone())]);
                }
                else
                {
                    cells = new JsonArray([.. rows.Select(r => r is JsonArray a ? new JsonArray([.. a.Select(Cell)]) : (JsonNode)new JsonArray(Cell(r)))]);
                }

                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Post,
                    $"{sheet}/values/{Uri.EscapeDataString(range)}:append?valueInputOption=USER_ENTERED&insertDataOption=INSERT_ROWS",
                    auth, new JsonObject { ["values"] = cells }, ct);
                if (status != 200) return Failure(status, body, text, id, connection);
                return Connector.Ok(new JsonObject
                {
                    ["appended"] = cells.Count,
                    ["updated_range"] = Connector.At(body, "updates", "updatedRange")
                });
            }

            case "update_range":
            {
                var range = Connector.Required(args, "range");
                var values = args["values"] as JsonArray ?? throw new ArgumentException("'values' must be a list of rows.");
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Put,
                    $"{sheet}/values/{Uri.EscapeDataString(range)}?valueInputOption=USER_ENTERED", auth,
                    new JsonObject { ["values"] = new JsonArray([.. values.Select(r => r is JsonArray a ? new JsonArray([.. a.Select(Cell)]) : (JsonNode)new JsonArray(Cell(r)))]) }, ct);
                if (status != 200) return Failure(status, body, text, id, connection);
                return Connector.Ok(new JsonObject { ["updated_range"] = Connector.At(body, "updatedRange"), ["updated_cells"] = body?["updatedCells"]?.DeepClone() });
            }

            case "clear_range":
            {
                var range = Connector.Required(args, "range");
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Post, $"{sheet}/values/{Uri.EscapeDataString(range)}:clear", auth, new JsonObject(), ct);
                if (status != 200) return Failure(status, body, text, id, connection);
                return Connector.Ok(new JsonObject { ["cleared_range"] = Connector.At(body, "clearedRange") });
            }

            default:
                return ToolExecutionResult.Fail($"Unknown tool '{toolName}'.");
        }
    });

    /// <summary>The id in a spreadsheet's link (…/spreadsheets/d/&lt;id&gt;/edit#gid=0), or the id itself.</summary>
    internal static string SpreadsheetId(string? linkOrId)
    {
        var value = (linkOrId ?? string.Empty).Trim();
        var match = System.Text.RegularExpressions.Regex.Match(value, @"/spreadsheets/(?:u/\d+/)?d/([a-zA-Z0-9_-]+)");
        return match.Success ? match.Groups[1].Value : value;
    }

    /// <summary>Google's errors, with what to check for the ones people hit setting this up.</summary>
    private static ToolExecutionResult Failure(int status, JsonNode? body, string text, string id, PluginConnection connection)
    {
        string? email = null;
        try { email = GoogleServiceAccountTokens.Parse(connection.Secret("service_account_json")).ClientEmail; } catch (Exception ex) when (Connector.IsUserError(ex)) { }
        return status switch
        {
            404 => ToolExecutionResult.Fail($"Google Sheets can't find a spreadsheet with id '{id}'. Use the id from the sheet's link " +
                                            "(between /d/ and /edit), and check it's a Google Sheet: an Excel file stored in Drive must first be saved as a Google Sheet (File → Save as Google Sheets)."),
            403 => ToolExecutionResult.Fail($"The service account can't open spreadsheet '{id}'. Share the sheet with {email ?? "the service account's email (client_email in the key)"} " +
                                            "as Viewer to read, or Editor to write. If it is shared, check the Google Sheets API is enabled for the key's project."),
            _ => Connector.Fail("Google Sheets", status, body, text)
        };
    }

    /// <summary>A cell value: numbers and booleans as they are, lists and objects as JSON text.</summary>
    private static JsonNode? Cell(JsonNode? value) => value switch
    {
        null => JsonValue.Create(""),
        JsonValue v => v.DeepClone(),
        _ => JsonValue.Create(value.ToJsonString())
    };

    private HttpClient Http => httpClientFactory.CreateClient("integrations");

    private Task<string> TokenAsync(PluginConnection connection, CancellationToken ct) =>
        tokens.GetTokenAsync(GoogleServiceAccountTokens.Parse(connection.Secret("service_account_json")),
            Connector.WritesAllowed(connection) ? "https://www.googleapis.com/auth/spreadsheets" : "https://www.googleapis.com/auth/spreadsheets.readonly", ct);
}
