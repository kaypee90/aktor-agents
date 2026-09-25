using AgentRuntime.Contracts;

namespace AgentRuntime.Tools;

/// <summary>
/// Describes a tool for LLM structured tool-calling (CLAUDE.md section 16/17). The schema is a
/// JSON Schema fragment describing the tool's parameters object.
/// </summary>
public sealed record ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string JsonSchema { get; init; }
    public ToolPermission RequiredPermissions { get; init; } = ToolPermission.None;

    /// <summary>What happens if the runtime runs this call again after a crash. Defaults to the
    /// safe assumption: an unknown tool is treated as unsafe to repeat.</summary>
    public ToolSideEffects SideEffects { get; init; } = ToolSideEffects.NonIdempotent;
}

/// <summary>
/// How a tool behaves if the same call is executed twice, which decides crash recovery: after a
/// crash mid-call, the runtime re-runs <see cref="ReadOnly"/> and <see cref="Idempotent"/> calls
/// with the same <see cref="ToolExecutionRequest.IdempotencyKey"/>, but never blindly repeats a
/// <see cref="NonIdempotent"/> one. It tells the agent the outcome is unknown instead.
/// </summary>
public enum ToolSideEffects
{
    /// <summary>No external effect (search, read, list). Always safe to re-run.</summary>
    ReadOnly,

    /// <summary>Has an effect, but repeating the same call with the same idempotency key has no
    /// additional effect (upserts, messages deduplicated by id, APIs that accept idempotency keys).</summary>
    Idempotent,

    /// <summary>Repeating it may repeat the effect (shell commands, raw HTTP POSTs, SQL writes).</summary>
    NonIdempotent
}

public sealed record ToolExecutionRequest
{
    public required string ToolName { get; init; }
    public required string AgentId { get; init; }
    public required string TaskId { get; init; }
    public required string ArgumentsJson { get; init; }

    /// <summary>Stable across retries and crash recovery of the same tool call
    /// (<c>{agentId}:{toolCallId}</c>). Tools with side effects pass it on (as a message id, a
    /// spawned agent id, an API idempotency header) so a replayed call can't take effect twice.</summary>
    public string IdempotencyKey { get; init; } = string.Empty;

    /// <summary>The calling agent's runtime-granted permissions, stamped by the runtime's tool registry
    /// so tools with permission-dependent modes (e.g. read vs write) can enforce them.</summary>
    public ToolPermission GrantedPermissions { get; init; } = ToolPermission.None;

    public CancellationToken CancellationToken { get; init; }
}

public sealed record ToolExecutionResult
{
    public required bool Success { get; init; }
    public string ResultJson { get; init; } = "{}";
    public string? ErrorMessage { get; init; }

    public static ToolExecutionResult Ok(string resultJson) => new() { Success = true, ResultJson = resultJson };
    public static ToolExecutionResult Fail(string error) => new() { Success = false, ErrorMessage = error };
}

/// <summary>
/// A tool the runtime can execute on behalf of an agent. The runtime — never the LLM — enforces
/// permissions and isolation around execution (CLAUDE.md sections 17-19, 50).
/// </summary>
public interface ITool
{
    ToolDefinition Definition { get; }

    Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request);
}
