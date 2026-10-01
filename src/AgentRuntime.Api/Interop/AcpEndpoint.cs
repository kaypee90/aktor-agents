using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Api.Platform;
using AgentRuntime.Contracts;
using AgentRuntime.Events;
using AgentRuntime.Tenancy;

namespace AgentRuntime.Api.Interop;

/// <summary>
/// Aktor as an Agent Client Protocol agent (roadmap P5; https://agentclientprotocol.com, protocol
/// version 1). ACP is how OpenClaw (through acpx) and editors such as Zed drive external agents.
/// Those clients launch agents as local processes speaking JSON-RPC on stdio; the
/// <c>aktor-acp</c> bridge in sdk/typescript is that process, and relays each message to this
/// WebSocket endpoint, which runs the session against the same <see cref="TaskService"/> as REST,
/// MCP and A2A.
///
/// <para>A session maps onto tasks: each <c>session/prompt</c> starts a task (a follow-up prompt
/// builds on the previous task's result), streams the team's progress as <c>session/update</c>
/// notifications (a plan with one entry per agent, a tool call per agent started, message chunks),
/// and answers with the final result and <c>stopReason: end_turn</c>. <c>session/cancel</c> stops
/// the task's agents and the prompt answers <c>cancelled</c>.</para>
/// </summary>
public static class AcpEndpoint
{
    public const string Path = "/acp";
    public const int ProtocolVersion = 1;

    public static void MapAcp(this WebApplication app) => app.Map(Path, async (HttpContext http, TaskService tasks, IEventStream events,
        Microsoft.Extensions.Options.IOptions<A2aSettings> settings, ILoggerFactory logs) =>
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "Connect with a WebSocket (the aktor-acp bridge does this for stdio ACP clients)." });
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var session = new AcpConnection(socket, tasks, events, http.Caller(), logs.CreateLogger("Aktor.Acp"));
        await session.RunAsync(http.RequestAborted);
    });
}

internal sealed class AcpConnection(WebSocket socket, TaskService tasks, IEventStream events, Infrastructure.Identity.Caller caller, ILogger logger)
{
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<string, AcpSession> _sessions = new();

    private sealed class AcpSession
    {
        public required string Id { get; init; }
        public string? LastTaskId { get; set; }
        public string? RunningTaskId { get; set; }
        public CancellationTokenSource? Running { get; set; }
        /// <summary>A prompt is being handled (set before its first await, so a cancel read right
        /// after the prompt still finds it).</summary>
        public bool InFlight { get; set; }
        public bool Cancelled { get; set; }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var pending = new List<Task>();
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, ct);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    foreach (var s in _sessions.Values) s.Running?.Cancel();
                    return;
                }

                message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);

            // A frame may hold one message or several, newline-delimited (as on stdio).
            foreach (var line in Encoding.UTF8.GetString(message.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                JsonObject? rpc;
                try
                {
                    rpc = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    await SendAsync(Error(null, -32700, "Parse error"));
                    continue;
                }

                if (rpc is null) continue;
                // Prompts run for minutes, so each request is handled on its own; a cancel
                // notification for a running prompt is read while it runs.
                pending.RemoveAll(t => t.IsCompleted);
                pending.Add(HandleAsync(rpc, ct));
            }
        }

        await Task.WhenAll(pending);
    }

    private async Task HandleAsync(JsonObject rpc, CancellationToken ct)
    {
        var id = rpc["id"]?.DeepClone();
        var method = rpc["method"]?.GetValue<string>();
        var p = rpc["params"] as JsonObject ?? [];
        if (method is null) return; // a response to something we never ask; ignore

        try
        {
            switch (method)
            {
                case "initialize":
                    await RespondAsync(id, new JsonObject
                    {
                        ["protocolVersion"] = AcpEndpoint.ProtocolVersion,
                        ["agentCapabilities"] = new JsonObject
                        {
                            ["loadSession"] = false,
                            ["promptCapabilities"] = new JsonObject { ["image"] = false, ["audio"] = false, ["embeddedContext"] = true },
                            ["mcpCapabilities"] = new JsonObject { ["http"] = false, ["sse"] = false }
                        },
                        ["authMethods"] = new JsonArray(),
                        ["agentInfo"] = new JsonObject { ["name"] = "aktor", ["title"] = "Aktor governed agent teams", ["version"] = "1.0.0" }
                    });
                    return;

                case "authenticate":
                    // The bridge authenticated the connection with the API key already.
                    await RespondAsync(id, new JsonObject());
                    return;

                case "session/new":
                {
                    var session = new AcpSession { Id = "acp-" + Guid.NewGuid().ToString("n")[..16] };
                    _sessions[session.Id] = session;
                    await RespondAsync(id, new JsonObject { ["sessionId"] = session.Id });
                    return;
                }

                case "session/prompt":
                    await PromptAsync(id, p, ct);
                    return;

                case "session/cancel":
                    if (p["sessionId"]?.GetValue<string>() is { } sid && _sessions.TryGetValue(sid, out var cancelled) && cancelled.InFlight)
                    {
                        cancelled.Cancelled = true;
                        cancelled.Running?.Cancel();
                    }

                    return; // a notification: no response

                default:
                    if (id is not null) await SendAsync(Error(id, -32601, $"Method not found: {method}"));
                    return;
            }
        }
        catch (TaskServiceException ex)
        {
            if (id is not null) await SendAsync(Error(id, -32602, ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ACP request {Method} failed", method);
            if (id is not null) await SendAsync(Error(id, -32603, "Internal error: " + ex.Message));
        }
    }

    private async Task PromptAsync(JsonNode? id, JsonObject p, CancellationToken ct)
    {
        var sessionId = p["sessionId"]?.GetValue<string>();
        if (sessionId is null || !_sessions.TryGetValue(sessionId, out var session))
        {
            await SendAsync(Error(id, -32602, "Unknown sessionId; call session/new first."));
            return;
        }

        session.InFlight = true;
        session.Cancelled = false;
        try
        {
            await RunPromptAsync(id, p, session, ct);
        }
        finally
        {
            session.InFlight = false;
        }
    }

    private async Task RunPromptAsync(JsonNode? id, JsonObject p, AcpSession session, CancellationToken ct)
    {
        if (caller.Role < TenantRole.Member)
        {
            await SendAsync(Error(id, -32602, "This API key may not start tasks (it needs the Member role)."));
            return;
        }

        var goal = new StringBuilder();
        foreach (var block in p["prompt"] as JsonArray ?? [])
        {
            switch (block?["type"]?.GetValue<string>())
            {
                case "text":
                    goal.AppendLine(block["text"]?.GetValue<string>());
                    break;
                case "resource" when (block["resource"]?["text"]) is JsonValue embedded:
                    var uri = block["resource"]?["uri"]?.ToString();
                    var body = embedded.GetValue<string>();
                    goal.AppendLine("[" + uri + "]\n" + body);
                    break;
                case "resource_link":
                    goal.AppendLine("(See " + block["uri"] + ")");
                    break;
            }
        }

        if (goal.Length == 0)
        {
            await SendAsync(Error(id, -32602, "The prompt has no text."));
            return;
        }

        var text = goal.ToString().Trim();
        if (session.LastTaskId is { } previousId && await tasks.GetAsync(caller.TenantId, previousId, ct) is { Summary: { } previousSummary })
        {
            text = $"{text}\n\nThis follows up an earlier request in the same session. Its result: {previousSummary}";
        }

        var started = await tasks.StartAsync(caller.TenantId, new StartTaskRequest { Goal = text, Source = "acp", CorrelationId = session.Id }, ct);
        session.LastTaskId = started.TaskId;
        session.RunningTaskId = started.TaskId;
        using var running = CancellationTokenSource.CreateLinkedTokenSource(ct);
        session.Running = running;
        if (session.Cancelled) await running.CancelAsync(); // cancelled while the task was being created

        await UpdateAsync(session.Id, Chunk($"Started an Aktor agent team (task {started.TaskId}). Live view: {started.DashboardUrl}\n"));

        var plan = new Dictionary<string, (string Content, string Status)>();
        var watch = WatchTeamAsync(session, started, plan, running.Token);

        TaskView? done = null;
        try
        {
            var timeout = TimeSpan.FromSeconds((started.Budget?.MaxDurationSeconds ?? 900) + 120);
            done = await tasks.WaitAsync(caller.TenantId, started.TaskId, timeout, ct: running.Token);
        }
        catch (OperationCanceledException) when (session.Cancelled)
        {
            // session/cancel: handled below.
        }
        finally
        {
            await running.CancelAsync();
            await watch.ContinueWith(_ => { }, CancellationToken.None);
            session.Running = null;
            session.RunningTaskId = null;
        }

        if (session.Cancelled)
        {
            await tasks.CancelAsync(caller.TenantId, started.TaskId, CancellationToken.None);
            await RespondAsync(id, new JsonObject { ["stopReason"] = "cancelled" });
            return;
        }

        done ??= await tasks.GetAsync(caller.TenantId, started.TaskId, ct);
        var result = done is { Done: true } ? await tasks.GetResultAsync(caller.TenantId, done.TaskId, ct) : null;
        var final = new StringBuilder();
        if (done is not { Done: true })
        {
            final.Append($"The team is still working; follow it at {started.DashboardUrl}.");
        }
        else
        {
            final.Append(done.State == "completed" ? "" : $"The task ended {done.State}. ").Append(done.Summary ?? "The task finished.");
            if (result?.Result is { } r && r.TryGetProperty("findings", out var findings) && findings.GetArrayLength() > 0)
            {
                final.Append("\n\nFindings:\n");
                foreach (var f in findings.EnumerateArray()) final.Append("- ").AppendLine(f.GetString());
            }

            final.Append($"\n\n{done.AgentsTotal} agents, {done.TokensUsed:N0} tokens, ${done.CostUsd:F4}. Full run: {done.DashboardUrl}");
        }

        await UpdateAsync(session.Id, Chunk(final.ToString()));
        await RespondAsync(id, new JsonObject { ["stopReason"] = "end_turn" });
    }

    /// <summary>The team as the client sees it: a plan with one entry per agent, and a tool call
    /// per agent started (in progress until it finishes).</summary>
    private async Task WatchTeamAsync(AcpSession session, TaskView task, Dictionary<string, (string Content, string Status)> plan, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in events.Subscribe(ct))
            {
                if (!TenantIds.Same(evt.TenantId, caller.TenantId) || evt.TaskId != task.TaskId) continue;
                switch (evt.Type)
                {
                    case RuntimeEventType.AgentSpawned when evt.TargetAgentId is { } child:
                        plan[child] = (evt.Summary, "in_progress");
                        await UpdateAsync(session.Id, new JsonObject
                        {
                            ["sessionUpdate"] = "tool_call",
                            ["toolCallId"] = child,
                            ["title"] = evt.Summary,
                            ["kind"] = "think",
                            ["status"] = "in_progress"
                        });
                        await UpdateAsync(session.Id, Plan(plan));
                        break;

                    case RuntimeEventType.AgentCompleted or RuntimeEventType.AgentFailed or RuntimeEventType.AgentTerminated
                        when evt.AgentId is { } finished && plan.ContainsKey(finished):
                        var ok = evt.Type == RuntimeEventType.AgentCompleted;
                        plan[finished] = (plan[finished].Content, "completed");
                        var update = new JsonObject
                        {
                            ["sessionUpdate"] = "tool_call_update",
                            ["toolCallId"] = finished,
                            ["status"] = ok ? "completed" : "failed"
                        };
                        if (evt.Data.TryGetValue("summary", out var summary))
                        {
                            update["content"] = new JsonArray(new JsonObject
                            {
                                ["type"] = "content",
                                ["content"] = new JsonObject { ["type"] = "text", ["text"] = summary }
                            });
                        }

                        await UpdateAsync(session.Id, update);
                        await UpdateAsync(session.Id, Plan(plan));
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The prompt finished.
        }
    }

    private static JsonObject Plan(Dictionary<string, (string Content, string Status)> plan) => new()
    {
        ["sessionUpdate"] = "plan",
        ["entries"] = new JsonArray(plan.Values.Select(e => (JsonNode)new JsonObject
        {
            ["content"] = e.Content,
            ["priority"] = "medium",
            ["status"] = e.Status
        }).ToArray())
    };

    private static JsonObject Chunk(string text) => new()
    {
        ["sessionUpdate"] = "agent_message_chunk",
        ["content"] = new JsonObject { ["type"] = "text", ["text"] = text }
    };

    private Task UpdateAsync(string sessionId, JsonObject update) => SendAsync(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "session/update",
        ["params"] = new JsonObject { ["sessionId"] = sessionId, ["update"] = update }
    });

    private Task RespondAsync(JsonNode? id, JsonNode result) => SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private async Task SendAsync(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _send.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
        }
        finally
        {
            _send.Release();
        }
    }
}
