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

    /// <summary>Search results only: how well the entry matched (higher is better).</summary>
    public double? Score { get; init; }

    /// <summary>Shared-knowledge records are visible to any agent of the same organization; others are owner-scoped.</summary>
    public bool IsShared => Kind == MemoryKind.Shared;
}

/// <summary>
/// Storage abstraction for working/episodic/shared memory (CLAUDE.md section 20). The Postgres
/// store adds semantic recall (pgvector) when an <see cref="IEmbeddingProvider"/> is configured,
/// and searches by keyword when not; agents use the same tools either way.
/// </summary>
public interface IMemoryStore
{
    Task WriteAsync(MemoryRecord record, CancellationToken cancellationToken = default);

    /// <summary>The agent's own record for the key, or a shared one from its organization.</summary>
    Task<MemoryRecord?> ReadAsync(string tenantId, string agentId, string key, CancellationToken cancellationToken = default);

    /// <summary>Searches one organization's memory; nothing from another tenant is ever returned.
    /// Results are ranked best first (by meaning, words and recency where the store supports it).</summary>
    Task<IReadOnlyList<MemoryRecord>> SearchAsync(
        string tenantId, string query, MemoryKind? kind = null, string? agentId = null, CancellationToken cancellationToken = default);
}
