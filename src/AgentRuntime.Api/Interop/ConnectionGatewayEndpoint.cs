using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using AgentRuntime.Api.Platform;
using AgentRuntime.Contracts;
using AgentRuntime.Integrations;
using AgentRuntime.Safety;
using AgentRuntime.Tenancy;
using AgentRuntime.Tools;
using AgentRuntime.Workspaces;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Api.Interop;

/// <summary>Calls per minute per gateway, shared by every caller of that connection.</summary>
public sealed class GatewayRateLimiter(IOptions<IntegrationsOptions> options)
{
    private readonly ConcurrentDictionary<string, FixedWindowRateLimiter> _limiters = new();

    public bool TryAcquire(string key)
    {
        var limiter = _limiters.GetOrAdd(key, _ => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, options.Value.GatewayCallsPerMinute),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
        using var lease = limiter.AttemptAcquire();
        return lease.IsAcquired;
    }
}

/// <summary>
/// A workspace connection served as its own MCP server (docs/plugins.md, "MCP gateway"): another
/// agent (Claude Code, n8n, a CrewAI crew…) calls the connection's tools directly, e.g. your REST
/// API's endpoints, with no Aktor agent team in between. The runtime still governs every call:
/// <list type="bullet">
/// <item>an API key (or session) of the workspace's organization; listing needs Viewer, calling Member;</item>
/// <item>only the tools the connection's owner chose to serve, while the workspace is active;</item>
/// <item>the organization's and the workspace's safety policies (a call that needs approval is refused:
/// nobody is there to approve it);</item>
/// <item>the stored credential is used inside the server and never returned;</item>
/// <item>a rate limit per connection, and every call in the workspace's audit log.</item>
/// </list>
/// Streamable HTTP, stateless, answering each JSON-RPC request with one JSON response.
/// </summary>
public static class ConnectionGatewayEndpoint
{
    public const string Route = "/mcp/gateway/{workspaceId}/{connectionId}";
    private static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    private const int MaxBodyBytes = 1_000_000;

    public static void MapConnectionGateway(this WebApplication app)
    {
        app.MapPost(Route, HandleAsync);
        // No server-to-client stream and no sessions: the spec's answer is 405.
        app.MapGet(Route, () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        app.MapDelete(Route, () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
    }

    private sealed class RpcError(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private static async Task HandleAsync(string workspaceId, string connectionId, HttpContext http, IGrainFactory grains,
        IntegrationService integrations, IAuditLog audit, GatewayRateLimiter limiter, ILoggerFactory loggers, CancellationToken ct)
    {
        var caller = http.Caller();
        var workspace = grains.GetGrain<IWorkspaceGrain>(workspaceId);
        if (!WorkspaceIds.IsWorkspace(workspaceId) || !TenantIds.Same(await workspace.GetTenantId() ?? "\0", caller.TenantId))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (http.Request.ContentLength > MaxBodyBytes)
        {
            http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

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

        if (request?["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method))
        {
            await WriteAsync(http, Error(null, -32600, "Invalid request"));
            return;
        }

        var id = request["id"]?.DeepClone();
        // A notification (no id), e.g. notifications/initialized: accepted, nothing to answer.
        if (id is null)
        {
            http.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        var p = request["params"] as JsonObject ?? [];
        try
        {
            var connection = await workspace.GetGatewayConnection(connectionId)
                             ?? throw new RpcError(-32001, "This connection isn't served as an MCP gateway (or its workspace isn't active).");
            var served = Served(connection);
            JsonNode result = method switch
            {
                "initialize" => Initialize(p, connection),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = new JsonArray(served.Select(ToolJson).ToArray()) },
                "tools/call" => await CallAsync(p, workspaceId, connection, served, caller, integrations, grains, audit, limiter,
                    loggers.CreateLogger("AgentRuntime.Api.Interop.ConnectionGateway"), ct),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                _ => throw new RpcError(-32601, $"Method not found: {method}")
            };
            await WriteAsync(http, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
        }
        catch (RpcError ex)
        {
            await WriteAsync(http, Error(id, ex.Code, ex.Message));
        }
    }

    private static List<CachedTool> Served(ConnectionDefinition c) =>
        c.Tools.Where(t => c.Gateway.Tools.Contains(t.LocalName, StringComparer.Ordinal)).ToList();

    private static JsonObject Initialize(JsonObject p, ConnectionDefinition c)
    {
        var asked = p["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var description = c.Settings.TryGetValue("description", out var about) && !string.IsNullOrWhiteSpace(about) ? about : c.Name;
        return new JsonObject
        {
            ["protocolVersion"] = asked is not null && ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = $"aktor-{c.Name}", ["title"] = description, ["version"] = "1.0.0" },
            ["instructions"] = $"Tools of the '{c.Name}' connection ({description}), served by Aktor with a stored credential. " +
                               "Calls follow the workspace's safety policy and are audited; writes may be refused when they need a person's approval."
        };
    }

    private static JsonObject ToolJson(CachedTool t)
    {
        JsonNode schema;
        try
        {
            schema = JsonNode.Parse(t.JsonSchema) ?? new JsonObject { ["type"] = "object" };
        }
        catch (JsonException)
        {
            schema = new JsonObject { ["type"] = "object" };
        }

        return new JsonObject
        {
            ["name"] = t.LocalName,
            ["description"] = t.Description,
            ["inputSchema"] = schema,
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = t.SideEffects == ToolSideEffects.ReadOnly,
                ["idempotentHint"] = t.SideEffects != ToolSideEffects.NonIdempotent,
                ["destructiveHint"] = t.SideEffects == ToolSideEffects.NonIdempotent,
                ["openWorldHint"] = true
            }
        };
    }

    private static async Task<JsonNode> CallAsync(JsonObject p, string workspaceId, ConnectionDefinition connection, List<CachedTool> served,
        Infrastructure.Identity.Caller caller, IntegrationService integrations, IGrainFactory grains, IAuditLog audit,
        GatewayRateLimiter limiter, ILogger logger, CancellationToken ct)
    {
        if (caller.Role < TenantRole.Member) throw new RpcError(-32003, "This key may list the tools but not call them (it needs the Member role).");
        var name = p["name"] is JsonValue n && n.TryGetValue<string>(out var s) ? s : throw new RpcError(-32602, "params.name is required");
        var tool = served.FirstOrDefault(t => t.LocalName == name) ?? throw new RpcError(-32602, $"Unknown tool: {name}");
        var arguments = p["arguments"] as JsonObject ?? [];
        var exposed = ConnectionNames.Expose(connection.Name, tool.LocalName);
        var callId = "gw-" + Guid.NewGuid().ToString("N")[..16];
        var actorType = caller.ApiKeyId is null ? "user" : "api_key";
        var actorId = caller.ApiKeyId ?? caller.UserId ?? "unknown";
        var actorName = caller.Email ?? (caller.ApiKeyId is null ? "Someone" : $"API key {caller.ApiKeyId}");

        async Task<JsonNode> Refuse(string outcome, string message)
        {
            await AuditAsync(audit, logger, workspaceId, callId, actorType, actorId, actorName, exposed, tool.SideEffects, outcome, message, arguments, null);
            return ToolResult(message, isError: true);
        }

        if (!limiter.TryAcquire($"{workspaceId}/{connection.ConnectionId}"))
        {
            return await Refuse("denied", "Rate limit reached for this connection's gateway; try again in a minute.");
        }

        // The same policies agents are held to: the stricter of the organization's and the workspace's.
        var orgPolicy = await grains.GetGrain<ITenantGrain>(TenantIds.Normalize(caller.TenantId)).GetSafetyPolicy();
        var wsPolicy = await grains.GetGrain<IWorkspaceGrain>(workspaceId).GetSafetyPolicy();
        var verdict = PolicyEngine.Evaluate(orgPolicy, wsPolicy, exposed, tool.SideEffects);
        if (verdict.Decision == PolicyDecisionKind.Deny) return await Refuse("denied", $"Blocked by {verdict.Reason}.");
        if (verdict.Decision == PolicyDecisionKind.RequireApproval)
        {
            return await Refuse("denied", $"Not run: {verdict.Reason} requires a person's approval, and gateway calls can't wait for one.");
        }

        var result = await integrations.ExecuteToolAsync(workspaceId, new ConnectionToolTarget { Connection = connection, LocalName = tool.LocalName, SideEffects = tool.SideEffects },
            new ToolExecutionRequest
            {
                ToolName = exposed,
                AgentId = $"gateway:{actorId}",
                TaskId = workspaceId,
                TenantId = TenantIds.Normalize(caller.TenantId),
                WorkspaceId = workspaceId,
                ArgumentsJson = arguments.ToJsonString(),
                IdempotencyKey = callId,
                GrantedPermissions = ToolPermission.Integrations,
                CancellationToken = ct
            });

        await AuditAsync(audit, logger, workspaceId, callId, actorType, actorId, actorName, exposed, tool.SideEffects,
            result.Success ? "ok" : "failed", result.Success ? "ok" : result.ErrorMessage ?? "failed", arguments, result.Success ? result.ResultJson : null);
        return result.Success ? ToolResult(result.ResultJson, isError: false) : ToolResult(result.ErrorMessage ?? "The call failed.", isError: true);
    }

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError
    };

    private static async Task AuditAsync(IAuditLog audit, ILogger logger, string workspaceId, string callId, string actorType, string actorId,
        string actorName, string tool, ToolSideEffects sideEffects, string outcome, string message, JsonObject arguments, string? result)
    {
        try
        {
            await audit.AppendAsync(new AuditEntry
            {
                Scope = workspaceId,
                Key = $"{callId}:gateway",
                ActorType = actorType,
                ActorId = actorId,
                ActorName = actorName,
                Action = "gateway.call",
                Target = tool,
                SideEffects = sideEffects.ToString(),
                Outcome = outcome,
                Summary = $"{actorName} called {tool} through the MCP gateway: {Clip(message, 160)}",
                DetailJson = JsonSerializer.Serialize(new
                {
                    arguments = Clip(arguments.ToJsonString(), 2000),
                    result = result is null ? null : Clip(result, 500),
                    error = outcome == "ok" ? null : Clip(message, 500)
                })
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit write failed for gateway call {Tool} in {WorkspaceId}", tool, workspaceId);
        }
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static Task WriteAsync(HttpContext http, JsonObject body)
    {
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(body.ToJsonString());
    }
}
