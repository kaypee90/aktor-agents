using System.Text.Json;
using AgentRuntime.Contracts;
using AgentRuntime.Memory;

namespace AgentRuntime.Tools;

public sealed class ReadMemoryTool(IMemoryStore memory) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "read_memory",
        Description = "Read a value you (or, for shared keys, another agent) previously wrote to memory.",
        JsonSchema = """{ "type": "object", "properties": { "key": { "type": "string" } }, "required": ["key"] }"""
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<KeyArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? throw new ArgumentException("Invalid read_memory arguments.");

        var record = await memory.ReadAsync(request.AgentId, args.Key, request.CancellationToken);
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
        Description = "Persist a working-memory or episodic-memory value. Set shared=true to make it " +
                      "visible to other agents as shared knowledge.",
        JsonSchema = """
        {
          "type": "object",
          "properties": {
            "key": { "type": "string" },
            "value": { "type": "string" },
            "shared": { "type": "boolean" }
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
            Kind = args.Shared ? MemoryKind.Shared : MemoryKind.Working,
            Key = args.Key,
            Value = args.Value
        }, request.CancellationToken);

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { written = true }, ToolJson.Options));
    }

    private sealed record WriteArgs(string Key, string Value, bool Shared = false);
}

public sealed class SearchKnowledgeTool(IMemoryStore memory) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "search_knowledge",
        Description = "Search shared knowledge written by any agent for relevant prior findings.",
        JsonSchema = """{ "type": "object", "properties": { "query": { "type": "string" } }, "required": ["query"] }"""
    };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
    {
        var args = JsonSerializer.Deserialize<QueryArgs>(request.ArgumentsJson, ToolJson.Options)
                   ?? throw new ArgumentException("Invalid search_knowledge arguments.");

        var results = await memory.SearchAsync(args.Query, MemoryKind.Shared, cancellationToken: request.CancellationToken);
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
        {
            results = results.Select(r => new { r.AgentId, r.Key, r.Value })
        }, ToolJson.Options));
    }

    private sealed record QueryArgs(string Query);
}
