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
}

public sealed record ToolExecutionRequest
{
    public required string ToolName { get; init; }
    public required string AgentId { get; init; }
    public required string TaskId { get; init; }
    public required string ArgumentsJson { get; init; }

    /// <summary>The calling agent's runtime-granted permissions, stamped by <see cref="ToolRegistry"/>
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
