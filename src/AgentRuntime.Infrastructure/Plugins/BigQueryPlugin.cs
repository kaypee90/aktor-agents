using System.Globalization;
using System.Text.Json.Nodes;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;

namespace AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// Google BigQuery through a service account. <c>query</c> is read-only by construction: every
/// query is dry-run first and refused unless BigQuery itself says it's a SELECT. Writes (streaming
/// inserts with per-row insert ids, and DML statements) exist only when the connection allows them.
/// Values are passed as named query parameters (<c>@name</c>), never pasted into SQL.
/// </summary>
public sealed class BigQueryPlugin(IHttpClientFactory httpClientFactory, GoogleServiceAccountTokens tokens) : IToolProviderPlugin
{
    private const string Api = "https://bigquery.googleapis.com/bigquery/v2/projects";
    private const string Scope = "https://www.googleapis.com/auth/bigquery";

    public PluginManifest Manifest { get; } = new()
    {
        Id = "bigquery",
        Name = "Google BigQuery",
        Description = "Query BigQuery tables with SQL and (optionally) insert rows or run DML statements.",
        Category = PluginCategory.Data,
        Settings =
        [
            new() { Key = "project_id", Label = "Project id", Placeholder = "Defaults to the key's project", Description = "The project queries run (and are billed) in." },
            new() { Key = "dataset", Label = "Default dataset", Placeholder = "analytics", Description = "Used for unqualified table names." },
            new() { Key = "location", Label = "Location", Placeholder = "US, EU, us-central1… (optional)" },
            new() { Key = "max_rows", Label = "Most rows a query returns", DefaultValue = "1000" },
            new() { Key = "allow_writes", Label = "Allow writes (insert rows, DML statements)", Options = ["false", "true"], DefaultValue = "false" },
            new() { Key = "service_account_json", Label = "Service account key (JSON)", Secret = true, Required = true }
        ],
        SetupHelp = "In Google Cloud: create a service account with the BigQuery Job User role on the project and BigQuery Data Viewer " +
                    "(or Data Editor, to write) on the datasets it may use, then add a JSON key and paste the whole file here."
    };

    public Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken) => Connector.Check(async () =>
    {
        var (project, auth) = await PrepareAsync(connection, cancellationToken);
        var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, $"{Api}/{Uri.EscapeDataString(project)}/datasets?maxResults=1", auth, null, cancellationToken);
        return status == 200
            ? ConnectionCheck.Success($"Reached project '{project}'.")
            : ConnectionCheck.Failure(Connector.Fail("BigQuery", status, body, text).ErrorMessage!);
    });

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken)
    {
        const string dataset = """ "dataset": { "type": "string", "description": "Optional: defaults to the connection's dataset." } """;
        const string sqlParams = """ "params": { "type": "object", "description": "Values for @name parameters in the SQL, e.g. {\"since\": \"2025-01-01\", \"min_total\": 100}." } """;
        var tools = new List<ToolDefinition>
        {
            Connector.Tool("list_datasets", "List the project's datasets.", ToolSideEffects.ReadOnly, """{ "type": "object", "properties": {} }"""),
            Connector.Tool("list_tables", "List a dataset's tables and views.", ToolSideEffects.ReadOnly,
                $$"""{ "type": "object", "properties": { {{dataset}} } }"""),
            Connector.Tool("get_table_schema", "A table's columns (name, type, mode, description) and row count.", ToolSideEffects.ReadOnly,
                $$"""{ "type": "object", "properties": { {{dataset}}, "table": { "type": "string" } }, "required": ["table"] }"""),
            Connector.Tool("query",
                "Run a read-only GoogleSQL SELECT and get rows as objects. Pass values as @name parameters in params rather than in the SQL. Only SELECT statements run.",
                ToolSideEffects.ReadOnly, $$"""
                {
                  "type": "object",
                  "properties": {
                    "sql": { "type": "string" },
                    {{sqlParams}},
                    "max_rows": { "type": "integer", "description": "At most the connection's limit." }
                  },
                  "required": ["sql"]
                }
                """)
        };

        if (Connector.WritesAllowed(connection))
        {
            tools.Add(Connector.Tool("insert_rows", "Stream rows (objects keyed by column name) into a table. Repeating the same call doesn't duplicate rows.",
                ToolSideEffects.Idempotent, $$"""
                {
                  "type": "object",
                  "properties": { {{dataset}}, "table": { "type": "string" }, "rows": { "type": "array", "items": { "type": "object" } } },
                  "required": ["table", "rows"]
                }
                """));
            tools.Add(Connector.Tool("run_statement", "Run a DML or DDL statement (INSERT, UPDATE, DELETE, MERGE, CREATE…). Changes data — use deliberately.",
                ToolSideEffects.NonIdempotent, $$"""
                { "type": "object", "properties": { "sql": { "type": "string" }, {{sqlParams}} }, "required": ["sql"] }
                """));
        }

        return Task.FromResult<IReadOnlyList<ToolDefinition>>(tools);
    }

    public Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request) => Connector.Guard(async () =>
    {
        var args = Connector.Args(request);
        var ct = request.CancellationToken;
        if (toolName is "insert_rows" or "run_statement" && !Connector.WritesAllowed(connection)) return ToolExecutionResult.Fail("This connection doesn't allow writes.");

        var (project, auth) = await PrepareAsync(connection, ct);
        var projectUrl = $"{Api}/{Uri.EscapeDataString(project)}";
        string Dataset() => Connector.Str(args, "dataset") is { Length: > 0 } d ? d
            : connection.Setting("dataset") is { Length: > 0 } fallback ? fallback
            : throw new ArgumentException("No dataset given, and the connection has no default dataset.");

        switch (toolName)
        {
            case "list_datasets":
            {
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, $"{projectUrl}/datasets?maxResults=1000", auth, null, ct);
                if (status != 200) return Connector.Fail("BigQuery", status, body, text);
                return Connector.Ok(new JsonObject
                {
                    ["datasets"] = new JsonArray([.. (body?["datasets"] as JsonArray ?? []).Select(d => (JsonNode?)Connector.At(d, "datasetReference", "datasetId"))])
                });
            }

            case "list_tables":
            {
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, $"{projectUrl}/datasets/{Uri.EscapeDataString(Dataset())}/tables?maxResults=1000", auth, null, ct);
                if (status != 200) return Connector.Fail("BigQuery", status, body, text);
                return Connector.Ok(new JsonObject
                {
                    ["tables"] = new JsonArray([.. (body?["tables"] as JsonArray ?? []).Select(t => (JsonNode?)new JsonObject
                    {
                        ["table"] = Connector.At(t, "tableReference", "tableId"),
                        ["type"] = Connector.At(t, "type")
                    })])
                });
            }

            case "get_table_schema":
            {
                var table = Connector.Required(args, "table");
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get,
                    $"{projectUrl}/datasets/{Uri.EscapeDataString(Dataset())}/tables/{Uri.EscapeDataString(table)}", auth, null, ct);
                if (status != 200) return Connector.Fail("BigQuery", status, body, text);
                return Connector.Ok(new JsonObject
                {
                    ["table"] = table,
                    ["rows"] = Connector.At(body, "numRows"),
                    ["columns"] = body?["schema"]?["fields"]?.DeepClone()
                });
            }

            case "query":
            {
                var sql = Connector.Required(args, "sql");
                var limit = Math.Min(Connector.Int(args, "max_rows", MaxRows(connection), 1, MaxRows(connection)), MaxRows(connection));
                var dry = await DryRunAsync(connection, project, auth, sql, args["params"] as JsonObject, ct);
                if (dry.Error is not null) return dry.Error;
                if (dry.StatementType != "SELECT")
                {
                    return ToolExecutionResult.Fail($"query only runs SELECT statements; this is {dry.StatementType}." +
                                                    (Connector.WritesAllowed(connection) ? " Use run_statement to change data." : ""));
                }

                return await RunQueryAsync(connection, project, auth, sql, args["params"] as JsonObject, limit, request.IdempotencyKey, ct);
            }

            case "run_statement":
            {
                var sql = Connector.Required(args, "sql");
                return await RunQueryAsync(connection, project, auth, sql, args["params"] as JsonObject, 100, request.IdempotencyKey, ct);
            }

            case "insert_rows":
            {
                var table = Connector.Required(args, "table");
                var rows = args["rows"] as JsonArray ?? throw new ArgumentException("'rows' must be a list of objects.");
                if (rows.Count == 0) return Connector.Ok(new JsonObject { ["inserted"] = 0 });
                if (rows.Count > 10_000) throw new ArgumentException("At most 10,000 rows per call.");
                if (rows.Any(r => r is not JsonObject)) throw new ArgumentException("Each row must be an object keyed by column name.");

                // Insert ids from the call's idempotency key: BigQuery drops a row it has seen with the same id.
                var body = new JsonObject
                {
                    ["skipInvalidRows"] = false,
                    ["ignoreUnknownValues"] = false,
                    ["rows"] = new JsonArray([.. rows.Select((r, i) => (JsonNode?)new JsonObject
                    {
                        ["insertId"] = string.IsNullOrEmpty(request.IdempotencyKey) ? Guid.NewGuid().ToString("n") : $"{request.IdempotencyKey}:{i}",
                        ["json"] = r!.DeepClone()
                    })])
                };
                var (status, response, text) = await Connector.SendAsync(Http, HttpMethod.Post,
                    $"{projectUrl}/datasets/{Uri.EscapeDataString(Dataset())}/tables/{Uri.EscapeDataString(table)}/insertAll", auth, body, ct);
                if (status != 200) return Connector.Fail("BigQuery", status, response, text);
                if (response?["insertErrors"] is JsonArray { Count: > 0 } errors)
                {
                    return ToolExecutionResult.Fail($"BigQuery rejected {errors.Count} row(s): {Clip(errors.ToJsonString(), 800)}");
                }

                return Connector.Ok(new JsonObject { ["inserted"] = rows.Count, ["table"] = table });
            }

            default:
                return ToolExecutionResult.Fail($"Unknown tool '{toolName}'.");
        }
    });

    private async Task<(string? StatementType, ToolExecutionResult? Error)> DryRunAsync(PluginConnection connection, string project, Action<HttpRequestMessage> auth,
        string sql, JsonObject? parameters, CancellationToken ct)
    {
        var query = QueryConfig(connection, project, sql, parameters);
        var job = new JsonObject { ["configuration"] = new JsonObject { ["dryRun"] = true, ["query"] = query } };
        if (Location(connection) is { } location) job["jobReference"] = new JsonObject { ["projectId"] = project, ["location"] = location };
        var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Post, $"{Api}/{Uri.EscapeDataString(project)}/jobs", auth, job, ct);
        if (status != 200) return (null, Connector.Fail("BigQuery", status, body, text));
        return (Connector.At(body, "statistics", "query", "statementType") ?? "UNKNOWN", null);
    }

    private async Task<ToolExecutionResult> RunQueryAsync(PluginConnection connection, string project, Action<HttpRequestMessage> auth, string sql,
        JsonObject? parameters, int maxRows, string idempotencyKey, CancellationToken ct)
    {
        var request = QueryConfig(connection, project, sql, parameters);
        request["maxResults"] = maxRows;
        request["timeoutMs"] = 30_000;
        if (Location(connection) is { } location) request["location"] = location;
        // The same call repeated (a retry after a crash) runs the job once.
        if (!string.IsNullOrEmpty(idempotencyKey)) request["requestId"] = DeterministicRequestId(idempotencyKey);

        var projectUrl = $"{Api}/{Uri.EscapeDataString(project)}";
        var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Post, $"{projectUrl}/queries", auth, request, ct);
        if (status != 200) return Connector.Fail("BigQuery", status, body, text);

        // Long queries: wait for the job, a bit at a time, up to two minutes.
        for (var waited = 0; Connector.At(body, "jobComplete") == "false" && waited < 4; waited++)
        {
            var jobId = Connector.At(body, "jobReference", "jobId") ?? throw new InvalidOperationException("BigQuery returned no job id.");
            var where = Connector.At(body, "jobReference", "location") is { } loc ? $"&location={Uri.EscapeDataString(loc)}" : "";
            (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get,
                $"{projectUrl}/queries/{Uri.EscapeDataString(jobId)}?maxResults={maxRows}&timeoutMs=30000{where}", auth, null, ct);
            if (status != 200) return Connector.Fail("BigQuery", status, body, text);
        }

        if (Connector.At(body, "jobComplete") == "false") return ToolExecutionResult.Fail("The query is still running after two minutes; narrow it down.");
        var fields = body?["schema"]?["fields"] as JsonArray ?? [];
        var rows = new JsonArray();
        foreach (var row in body?["rows"] as JsonArray ?? [])
        {
            rows.Add(Record(fields, row?["f"] as JsonArray ?? []));
        }

        var total = long.TryParse(Connector.At(body, "totalRows"), out var t) ? t : rows.Count;
        return Connector.Ok(new JsonObject
        {
            ["rows"] = rows,
            ["row_count"] = rows.Count,
            ["total_rows"] = total,
            ["truncated"] = total > rows.Count,
            ["affected_rows"] = Connector.At(body, "numDmlAffectedRows") is { } affected ? long.Parse(affected, CultureInfo.InvariantCulture) : null,
            ["bytes_processed"] = Connector.At(body, "totalBytesProcessed")
        });
    }

    private static JsonObject QueryConfig(PluginConnection connection, string project, string sql, JsonObject? parameters)
    {
        var query = new JsonObject { ["query"] = sql, ["useLegacySql"] = false };
        if (connection.Setting("dataset") is { Length: > 0 } dataset)
        {
            query["defaultDataset"] = new JsonObject { ["projectId"] = project, ["datasetId"] = dataset };
        }

        if (parameters is { Count: > 0 })
        {
            query["parameterMode"] = "NAMED";
            query["queryParameters"] = new JsonArray([.. parameters.Select(p => (JsonNode?)Parameter(p.Key, p.Value))]);
        }

        return query;
    }

    /// <summary>A named query parameter, typed from the JSON value.</summary>
    private static JsonObject Parameter(string name, JsonNode? value)
    {
        if (value is JsonArray list)
        {
            var element = list.FirstOrDefault(v => v is not null);
            return new JsonObject
            {
                ["name"] = name,
                ["parameterType"] = new JsonObject { ["type"] = "ARRAY", ["arrayType"] = new JsonObject { ["type"] = TypeOf(element) } },
                ["parameterValue"] = new JsonObject { ["arrayValues"] = new JsonArray([.. list.Select(v => (JsonNode?)new JsonObject { ["value"] = Scalar(v) })]) }
            };
        }

        if (value is JsonObject) throw new ArgumentException($"Parameter '{name}' must be a value or a list of values, not an object.");
        return new JsonObject
        {
            ["name"] = name,
            ["parameterType"] = new JsonObject { ["type"] = TypeOf(value) },
            ["parameterValue"] = new JsonObject { ["value"] = Scalar(value) }
        };
    }

    private static string TypeOf(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<bool>(out _) => "BOOL",
        JsonValue v when v.TryGetValue<long>(out _) => "INT64",
        JsonValue v when v.TryGetValue<double>(out _) => "FLOAT64",
        _ => "STRING"
    };

    private static string? Scalar(JsonNode? value) => value switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        _ => value.ToJsonString()
    };

    /// <summary>A row's cells as an object, typed by the schema (BigQuery sends every value as text).</summary>
    private static JsonObject Record(JsonArray fields, JsonArray cells)
    {
        var obj = new JsonObject();
        for (var i = 0; i < fields.Count && i < cells.Count; i++)
        {
            var field = fields[i]!;
            var name = Connector.At(field, "name") ?? $"f{i}";
            obj[name] = Value(field, cells[i]?["v"]);
        }

        return obj;
    }

    private static JsonNode? Value(JsonNode field, JsonNode? v)
    {
        if (v is null) return null;
        if (Connector.At(field, "mode") == "REPEATED" && v is JsonArray repeated)
        {
            var single = field.DeepClone().AsObject();
            single["mode"] = "NULLABLE";
            return new JsonArray([.. repeated.Select(x => Value(single, x?["v"]))]);
        }

        var type = Connector.At(field, "type");
        if (type is "RECORD" or "STRUCT" && v is JsonObject record)
        {
            return Record(field["fields"] as JsonArray ?? [], record["f"] as JsonArray ?? []);
        }

        var text = v is JsonValue s && s.TryGetValue<string>(out var str) ? str : v.ToJsonString();
        return type switch
        {
            "INTEGER" or "INT64" when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
            "FLOAT" or "FLOAT64" when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            "NUMERIC" or "BIGNUMERIC" when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) => m,
            "BOOLEAN" or "BOOL" => text == "true",
            // Timestamps come as seconds since the epoch.
            "TIMESTAMP" when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) =>
                DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).ToString("O"),
            _ => text
        };
    }

    private async Task<(string Project, Action<HttpRequestMessage> Auth)> PrepareAsync(PluginConnection connection, CancellationToken ct)
    {
        var account = GoogleServiceAccountTokens.Parse(connection.Secret("service_account_json"));
        var project = connection.Setting("project_id") is { Length: > 0 } p ? p
            : account.ProjectId ?? throw new ArgumentException("Set the project id: the service account key doesn't name one.");
        return (project, GoogleServiceAccountTokens.Bearer(await tokens.GetTokenAsync(account, Scope, ct)));
    }

    private static int MaxRows(PluginConnection connection) =>
        int.TryParse(connection.Setting("max_rows", "1000"), out var max) ? Math.Clamp(max, 1, 50_000) : 1000;

    private static string? Location(PluginConnection connection) => connection.Setting("location") is { Length: > 0 } l ? l : null;

    /// <summary>BigQuery request ids are up to 36 characters: a hash of the idempotency key.</summary>
    private static string DeterministicRequestId(string key) =>
        new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(key))).ToString();

    private HttpClient Http => httpClientFactory.CreateClient("integrations");

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
