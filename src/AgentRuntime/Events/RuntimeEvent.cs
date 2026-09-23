using AgentRuntime.Contracts;

namespace AgentRuntime.Events;

/// <summary>
/// A structured, UI-safe telemetry event. Never carries private chain-of-thought — only the
/// structured decision/action/reason summary an agent chose to expose (CLAUDE.md section 30).
/// </summary>
public sealed record RuntimeEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("n");
    public required RuntimeEventType Type { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public string? AgentId { get; init; }
    public string? ParentAgentId { get; init; }
    public string? TargetAgentId { get; init; }
    public string? TaskId { get; init; }
    public string? CorrelationId { get; init; }

    /// <summary>Short human-readable summary, e.g. "Spawning database specialist".</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Optional structured decision trace: {"decision": "...", "action": "...", "reason": "..."}.</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();
}

public interface IEventPublisher
{
    ValueTask PublishAsync(RuntimeEvent evt, CancellationToken cancellationToken = default);
}

public interface IEventStream
{
    /// <summary>Subscribes to the live event feed. The channel is torn down when enumeration stops.</summary>
    IAsyncEnumerable<RuntimeEvent> Subscribe(CancellationToken cancellationToken);
}
