using AgentRuntime.Contracts;

namespace AgentRuntime.Messaging;

/// <summary>
/// Immutable message contract exchanged between agents. Messages are always treated as
/// untrusted input by the receiver (CLAUDE.md section 51) and are persisted (section 12).
/// </summary>
[GenerateSerializer]
public sealed record AgentMessage
{
    [Id(0)] public string MessageId { get; init; } = Guid.NewGuid().ToString("n");
    [Id(1)] public required string FromAgentId { get; init; }
    [Id(2)] public required string ToAgentId { get; init; }
    [Id(3)] public string ConversationId { get; init; } = Guid.NewGuid().ToString("n");
    [Id(4)] public string? CorrelationId { get; init; }
    [Id(5)] public required MessageType MessageType { get; init; }
    [Id(6)] public MessagePriority Priority { get; init; } = MessagePriority.Normal;
    [Id(7)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    [Id(8)] public required string Payload { get; init; }
    [Id(9)] public string? ReplyTo { get; init; }
    [Id(10)] public string TaskId { get; init; } = string.Empty;
}

[GenerateSerializer]
public sealed record AgentMessageAck
{
    [Id(0)] public required string MessageId { get; init; }
    [Id(1)] public bool Accepted { get; init; }
    [Id(2)] public string? RejectionReason { get; init; }
}

[GenerateSerializer]
public sealed record EnvironmentEvent
{
    [Id(0)] public required string EventName { get; init; }
    [Id(1)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    [Id(2)] public string Payload { get; init; } = "{}";
}
