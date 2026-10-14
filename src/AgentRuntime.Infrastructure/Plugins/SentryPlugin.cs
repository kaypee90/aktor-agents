using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;

namespace AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// Sentry: an organization's projects and issues, an issue's latest event (exception and stack
/// trace, trimmed to what helps), and, when writes are allowed, resolving, ignoring or assigning
/// issues. <c>list_issues</c> is read-only, so a watch can alert on new unresolved issues with no model.
/// Works with sentry.io (US or EU) and self-hosted Sentry.
/// </summary>
public sealed class SentryPlugin(IHttpClientFactory httpClientFactory) : IToolProviderPlugin
{
    private const int MaxFrames = 12;

    public PluginManifest Manifest { get; } = new()
    {
        Id = "sentry",
        Name = "Sentry",
        Description = "Read Sentry issues and their stack traces, and (optionally) resolve, ignore or assign them.",
        Category = PluginCategory.Tools,
        Settings =
        [
            new() { Key = "organization", Label = "Organization slug", Required = true, Placeholder = "my-company", Description = "From your Sentry URL: sentry.io/organizations/<slug>/" },
            new() { Key = "base_url", Label = "Sentry URL", DefaultValue = "https://sentry.io", Placeholder = "https://sentry.io, https://de.sentry.io or your own" },
            new() { Key = "allow_writes", Label = "Allow changes (resolve, ignore, assign)", Options = ["false", "true"], DefaultValue = "false" },
            new() { Key = "auth_token", Label = "Auth token", Secret = true, Required = true, Placeholder = "sntrys_… or sntryu_…" }
        ],
        SetupHelp = "Create an internal integration (Settings → Developer Settings → Custom Integrations) or a personal token with " +
                    "Issue & Event Read and Project Read (plus Issue & Event Write to resolve or assign), and paste its token."
    };

    public Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken) => Connector.Check(async () =>
    {
        var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, $"{Org(connection)}/", Auth(connection), null, cancellationToken);
        return status == 200
            ? ConnectionCheck.Success($"Connected to the Sentry organization '{Connector.At(body, "name")}'.")
            : ConnectionCheck.Failure(Connector.Fail("Sentry", status, body, text).ErrorMessage!);
    });

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken)
    {
        const string issue = """ "issue_id": { "type": "string", "description": "The issue's numeric id (or short id like PROJ-1A)." } """;
        var tools = new List<ToolDefinition>
        {
            Connector.Tool("list_projects", "The organization's projects: slug, id and platform.", ToolSideEffects.ReadOnly, """{ "type": "object", "properties": {} }"""),
            Connector.Tool("list_issues", "Issues matching a Sentry search, newest activity first.", ToolSideEffects.ReadOnly, """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Sentry search, e.g. is:unresolved level:error. Default is:unresolved." },
                    "project": { "type": "string", "description": "A project's numeric id (list_projects); all projects when left out." },
                    "environment": { "type": "string" },
                    "period": { "type": "string", "description": "How far back: 1h, 24h, 14d (default 24h)." },
                    "sort": { "type": "string", "enum": ["date", "new", "freq", "user"], "description": "date: last seen; new: first seen; freq: events; user: users affected." },
                    "limit": { "type": "integer", "description": "Default 25, at most 100." }
                  }
                }
                """),
            Connector.Tool("get_issue", "One issue: title, level, status, counts, when it was first and last seen, and its link.", ToolSideEffects.ReadOnly,
                $$"""{ "type": "object", "properties": { {{issue}} }, "required": ["issue_id"] }"""),
            Connector.Tool("get_latest_event", "The issue's latest event: the exception, the stack trace's last frames, tags, release and request.", ToolSideEffects.ReadOnly,
                $$"""{ "type": "object", "properties": { {{issue}} }, "required": ["issue_id"] }""")
        };

        if (Connector.WritesAllowed(connection))
        {
            tools.Add(Connector.Tool("update_issue", "Resolve, unresolve or ignore an issue, or assign it to someone.", ToolSideEffects.Idempotent, $$"""
                {
                  "type": "object",
                  "properties": {
                    {{issue}},
                    "status": { "type": "string", "enum": ["resolved", "unresolved", "ignored"] },
                    "assigned_to": { "type": "string", "description": "A member's email or username, or team:<slug>; \"\" to unassign." }
                  },
                  "required": ["issue_id"]
                }
                """));
        }

        return Task.FromResult<IReadOnlyList<ToolDefinition>>(tools);
    }

    public Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request) => Connector.Guard(async () =>
    {
        var args = Connector.Args(request);
        var ct = request.CancellationToken;
        var auth = Auth(connection);
        string IssueUrl() => $"{Org(connection)}/issues/{Uri.EscapeDataString(Connector.Required(args, "issue_id"))}/";

        switch (toolName)
        {
            case "list_projects":
            {
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, $"{Org(connection)}/projects/", auth, null, ct);
                if (status != 200) return Connector.Fail("Sentry", status, body, text);
                return Connector.Ok(new JsonObject
                {
                    ["projects"] = new JsonArray([.. (body as JsonArray ?? []).Select(p => (JsonNode?)new JsonObject
                    {
                        ["id"] = Connector.At(p, "id"),
                        ["slug"] = Connector.At(p, "slug"),
                        ["name"] = Connector.At(p, "name"),
                        ["platform"] = Connector.At(p, "platform")
                    })])
                });
            }

            case "list_issues":
            {
                var query = Connector.Str(args, "query") is { Length: > 0 } q ? q : "is:unresolved";
                var url = $"{Org(connection)}/issues/?query={Uri.EscapeDataString(query)}" +
                          $"&statsPeriod={Uri.EscapeDataString(Connector.Str(args, "period") is { Length: > 0 } p ? p : "24h")}" +
                          $"&sort={Uri.EscapeDataString(Connector.Str(args, "sort") is { Length: > 0 } s ? s : "date")}" +
                          $"&limit={Connector.Int(args, "limit", 25, 1, 100)}";
                if (Connector.Str(args, "project") is { Length: > 0 } project) url += $"&project={Uri.EscapeDataString(project)}";
                if (Connector.Str(args, "environment") is { Length: > 0 } env) url += $"&environment={Uri.EscapeDataString(env)}";
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, url, auth, null, ct);
                if (status != 200) return Connector.Fail("Sentry", status, body, text);
                var issues = new JsonArray([.. (body as JsonArray ?? []).Select(i => (JsonNode?)Issue(i))]);
                return Connector.Ok(new JsonObject { ["issues"] = issues, ["count"] = issues.Count });
            }

            case "get_issue":
            {
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, IssueUrl(), auth, null, ct);
                return status == 200 ? Connector.Ok(Issue(body)) : Connector.Fail("Sentry", status, body, text);
            }

            case "get_latest_event":
            {
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, $"{IssueUrl()}events/latest/", auth, null, ct);
                return status == 200 ? Connector.Ok(Event(body)) : Connector.Fail("Sentry", status, body, text);
            }

            case "update_issue" when Connector.WritesAllowed(connection):
            {
                var payload = new JsonObject();
                if (Connector.Str(args, "status") is { Length: > 0 } newStatus)
                {
                    if (newStatus is not ("resolved" or "unresolved" or "ignored")) throw new ArgumentException("status must be resolved, unresolved or ignored.");
                    payload["status"] = newStatus;
                }

                if (args.ContainsKey("assigned_to")) payload["assignedTo"] = Connector.Str(args, "assigned_to") ?? "";
                if (payload.Count == 0) throw new ArgumentException("Give a status, an assigned_to, or both.");
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Put, IssueUrl(), auth, payload, ct);
                return status == 200 ? Connector.Ok(Issue(body)) : Connector.Fail("Sentry", status, body, text);
            }

            default:
                return ToolExecutionResult.Fail($"Unknown or disabled tool '{toolName}'.");
        }
    });

    /// <summary>The parts of an issue worth a model's attention.</summary>
    private static JsonObject Issue(JsonNode? i) => new()
    {
        ["id"] = Connector.At(i, "id"),
        ["short_id"] = Connector.At(i, "shortId"),
        ["title"] = Connector.At(i, "title"),
        ["culprit"] = Connector.At(i, "culprit"),
        ["level"] = Connector.At(i, "level"),
        ["status"] = Connector.At(i, "status"),
        ["project"] = Connector.At(i, "project", "slug"),
        ["events"] = Connector.At(i, "count"),
        ["users"] = i?["userCount"]?.DeepClone(),
        ["first_seen"] = Connector.At(i, "firstSeen"),
        ["last_seen"] = Connector.At(i, "lastSeen"),
        ["assigned_to"] = Connector.At(i, "assignedTo", "name") ?? Connector.At(i, "assignedTo", "email"),
        ["link"] = Connector.At(i, "permalink")
    };

    /// <summary>An event, trimmed: the exception(s) with the last frames of each stack trace (the
    /// app's own first), plus tags, release and request. Full events run to hundreds of kilobytes.</summary>
    private static JsonObject Event(JsonNode? e)
    {
        var exceptions = new JsonArray();
        foreach (var entry in e?["entries"] as JsonArray ?? [])
        {
            if (Connector.At(entry, "type") != "exception") continue;
            foreach (var ex in entry?["data"]?["values"] as JsonArray ?? [])
            {
                var frames = (ex?["stacktrace"]?["frames"] as JsonArray ?? []).ToList();
                var shown = frames.Where(f => Connector.At(f, "inApp") == "true").TakeLast(MaxFrames).ToList();
                if (shown.Count == 0) shown = frames.TakeLast(MaxFrames).ToList();
                exceptions.Add(new JsonObject
                {
                    ["type"] = Connector.At(ex, "type"),
                    ["value"] = Connector.At(ex, "value"),
                    ["frames"] = new JsonArray([.. shown.Select(f => (JsonNode?)new JsonObject
                    {
                        ["file"] = Connector.At(f, "filename") ?? Connector.At(f, "module"),
                        ["function"] = Connector.At(f, "function"),
                        ["line"] = f?["lineNo"]?.DeepClone(),
                        ["code"] = Connector.At(f, "context") is null ? null : ContextLine(f)
                    })])
                });
            }
        }

        JsonNode? request = null;
        if ((e?["entries"] as JsonArray ?? []).FirstOrDefault(x => Connector.At(x, "type") == "request") is { } r)
        {
            request = new JsonObject { ["method"] = Connector.At(r, "data", "method"), ["url"] = Connector.At(r, "data", "url") };
        }

        return new JsonObject
        {
            ["event_id"] = Connector.At(e, "eventID") ?? Connector.At(e, "id"),
            ["message"] = Connector.At(e, "message") ?? Connector.At(e, "title"),
            ["date"] = Connector.At(e, "dateCreated"),
            ["platform"] = Connector.At(e, "platform"),
            ["release"] = Connector.At(e, "release", "version"),
            ["environment"] = (e?["tags"] as JsonArray ?? []).FirstOrDefault(t => Connector.At(t, "key") == "environment") is { } env ? Connector.At(env, "value") : null,
            ["tags"] = new JsonObject((e?["tags"] as JsonArray ?? []).Take(20)
                .Select(t => KeyValuePair.Create(Connector.At(t, "key") ?? "", (JsonNode?)Connector.At(t, "value")))
                .Where(kv => kv.Key.Length > 0)
                .DistinctBy(kv => kv.Key)),
            ["exceptions"] = exceptions,
            ["request"] = request
        };
    }

    /// <summary>The frame's own line of code, from its [line number, text] context pairs.</summary>
    private static string? ContextLine(JsonNode? frame)
    {
        var line = frame?["lineNo"]?.ToString();
        foreach (var pair in frame?["context"] as JsonArray ?? [])
        {
            if (pair is JsonArray { Count: 2 } p && p[0]?.ToString() == line) return p[1]?.ToString()?.Trim();
        }

        return null;
    }

    private static string Org(PluginConnection c)
    {
        var slug = c.Setting("organization");
        if (slug.Length == 0 || !slug.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) throw new ArgumentException("Set the organization's slug, e.g. my-company.");
        var baseUrl = c.Setting("base_url", "https://sentry.io").TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new ArgumentException("The Sentry URL must start with https://.");
        return $"{baseUrl}/api/0/organizations/{slug}";
    }

    private static Action<HttpRequestMessage> Auth(PluginConnection c)
    {
        var token = c.Secret("auth_token");
        return r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private HttpClient Http => httpClientFactory.CreateClient("integrations");
}
