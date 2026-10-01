using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Api.Platform;
using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Interop;

public sealed class A2aSettings
{
    public const string SectionName = "A2a";

    /// <summary>Longest a blocking SendMessage waits before answering with the task still working.</summary>
    public int MaxWaitSeconds { get; set; } = 600;

    /// <summary>The API's public URL, for the agent card (blank: taken from the request).</summary>
    public string? PublicBaseUrl { get; set; }
}

/// <summary>
/// Aktor as an A2A agent (roadmap P5; https://a2a-protocol.org). Other agent frameworks (CrewAI and
/// anything built on the A2A SDKs) delegate a goal by sending a message; Aktor runs it as a task
/// through the same <see cref="TaskService"/> as REST and MCP, and answers with an A2A Task whose
/// artifact is the final TaskResult.
///
/// <para>Speaks both protocol versions in use: 1.0 (PascalCase methods, TASK_STATE_* enums, parts and
/// events discriminated by member name) when the client sends <c>A2A-Version: 1.0</c>, and 0.3
/// (message/send, lowercase states, "kind" discriminators) otherwise, which the spec says to assume
/// when the header is missing. Method names of either version are accepted under either.</para>
/// </summary>
public static class A2aEndpoint
{
    public const string Path = "/a2a";

    public static void MapA2a(this WebApplication app)
    {
        app.MapGet("/.well-known/agent-card.json", AgentCard).AllowAnonymous();
        // Older clients (A2A 0.2) look here.
        app.MapGet("/.well-known/agent.json", AgentCard).AllowAnonymous();
        app.MapPost(Path, HandleAsync);
    }

    private static bool IsV1(HttpContext http)
    {
        var v = http.Request.Headers["A2A-Version"].FirstOrDefault() ?? http.Request.Query["A2A-Version"].FirstOrDefault();
        return v is not null && (v.StartsWith("1", StringComparison.Ordinal));
    }

    private static string BaseUrl(HttpContext http, A2aSettings settings) =>
        (string.IsNullOrWhiteSpace(settings.PublicBaseUrl) ? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}" : settings.PublicBaseUrl).TrimEnd('/');

    private static IResult AgentCard(HttpContext http, IOptions<A2aSettings> settings)
    {
        var url = BaseUrl(http, settings.Value) + Path;
        var skill = new JsonObject
        {
            ["id"] = "run-goal",
            ["name"] = "Run a goal with a governed agent team",
            ["description"] = "Takes an open-ended goal, plans it, spawns specialist agents and works on it under budgets the " +
                              "server enforces. Returns a summary, findings from every agent, artifacts and metrics.",
            ["tags"] = new JsonArray("research", "analysis", "multi-agent", "planning"),
            ["examples"] = new JsonArray("Research the market for AI-powered property management software and recommend a positioning.",
                "Investigate why checkout errors spiked after the last deploy."),
            ["inputModes"] = new JsonArray("text/plain", "application/json"),
            ["outputModes"] = new JsonArray("text/plain", "application/json")
        };

        JsonObject card;
        if (IsV1(http))
        {
            card = new JsonObject
            {
                ["name"] = "Aktor",
                ["description"] = "Governed, budget-enforced autonomous agent teams. Send a goal; get a researched result.",
                ["supportedInterfaces"] = new JsonArray(
                    new JsonObject { ["url"] = url, ["protocolBinding"] = "JSONRPC", ["protocolVersion"] = "1.0" },
                    new JsonObject { ["url"] = url, ["protocolBinding"] = "JSONRPC", ["protocolVersion"] = "0.3" }),
                ["provider"] = new JsonObject { ["organization"] = "Aktor", ["url"] = BaseUrl(http, settings.Value) },
                ["version"] = "1.0.0",
                ["capabilities"] = new JsonObject { ["streaming"] = true, ["pushNotifications"] = false },
                ["securitySchemes"] = new JsonObject
                {
                    ["aktorApiKey"] = new JsonObject
                    {
                        ["httpAuthSecurityScheme"] = new JsonObject { ["scheme"] = "Bearer", ["bearerFormat"] = "ak_…", ["description"] = "An Aktor API key." }
                    }
                },
                ["securityRequirements"] = new JsonArray(new JsonObject { ["schemes"] = new JsonObject { ["aktorApiKey"] = new JsonObject { ["list"] = new JsonArray() } } }),
                ["defaultInputModes"] = new JsonArray("text/plain", "application/json"),
                ["defaultOutputModes"] = new JsonArray("text/plain", "application/json"),
                ["skills"] = new JsonArray(skill)
            };
        }
        else
        {
            card = new JsonObject
            {
                ["protocolVersion"] = "0.3.0",
                ["name"] = "Aktor",
                ["description"] = "Governed, budget-enforced autonomous agent teams. Send a goal; get a researched result.",
                ["url"] = url,
                ["preferredTransport"] = "JSONRPC",
                ["provider"] = new JsonObject { ["organization"] = "Aktor", ["url"] = BaseUrl(http, settings.Value) },
                ["version"] = "1.0.0",
                ["capabilities"] = new JsonObject { ["streaming"] = true, ["pushNotifications"] = false, ["stateTransitionHistory"] = false },
                ["securitySchemes"] = new JsonObject
                {
                    ["aktorApiKey"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer", ["bearerFormat"] = "ak_…", ["description"] = "An Aktor API key." }
                },
                ["security"] = new JsonArray(new JsonObject { ["aktorApiKey"] = new JsonArray() }),
                ["defaultInputModes"] = new JsonArray("text/plain", "application/json"),
                ["defaultOutputModes"] = new JsonArray("text/plain", "application/json"),
                ["skills"] = new JsonArray(skill)
            };
        }

        return Results.Json(card);
    }

    private sealed class RpcError(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private static async Task HandleAsync(HttpContext http, TaskService tasks, IOptions<A2aSettings> settings,
        IOptions<DefaultBudgetOptions> defaultBudget, CancellationToken ct)
    {
        var v1 = IsV1(http);
        JsonNode? id = null;
        JsonObject? request;
        try
        {
            request = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct) as JsonObject;
        }
        catch (JsonException)
        {
            await WriteAsync(http, Error(null, -32700, "Parse error"));
            return;
        }

        if (request is null || request["method"]?.GetValue<string>() is not { } method)
        {
            await WriteAsync(http, Error(null, -32600, "Invalid request"));
            return;
        }

        id = request["id"]?.DeepClone();
        var p = request["params"] as JsonObject ?? [];
        var caller = http.Caller();
        try
        {
            switch (method)
            {
                case "SendMessage" or "message/send":
                {
                    RequireMember(caller);
                    var started = await StartAsync(tasks, caller.TenantId, p, defaultBudget.Value, http, ct);
                    var block = v1 ? p["configuration"]?["returnImmediately"]?.GetValue<bool>() != true
                                   : p["configuration"]?["blocking"]?.GetValue<bool>() != false;
                    var view = block
                        ? await tasks.WaitAsync(caller.TenantId, started.TaskId, TimeSpan.FromSeconds(settings.Value.MaxWaitSeconds), ct: ct) ?? started
                        : started;
                    var task = await TaskObjectAsync(tasks, caller.TenantId, view, v1, HistoryLength(p), ct);
                    await WriteAsync(http, Result(id, v1 ? new JsonObject { ["task"] = task } : task));
                    return;
                }

                case "SendStreamingMessage" or "message/stream":
                {
                    RequireMember(caller);
                    var started = await StartAsync(tasks, caller.TenantId, p, defaultBudget.Value, http, ct);
                    await StreamAsync(http, tasks, caller.TenantId, started, id, v1, settings.Value, ct);
                    return;
                }

                case "SubscribeToTask" or "tasks/resubscribe":
                {
                    var view = await tasks.GetAsync(caller.TenantId, TaskId(p), ct) ?? throw new RpcError(-32001, "Task not found");
                    if (view.Done) throw new RpcError(-32004, "The task has finished; use GetTask for its result.");
                    await StreamAsync(http, tasks, caller.TenantId, view, id, v1, settings.Value, ct);
                    return;
                }

                case "GetTask" or "tasks/get":
                {
                    var view = await tasks.GetAsync(caller.TenantId, TaskId(p), ct) ?? throw new RpcError(-32001, "Task not found");
                    var task = await TaskObjectAsync(tasks, caller.TenantId, view, v1, HistoryLength(p), ct);
                    await WriteAsync(http, Result(id, task));
                    return;
                }

                case "CancelTask" or "tasks/cancel":
                {
                    RequireMember(caller);
                    var view = await tasks.GetAsync(caller.TenantId, TaskId(p), ct) ?? throw new RpcError(-32001, "Task not found");
                    if (view.Done) throw new RpcError(-32002, "Task cannot be canceled: it has already finished.");
                    try
                    {
                        view = await tasks.CancelAsync(caller.TenantId, view.TaskId, ct) ?? view;
                    }
                    catch (TaskServiceException ex)
                    {
                        throw new RpcError(-32002, ex.Message);
                    }

                    await WriteAsync(http, Result(id, await TaskObjectAsync(tasks, caller.TenantId, view with { State = "canceled" }, v1, 0, ct)));
                    return;
                }

                default:
                    throw new RpcError(-32601, $"Method not found: {method}");
            }
        }
        catch (RpcError ex)
        {
            if (!http.Response.HasStarted) await WriteAsync(http, Error(id, ex.Code, ex.Message));
        }
        catch (TaskServiceException ex)
        {
            if (!http.Response.HasStarted) await WriteAsync(http, Error(id, ex.StatusCode == 404 ? -32001 : -32602, ex.Message));
        }
    }

    private static void RequireMember(Infrastructure.Identity.Caller caller)
    {
        if (caller.Role < Tenancy.TenantRole.Member) throw new RpcError(-32004, "This API key may read tasks but not start or cancel them (it needs the Member role).");
    }

    private static string TaskId(JsonObject p) => p["id"]?.GetValue<string>() ?? throw new RpcError(-32602, "params.id is required");

    private static int HistoryLength(JsonObject p) =>
        (p["historyLength"] ?? p["configuration"]?["historyLength"]) is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    /// <summary>The goal is the message's text (data parts appended as JSON). Options ride in
    /// metadata: budget, workspace, correlation_id, team_policy — the same as MCP's run_goal.</summary>
    private static async Task<TaskView> StartAsync(TaskService tasks, string tenantId, JsonObject p, DefaultBudgetOptions defaults,
        HttpContext http, CancellationToken ct)
    {
        var message = p["message"] as JsonObject ?? throw new RpcError(-32602, "params.message is required");
        var goal = new StringBuilder();
        foreach (var part in message["parts"] as JsonArray ?? [])
        {
            if (part?["text"] is JsonValue textPart) goal.AppendLine(textPart.GetValue<string>());
            else if (part?["data"] is { } data) goal.AppendLine(data.ToJsonString());
        }

        if (goal.Length == 0) throw new RpcError(-32602, "The message has no text to use as a goal.");

        var meta = (message["metadata"] as JsonObject) ?? (p["metadata"] as JsonObject) ?? [];
        var aktor = meta["aktor"] as JsonObject ?? meta;
        var budget = aktor["budget"] is JsonObject b ? b.Deserialize<McpBudget>(McpJson.Options)?.ToBudget(defaults.ToBudget()) : null;

        // Follow-ups: a message naming a finished task starts a new one that builds on its result.
        var text = goal.ToString().Trim();
        if (message["taskId"]?.GetValue<string>() is { Length: > 0 } previousId)
        {
            var previous = await tasks.GetAsync(tenantId, previousId, ct) ?? throw new RpcError(-32001, "Task not found");
            if (!previous.Done) throw new RpcError(-32004, "That task is still running; Aktor tasks don't take input mid-run. Wait for it, or cancel it.");
            text = $"{text}\n\nThis follows up an earlier task. Its result: {previous.Summary}";
        }

        return await tasks.StartAsync(tenantId, new StartTaskRequest
        {
            Goal = text,
            Budget = budget,
            WorkspaceId = aktor["workspace"]?.GetValue<string>(),
            // The A2A context is the correlation id: every task of one conversation shares it.
            CorrelationId = aktor["correlation_id"]?.GetValue<string>() ?? message["contextId"]?.GetValue<string>(),
            TeamPolicy = aktor["team_policy"] is JsonObject tp ? tp.Deserialize<Safety.TeamPolicy>(McpJson.Options) : null,
            Source = "a2a"
        }, ct);
    }

    private static async Task StreamAsync(HttpContext http, TaskService tasks, string tenantId, TaskView started, JsonNode? id, bool v1,
        A2aSettings settings, CancellationToken ct)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        var send = new SemaphoreSlim(1, 1);

        async Task Emit(JsonObject payload)
        {
            await send.WaitAsync(ct);
            try
            {
                await http.Response.WriteAsync($"data: {Result(id?.DeepClone(), payload).ToJsonString()}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
            finally
            {
                send.Release();
            }
        }

        var first = await TaskObjectAsync(tasks, tenantId, started, v1, 0, ct);
        await Emit(v1 ? new JsonObject { ["task"] = first } : first);

        var done = await tasks.WaitAsync(tenantId, started.TaskId, TimeSpan.FromSeconds(Math.Max(settings.MaxWaitSeconds, (started.Budget?.MaxDurationSeconds ?? 900) + 60)),
            p => new ValueTask(Emit(StatusUpdate(started, State(started with { State = "working" }, v1), AgentText(p.Message, started, v1), final: false, v1))), ct) ?? started;

        var task = await TaskObjectAsync(tasks, tenantId, done, v1, 0, ct);
        foreach (var artifact in task["artifacts"] as JsonArray ?? [])
        {
            var update = new JsonObject
            {
                ["taskId"] = done.TaskId,
                ["contextId"] = ContextId(done),
                ["artifact"] = artifact!.DeepClone(),
                ["append"] = false,
                ["lastChunk"] = true
            };
            await Emit(v1 ? new JsonObject { ["artifactUpdate"] = update } : Kind("artifact-update", update));
        }

        await Emit(StatusUpdate(done, State(done, v1), task["status"]?["message"]?.DeepClone() as JsonObject, final: done.Done, v1));
    }

    private static JsonObject StatusUpdate(TaskView view, string state, JsonObject? message, bool final, bool v1)
    {
        var status = new JsonObject { ["state"] = state, ["timestamp"] = DateTimeOffset.UtcNow.ToString("O") };
        if (message is not null) status["message"] = message;
        var update = new JsonObject { ["taskId"] = view.TaskId, ["contextId"] = ContextId(view), ["status"] = status };
        if (v1) return new JsonObject { ["statusUpdate"] = update };
        update["final"] = final;
        return Kind("status-update", update);
    }

    private static string ContextId(TaskView view) => view.CorrelationId ?? view.TaskId;

    private static string State(TaskView view, bool v1)
    {
        var s = view.State switch
        {
            "completed" => "completed",
            "failed" => "failed",
            "canceled" => "canceled",
            _ => "working"
        };
        return v1 ? "TASK_STATE_" + s.ToUpperInvariant() : s;
    }

    private static JsonObject AgentText(string text, TaskView view, bool v1)
    {
        var part = v1 ? new JsonObject { ["text"] = text } : Kind("text", new JsonObject { ["text"] = text });
        var message = new JsonObject
        {
            ["messageId"] = Guid.NewGuid().ToString("n"),
            ["role"] = v1 ? "ROLE_AGENT" : "agent",
            ["parts"] = new JsonArray(part),
            ["taskId"] = view.TaskId,
            ["contextId"] = ContextId(view)
        };
        return v1 ? message : Kind("message", message);
    }

    private static JsonObject Kind(string kind, JsonObject o)
    {
        var withKind = new JsonObject { ["kind"] = kind };
        foreach (var (k, v) in o) withKind[k] = v?.DeepClone();
        return withKind;
    }

    /// <summary>The A2A Task for one of ours: its state, a status message (the summary, once done),
    /// the final TaskResult as an artifact, and Aktor's own links in metadata.</summary>
    private static async Task<JsonObject> TaskObjectAsync(TaskService tasks, string tenantId, TaskView view, bool v1, int historyLength, CancellationToken ct)
    {
        var status = new JsonObject
        {
            ["state"] = State(view, v1),
            ["timestamp"] = (view.CompletedAt ?? DateTimeOffset.UtcNow).ToString("O")
        };

        var artifacts = new JsonArray();
        if (view.Done)
        {
            var result = await tasks.GetResultAsync(tenantId, view.TaskId, ct);
            var summary = view.Summary ?? "The task finished.";
            status["message"] = AgentText(summary, view, v1);

            var text = new StringBuilder(summary);
            if (result?.Result is { } r && r.TryGetProperty("findings", out var findings) && findings.GetArrayLength() > 0)
            {
                text.Append("\n\nFindings:\n");
                foreach (var f in findings.EnumerateArray()) text.Append("- ").AppendLine(f.GetString());
            }

            if (result?.Replies is { Count: > 0 } replies) text.Append("\n\n").AppendJoin("\n", replies);
            text.Append($"\n\nFull run: {view.DashboardUrl}");

            var parts = new JsonArray(v1 ? new JsonObject { ["text"] = text.ToString() } : Kind("text", new JsonObject { ["text"] = text.ToString() }));
            if (result?.Result is { } data)
            {
                var dataNode = JsonNode.Parse(data.GetRawText());
                parts.Add(v1 ? new JsonObject { ["data"] = dataNode, ["mediaType"] = "application/json" } : Kind("data", new JsonObject { ["data"] = dataNode }));
            }

            artifacts.Add(new JsonObject
            {
                ["artifactId"] = $"{view.TaskId}-result",
                ["name"] = "task_result",
                ["description"] = "The team's final result: summary, findings from every agent, artifacts and metrics.",
                ["parts"] = parts
            });
        }

        var task = new JsonObject
        {
            ["id"] = view.TaskId,
            ["contextId"] = ContextId(view),
            ["status"] = status,
            ["artifacts"] = artifacts,
            ["metadata"] = new JsonObject
            {
                ["aktor"] = new JsonObject
                {
                    ["dashboard_url"] = view.DashboardUrl,
                    ["correlation_id"] = view.CorrelationId,
                    ["agents"] = view.AgentsTotal,
                    ["tokens_used"] = view.TokensUsed,
                    ["cost_usd"] = view.CostUsd,
                    ["kind"] = view.Kind
                }
            }
        };

        if (historyLength > 0)
        {
            var user = new JsonObject
            {
                ["messageId"] = view.TaskId + "-goal",
                ["role"] = v1 ? "ROLE_USER" : "user",
                ["parts"] = new JsonArray(v1 ? new JsonObject { ["text"] = view.Goal } : Kind("text", new JsonObject { ["text"] = view.Goal })),
                ["taskId"] = view.TaskId,
                ["contextId"] = ContextId(view)
            };
            task["history"] = new JsonArray(v1 ? user : Kind("message", user));
        }

        return v1 ? task : Kind("task", task);
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static Task WriteAsync(HttpContext http, JsonObject body)
    {
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(body.ToJsonString());
    }
}
