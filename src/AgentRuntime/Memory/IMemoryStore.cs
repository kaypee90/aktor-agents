using AgentRuntime.Contracts;

namespace AgentRuntime.Memory;

public sealed record MemoryRecord
{
    public string MemoryId { get; init; } = Guid.NewGuid().ToString("n");
    public required string AgentId { get; init; }
    public required MemoryKind Kind { get; init; }
    public required string Key { get; init; }
    public required string Value { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Shared-knowledge records are visible to any agent; others are owner-scoped.</summary>
    public bool IsShared => Kind == MemoryKind.Shared;
}

/// <summary>
/// Storage abstraction for working/episodic/shared memory (CLAUDE.md section 20). Starts
/// Postgres-backed; a vector-search implementation can be substituted without touching agents.
/// </summary>
public interface IMemoryStore
{
    Task WriteAsync(MemoryRecord record, CancellationToken cancellationToken = default);

    Task<MemoryRecord?> ReadAsync(string agentId, string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryRecord>> SearchAsync(
        string query, MemoryKind? kind = null, string? agentId = null, CancellationToken cancellationToken = default);
}
