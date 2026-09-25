using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRuntime.Agents;
using AgentRuntime.Contracts;
using AgentRuntime.Messaging;

namespace AgentRuntime.Tools;

/// <summary>
/// Every tool's JSON schema declares snake_case fields (e.g. "to_agent_id", "initial_context",
/// "remaining_work"), matching CLAUDE.md's own examples and the convention real LLM function
/// calling follows for these tools. Options MUST use a matching naming policy — plain
/// JsonSerializerDefaults.Web applies camelCase instead, which silently deserializes every
/// snake_case argument to null/default rather than throwing, so this is easy to get wrong.
/// </summary>
public static class ToolJson
{
    internal static string? NullIfEmptyKey(string? key) => string.IsNullOrEmpty(key) ? null : key;

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}

/// <summary>The most important tool in the system (CLAUDE.md section 9): asks the runtime to
/// create a new specialized agent. The LLM decides; <see cref="IAgentOrchestrator"/> validates.</summary>
public sealed class SpawnAgentTool(IAgentOrchestrator orchestrator) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "spawn_agent",
        SideEffects = ToolSideEffects.Idempotent,
        Description = "Create a new specialized sub-agent to work on part of your goal in parallel or with " +
                      "different expertise. The runtime enforces depth/child/total-agent limits.",
        RequiredPermissions = ToolPermission.SpawnAgents,
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "role": { "type": "string", "description": "Short role name, e.g. 'database specialist'" },
            "goal": { "type": "string", "description": "The specific goal the new agent should pursue" },
            "capabilities": { "type": "array", "items": { "type": "string" } },
            "initial_context": { "type": "string" },
            "standing": { "type": "boolean", "description": "Workspaces only: a long-lived agent (monitor, responder) that waits for events instead of finishing" }
          },
          "required": ["role", "goal"]
        }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<SpawnArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? throw new ArgumentException("Invalid spawn_agent arguments.");

        var result = await orchestrator.SpawnAgentAsync(request.AgentId, new SpawnAgentRequest
        {
            Role = args.Role,
            Goal = args.Goal,
            Capabilities = args.Capabilities ?? [],
            InitialContext = args.InitialContext,
            Standing = args.Standing ?? false
        }, ToolJson.NullIfEmptyKey(request.IdempotencyKey));

        return result.Success
            ? ToolExecutionResult.Ok(JsonSerializer.Serialize(new { agent_id = result.AgentId, status = result.Status, granted_budget = result.GrantedBudget }, ToolJson.Options))
            : ToolExecutionResult.Fail(result.RejectionReason ?? "Spawn rejected.");
    }

    private sealed record SpawnArgs(string Role, string Goal, List<string>? Capabilities, string? InitialContext, bool? Standing);
}

public sealed class FindAgentsTool(IAgentOrchestrator orchestrator) : ITool
{
    // Bounds how much of the agent directory a single find_agents call can inject into the
    // caller's transcript. Every LLM call resends the *entire* conversation, so an unbounded
    // directory dump here is a direct, compounding contributor to token-budget exhaustion on any
    // agent whose tree grows large (CLAUDE.md section 24 — the runtime enforces this, not a prompt).
    private const int MaxResults = 20;
    private const int MaxGoalLength = 150;

    public ToolDefinition Definition { get; } = new()
    {
        Name = "find_agents",
        SideEffects = ToolSideEffects.ReadOnly,
        Description = "Discover agents within your own task by capability and/or status, so you can reuse " +
                      "one instead of spawning a duplicate. Only ever returns agents working on the same " +
                      "goal as you (never other unrelated tasks), capped to the most relevant matches.",
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "capabilities": { "type": "array", "items": { "type": "string" } },
            "status": { "type": "string", "enum": ["Created","Initializing","Idle","Thinking","Executing","Waiting","Spawning","Completed","Failed","Terminated","TimedOut"] }
          }
        }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<FindArgs>(request.ArgumentsJson, ToolJson.Options) ?? new FindArgs(null, null);
        AgentStatus? status = args.Status is null ? null : Enum.Parse<AgentStatus>(args.Status, ignoreCase: true);

        // Scope to the caller's own tree — an agent has no legitimate reason to discover, or spend
        // its budget reading about, a different task's agents.
        var caller = await orchestrator.GetSnapshotAsync(request.AgentId);
        var results = await orchestrator.FindAgentsAsync(new FindAgentsQuery
        {
            Capabilities = args.Capabilities,
            Status = status,
            RootAgentId = caller?.RootAgentId
        });

        var trimmed = results
            .Where(a => a.AgentId != request.AgentId)
            .Take(MaxResults)
            .Select(a => new
            {
                agent_id = a.AgentId,
                role = a.Role,
                goal = a.Goal.Length > MaxGoalLength ? a.Goal[..MaxGoalLength] + "…" : a.Goal,
                status = a.Status.ToString(),
                capabilities = a.Capabilities
            })
            .ToList();

        var payload = results.Count > MaxResults
            ? new { agents = trimmed, truncated = true, total_matches = results.Count }
            : (object)new { agents = trimmed };

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(payload, ToolJson.Options));
    }

    private sealed record FindArgs(List<string>? Capabilities, string? Status);
}

public sealed class SendMessageTool(IAgentOrchestrator orchestrator) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "send_message",
        SideEffects = ToolSideEffects.Idempotent,
        Description = "Send a message directly to another agent (no need to route through the root agent). " +
                      "Use this to delegate, ask a question, or share findings.",
        RequiredPermissions = ToolPermission.SendMessages,
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "to_agent_id": { "type": "string" },
            "message_type": { "type": "string", "enum": ["TaskRequest","TaskResponse","InformationRequest","InformationResponse","StatusUpdate","DelegationRequest","DelegationResponse","CompletionNotification","FailureNotification"] },
            "payload": { "type": "string", "description": "The message content" }
          },
          "required": ["to_agent_id", "message_type", "payload"]
        }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<SendArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? throw new ArgumentException("Invalid send_message arguments.");

        var ack = await orchestrator.SendMessageAsync(new AgentMessage
        {
            // Fixed by the call's idempotency key, so a send replayed after a crash is dropped by
            // the recipient's mailbox instead of arriving twice.
            MessageId = DeterministicId.FromOrNew("msg-", ToolJson.NullIfEmptyKey(request.IdempotencyKey)),
            FromAgentId = request.AgentId,
            ToAgentId = args.ToAgentId,
            MessageType = Enum.Parse<MessageType>(args.MessageType, ignoreCase: true),
            Payload = args.Payload,
            TaskId = request.TaskId
        });

        return ack.Accepted
            ? ToolExecutionResult.Ok(JsonSerializer.Serialize(new { delivered = true, message_id = ack.MessageId }, ToolJson.Options))
            : ToolExecutionResult.Fail(ack.RejectionReason ?? "Message rejected.");
    }

    private sealed record SendArgs(string ToAgentId, string MessageType, string Payload);
}

public sealed class GetAgentStatusTool(IAgentOrchestrator orchestrator) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "get_agent_status",
        SideEffects = ToolSideEffects.ReadOnly,
        Description = "Check the current status, goal, and resource usage of a specific agent by id.",
        JsonSchema = """
        { "type": "object", "properties": { "agent_id": { "type": "string" } }, "required": ["agent_id"] }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<StatusArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? throw new ArgumentException("Invalid get_agent_status arguments.");

        var snapshot = await orchestrator.GetSnapshotAsync(args.AgentId);
        if (snapshot is null)
        {
            return ToolExecutionResult.Fail($"No such agent '{args.AgentId}'.");
        }

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
        {
            agent_id = snapshot.AgentId,
            status = snapshot.Status.ToString(),
            goal = snapshot.Goal,
            current_task = snapshot.CurrentTask,
            children = snapshot.Children
        }, ToolJson.Options));
    }

    private sealed record StatusArgs(string AgentId);
}

public sealed class ListChildrenTool(IAgentOrchestrator orchestrator) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "list_children",
        SideEffects = ToolSideEffects.ReadOnly,
        Description = "List the ids of agents you have spawned.",
        JsonSchema = """{ "type": "object", "properties": { "agent_id": { "type": "string" } }, "required": ["agent_id"] }"""
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<ChildrenArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? new ChildrenArgs(request.AgentId);
        var children = await orchestrator.ListChildrenAsync(args.AgentId);
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { children }, ToolJson.Options));
    }

    private sealed record ChildrenArgs(string AgentId);
}

public sealed class CompleteTaskTool : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "complete_task",
        SideEffects = ToolSideEffects.ReadOnly,
        Description = "Declare that your assigned goal is complete (or has failed beyond recovery). " +
                      "The runtime records this as your final result.",
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "status": { "type": "string", "enum": ["completed", "failed", "partial"] },
            "summary": { "type": "string" },
            "artifacts": { "type": "array", "items": { "type": "string" } },
            "evidence": { "type": "array", "items": { "type": "string" } },
            "remaining_work": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["status", "summary"]
        }
        """
    };

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        // Validation only; AgentGrain applies the actual state transition after this returns
        // successfully (CLAUDE.md section 25 — the runtime validates completion).
        var parsed = JsonSerializer.Deserialize<CompleteTaskRequest>(request.ArgumentsJson, ToolJson.Options);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Summary))
        {
            return Task.FromResult(ToolExecutionResult.Fail("complete_task requires at least a summary."));
        }

        return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new { acknowledged = true }, ToolJson.Options)));
    }
}
