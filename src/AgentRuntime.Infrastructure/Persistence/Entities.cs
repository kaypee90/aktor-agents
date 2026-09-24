namespace AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Durable history tables (CLAUDE.md section 39). Orleans grain state stays optimized for active
/// execution; these rows are the permanent record, written by <see cref="PersistenceEventSubscriber"/>.
/// </summary>
public sealed class TaskRecord
{
    public required string TaskId { get; set; }
    public required string Goal { get; set; }
    public string Status { get; set; } = "Running";
    public string? RootAgentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ResultSummary { get; set; }
    public string? ResultJson { get; set; }
}

public sealed class AgentRecord
{
    public required string AgentId { get; set; }
    public string? ParentAgentId { get; set; }
    public required string RootAgentId { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public required string Name { get; set; }
    public required string Role { get; set; }
    public required string Goal { get; set; }
    public string Status { get; set; } = "Created";
    public string CapabilitiesJson { get; set; } = "[]";
    public string AllowedToolsJson { get; set; } = "[]";
    public int Depth { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int TokensUsed { get; set; }
    public int ToolCallsUsed { get; set; }
    public int ChildrenSpawned { get; set; }
    public decimal CostUsd { get; set; }
    public string? FailureReason { get; set; }
}

public sealed class MessageRecord
{
    public required string MessageId { get; set; }
    public required string FromAgentId { get; set; }
    public required string ToAgentId { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public required string MessageType { get; set; }
    public string Priority { get; set; } = "Normal";
    public DateTimeOffset Timestamp { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
}

public sealed class EventRecord
{
    public long Id { get; set; }
    public required string EventId { get; set; }
    public required string Type { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? AgentId { get; set; }
    public string? ParentAgentId { get; set; }
    public string? TargetAgentId { get; set; }
    public string? TaskId { get; set; }
    public string? CorrelationId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string DataJson { get; set; } = "{}";
}

public sealed class ArtifactRecord
{
    public required string ArtifactId { get; set; }
    public required string Type { get; set; }
    public required string Location { get; set; }
    public required string CreatedByAgent { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string MetadataJson { get; set; } = "{}";
}

public sealed class MemoryEntity
{
    public required string MemoryId { get; set; }
    public required string AgentId { get; set; }
    public required string Kind { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ToolCallRecord
{
    public long Id { get; set; }
    public required string AgentId { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public required string ToolName { get; set; }
    public string ArgumentsJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public bool Success { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>One simulated world. <see cref="SnapshotJson"/> is the latest full snapshot, so a world
/// stays inspectable after the in-memory world grain is gone (e.g. after a restart).</summary>
public sealed class WorldRecord
{
    public required string WorldId { get; set; }
    public required string Name { get; set; }
    public string Seed { get; set; } = string.Empty;
    public string Status { get; set; } = "Created";
    public int Tick { get; set; }
    public int MaxTicks { get; set; }
    public int Residents { get; set; }
    public decimal CostUsd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string SnapshotJson { get; set; } = "{}";
}
