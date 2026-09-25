using AgentRuntime.Contracts;

namespace AgentRuntime.Memory;

public sealed record MemoryRecord
{
    public string MemoryId { get; init; } = Guid.NewGuid().ToString("n");
    public required string AgentId { get; init; }
    /// <summary>Shared knowledge is shared within one organization only.</summary>
    public string TenantId { get; init; } = Tenancy.TenantIds.Default;
    public required MemoryKind Kind { get; init; }
    public required string Key { get; init; }
    public required string Value { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Shared-knowledge records are visible to any agent of the same organization; others are owner-scoped.</summary>
    public bool IsShared => Kind == MemoryKind.Shared;
}

/// <summary>
/// Storage abstraction for working/episodic/shared memory (CLAUDE.md section 20). Starts
/// Postgres-backed; a vector-search implementation can be substituted without touching agents.
/// </summary>
public interface IMemoryStore
{
    Task WriteAsync(MemoryRecord record, CancellationToken cancellationToken = default);

    /// <summary>The agent's own record for the key, or a shared one from its organization.</summary>
    Task<MemoryRecord?> ReadAsync(string tenantId, string agentId, string key, CancellationToken cancellationToken = default);

    /// <summary>Searches one organization's memory; nothing from another tenant is ever returned.</summary>
    Task<IReadOnlyList<MemoryRecord>> SearchAsync(
        string tenantId, string query, MemoryKind? kind = null, string? agentId = null, CancellationToken cancellationToken = default);
}
