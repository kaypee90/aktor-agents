using System.Text.Json;
using AgentRuntime.Contracts;
using AgentRuntime.Memory;
using AgentRuntime.Workspaces;

namespace AgentRuntime.Tools;

/// <summary>Workspace agents run with their workspace's id as their task id: that's the knowledge they may see.</summary>
internal static class MemoryScopes
{
    /// <summary>The calling agent's workspace (a pipeline run's agents run under the run's id, so
    /// it comes from the runtime's stamp, never the task id).</summary>
    public static string? WorkspaceOf(ToolExecutionRequest request) => WorkspaceIds.IsWorkspace(request.WorkspaceId) ? request.WorkspaceId : null;
}

public sealed class ReadMemoryTool(IMemoryStore memory) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "read_memory",
        SideEffects = ToolSideEffects.ReadOnly,
        Description = "Read a value you (or, for shared keys, another agent) previously wrote to memory.",
        JsonSchema = """{ "type": "object", "properties": { "key": { "type": "string" } }, "required": ["key"] }"""
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<KeyArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? throw new ArgumentException("Invalid read_memory arguments.");

        var record = await memory.ReadAsync(request.TenantId, request.AgentId, args.Key,
            MemoryScope.ForAgentIn(MemoryScopes.WorkspaceOf(request)), request.CancellationToken);
        return record is null
            ? ToolExecutionResult.Ok(JsonSerializer.Serialize(new { found = false }, ToolJson.Options))
            : ToolExecutionResult.Ok(JsonSerializer.Serialize(new { found = true, value = record.Value }, ToolJson.Options));
    }

    private sealed record KeyArgs(string Key);
}

public sealed class WriteMemoryTool(IMemoryStore memory) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "write_memory",
        SideEffects = ToolSideEffects.Idempotent,
        Description = "Persist a working-memory or episodic-memory value. Set shared=true to make it " +
                      "visible to other agents as shared knowledge. In a workspace, shared knowledge stays within the " +
                      "workspace unless organization_wide=true.",
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "key": { "type": "string" },
            "value": { "type": "string" },
            "shared": { "type": "boolean" },
            "organization_wide": { "type": "boolean", "description": "Workspace agents only: share with every agent of the organization, not just this workspace." }
          },
          "required": ["key", "value"]
        }
        """
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<WriteArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? throw new ArgumentException("Invalid write_memory arguments.");

        await memory.WriteAsync(new MemoryRecord
        {
            AgentId = request.AgentId,
            TenantId = request.TenantId,
            Kind = args.Shared ? MemoryKind.Shared : MemoryKind.Working,
            Key = args.Key,
            Value = args.Value,
            // A workspace's findings stay in it unless the agent shares them with everyone.
            WorkspaceId = args.OrganizationWide ? null : MemoryScopes.WorkspaceOf(request)
        }, request.CancellationToken);

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { written = true }, ToolJson.Options));
    }

    private sealed record WriteArgs(string Key, string Value, bool Shared = false, bool OrganizationWide = false);
}

public sealed class SearchKnowledgeTool(IMemoryStore memory) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "search_knowledge",
        SideEffects = ToolSideEffects.ReadOnly,
        Description = "Search shared knowledge for relevant prior findings: your organization's, and your workspace's own if " +
                      "you're in one. Matches by meaning as well as by words, best and most recent first.",
        JsonSchema = """{ "type": "object", "properties": { "query": { "type": "string" } }, "required": ["query"] }"""
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<QueryArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? throw new ArgumentException("Invalid search_knowledge arguments.");

        var results = await memory.SearchAsync(request.TenantId, args.Query, MemoryKind.Shared,
            scope: MemoryScope.ForAgentIn(MemoryScopes.WorkspaceOf(request)), cancellationToken: request.CancellationToken);
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
        {
            results = results.Take(MaxResults).Select(r => new { r.AgentId, r.Key, r.Value, r.Score })
        }, ToolJson.Options));
    }

    private const int MaxResults = 10;

    private sealed record QueryArgs(string Query);
}
