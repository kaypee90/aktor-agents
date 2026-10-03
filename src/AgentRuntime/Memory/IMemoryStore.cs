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

    /// <summary>The workspace whose agents alone can find this entry; null for the whole organization.</summary>
    public string? WorkspaceId { get; init; }

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
    /// <summary>Saves the record, replacing one with the same tenant, author, key and workspace.</summary>
    Task WriteAsync(MemoryRecord record, CancellationToken cancellationToken = default);

    /// <summary>The agent's own record for the key, or a shared one it can see (see <see cref="MemoryScope"/>).</summary>
    Task<MemoryRecord?> ReadAsync(string tenantId, string agentId, string key, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>Searches one organization's memory; nothing from another tenant is ever returned, and
    /// nothing from a workspace outside <paramref name="scope"/> (organization-wide only by default).
    /// Results are ranked best first (by meaning, words and recency where the store supports it).</summary>
    Task<IReadOnlyList<MemoryRecord>> SearchAsync(
        string tenantId, string query, MemoryKind? kind = null, string? agentId = null, MemoryScope? scope = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Which knowledge a reader sees. Organization-wide entries (no workspace) are visible everywhere in
/// the organization; a workspace's entries only to that workspace's agents (and to people managing
/// that workspace's knowledge).
/// </summary>
public sealed record MemoryScope(string? WorkspaceId, bool WorkspaceOnly = false)
{
    /// <summary>Organization-wide entries only: task agents and the organization's knowledge page.</summary>
    public static readonly MemoryScope Organization = new((string?)null);

    /// <summary>An agent in a workspace: organization-wide entries plus its workspace's.</summary>
    public static MemoryScope ForAgentIn(string? workspaceId) => new(string.IsNullOrEmpty(workspaceId) ? null : workspaceId);

    /// <summary>One workspace's own entries (managing that workspace's knowledge).</summary>
    public static MemoryScope OnlyWorkspace(string workspaceId) => new(workspaceId, WorkspaceOnly: true);

    /// <summary>Whether an entry with this workspace is visible in the scope.</summary>
    public bool Includes(string? entryWorkspaceId) =>
        WorkspaceId is null ? entryWorkspaceId is null
        : WorkspaceOnly ? entryWorkspaceId == WorkspaceId
        : entryWorkspaceId is null || entryWorkspaceId == WorkspaceId;
}
