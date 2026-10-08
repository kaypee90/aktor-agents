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

    /// <summary>
    /// Deletes shared knowledge of one organization in exactly <paramref name="scope"/> (its own
    /// workspace's entries, or organization-wide ones): the entries with <paramref name="memoryIds"/>,
    /// or every passage of the file <paramref name="fileName"/> (see <see cref="KnowledgeFiles"/>).
    /// Returns what was deleted; nothing outside the tenant and scope is ever touched.
    /// </summary>
    Task<IReadOnlyList<MemoryRecord>> DeleteSharedAsync(
        string tenantId, MemoryScope scope, IReadOnlyCollection<string>? memoryIds = null, string? fileName = null,
        CancellationToken cancellationToken = default);

    /// <summary>The keys and authors of every shared entry in exactly <paramref name="scope"/>, newest
    /// first, for counting what a workspace or the organization knows (search is capped).</summary>
    Task<IReadOnlyList<(string Key, string AgentId)>> ListSharedKeysAsync(
        string tenantId, MemoryScope scope, CancellationToken cancellationToken = default);
}

/// <summary>What a scope's shared knowledge holds: files people added, facts people typed, and
/// entries agents saved.</summary>
public sealed record KnowledgeSummary(int Entries, int Files, int Facts, int FromAgents, IReadOnlyList<string> FileNames)
{
    public static KnowledgeSummary Of(IReadOnlyList<(string Key, string AgentId)> entries)
    {
        var byPeople = entries.Where(e => e.AgentId == "user").ToList();
        var files = byPeople.Select(e => KnowledgeFiles.FileOf(e.Key) ?? (KnowledgeFiles.LooksLikeFile(e.Key) ? e.Key : null))
            .Where(f => f is not null).Select(f => f!).Distinct().ToList();
        var facts = byPeople.Count(e => KnowledgeFiles.FileOf(e.Key) is null && !KnowledgeFiles.LooksLikeFile(e.Key));
        return new KnowledgeSummary(entries.Count, files.Count, facts, entries.Count - byPeople.Count, files);
    }
}

/// <summary>
/// Files added as knowledge are split into passages keyed by the file name: "notes.md" for a file
/// that fits in one, else "report.pdf (part 2 of 5)". These tell a file's passages apart from
/// other entries, so a file can be deleted (or replaced) as a whole.
/// </summary>
public static partial class KnowledgeFiles
{
    /// <summary>The key of one passage of a file.</summary>
    public static string KeyOf(string fileName, int part, int parts) => parts == 1 ? fileName : $"{fileName} (part {part} of {parts})";

    /// <summary>The file a "(part N of M)" passage came from; null for any other key.</summary>
    public static string? FileOf(string key) => PartSuffix().Match(key) is { Success: true } m ? key[..m.Index] : null;

    /// <summary>Whether the entry with this key is (a passage of) the file.</summary>
    public static bool IsPassageOf(string key, string fileName) => key == fileName || FileOf(key) == fileName;

    /// <summary>A key that reads as a file name ("notes.md"): a file that fit in one passage.</summary>
    public static bool LooksLikeFile(string key) => FileExtension().IsMatch(key);

    [System.Text.RegularExpressions.GeneratedRegex(@"\.[A-Za-z0-9]{1,5}$")]
    private static partial System.Text.RegularExpressions.Regex FileExtension();

    [System.Text.RegularExpressions.GeneratedRegex(@" \(part \d+ of \d+\)$")]
    private static partial System.Text.RegularExpressions.Regex PartSuffix();
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
