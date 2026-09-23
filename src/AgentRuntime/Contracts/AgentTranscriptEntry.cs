namespace AgentRuntime.Contracts;

/// <summary>
/// A persisted, serializable projection of one LLM conversation turn. Deliberately structured
/// (role/content/tool-calls) rather than free text so it can be replayed into any provider and
/// safely displayed in the UI without leaking hidden chain-of-thought (CLAUDE.md section 30).
/// </summary>
[GenerateSerializer]
public sealed record AgentTranscriptEntry
{
    [Id(0)] public required string Role { get; init; }
    [Id(1)] public string? Content { get; init; }
    [Id(2)] public List<TranscriptToolCall>? ToolCalls { get; init; }
    [Id(3)] public string? ToolCallId { get; init; }
    [Id(4)] public string? ToolName { get; init; }
    [Id(5)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

[GenerateSerializer]
public sealed record TranscriptToolCall
{
    [Id(0)] public required string Id { get; init; }
    [Id(1)] public required string Name { get; init; }
    [Id(2)] public required string ArgumentsJson { get; init; }
}
