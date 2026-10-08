using System.Collections.Concurrent;
using AgentRuntime.Contracts;
using AgentRuntime.Memory;

namespace AgentRuntime.IntegrationTests.TestSupport;

/// <summary>Hermetic stand-in for <see cref="IMemoryStore"/> so integration tests don't need Postgres.
/// Scoped like the Postgres store: shared records are visible within their tenant only.</summary>
public sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly ConcurrentDictionary<string, MemoryRecord> _records = new();

    public Task WriteAsync(MemoryRecord record, CancellationToken cancellationToken = default)
    {
        _records[record.TenantId + "|" + record.AgentId + "|" + record.Key + "|" + record.WorkspaceId] = record;
        return Task.CompletedTask;
    }

    public Task<MemoryRecord?> ReadAsync(string tenantId, string agentId, string key, MemoryScope? scope = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(_records.Values
            .Where(r => r.TenantId == tenantId && r.Key == key &&
                        (r.AgentId == agentId || (r.IsShared && (scope ?? MemoryScope.Organization).Includes(r.WorkspaceId))))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefault());

    public Task<IReadOnlyList<MemoryRecord>> SearchAsync(
        string tenantId, string query, MemoryKind? kind = null, string? agentId = null, MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        var visible = scope ?? MemoryScope.Organization;
        IEnumerable<MemoryRecord> results = _records.Values.Where(r => r.TenantId == tenantId && visible.Includes(r.WorkspaceId));
        if (kind is { } k) results = results.Where(r => r.Kind == k);
        if (agentId is not null) results = results.Where(r => r.AgentId == agentId);
        if (!string.IsNullOrWhiteSpace(query))
        {
            results = results.Where(r => r.Key.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Value.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return Task.FromResult<IReadOnlyList<MemoryRecord>>(results.ToList());
    }

    public Task<IReadOnlyList<MemoryRecord>> DeleteSharedAsync(
        string tenantId, MemoryScope scope, IReadOnlyCollection<string>? memoryIds = null, string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        if ((memoryIds is null || memoryIds.Count == 0) && string.IsNullOrEmpty(fileName)) return Task.FromResult<IReadOnlyList<MemoryRecord>>([]);
        var deleted = new List<MemoryRecord>();
        foreach (var (key, r) in _records)
        {
            if (r.TenantId != tenantId || !r.IsShared || r.WorkspaceId != scope.WorkspaceId) continue;
            var match = memoryIds is { Count: > 0 } ? memoryIds.Contains(r.MemoryId) : KnowledgeFiles.IsPassageOf(r.Key, fileName!);
            if (match && _records.TryRemove(key, out var removed)) deleted.Add(removed);
        }

        return Task.FromResult<IReadOnlyList<MemoryRecord>>(deleted);
    }
}
