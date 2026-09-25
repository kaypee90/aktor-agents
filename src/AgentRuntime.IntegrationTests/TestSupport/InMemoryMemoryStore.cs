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
        _records[record.TenantId + "|" + record.AgentId + "|" + record.Key] = record;
        return Task.CompletedTask;
    }

    public Task<MemoryRecord?> ReadAsync(string tenantId, string agentId, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_records.Values
            .Where(r => r.TenantId == tenantId && r.Key == key && (r.AgentId == agentId || r.IsShared))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefault());

    public Task<IReadOnlyList<MemoryRecord>> SearchAsync(
        string tenantId, string query, MemoryKind? kind = null, string? agentId = null, CancellationToken cancellationToken = default)
    {
        IEnumerable<MemoryRecord> results = _records.Values.Where(r => r.TenantId == tenantId);
        if (kind is { } k) results = results.Where(r => r.Kind == k);
        if (agentId is not null) results = results.Where(r => r.AgentId == agentId);
        if (!string.IsNullOrWhiteSpace(query))
        {
            results = results.Where(r => r.Key.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Value.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return Task.FromResult<IReadOnlyList<MemoryRecord>>(results.ToList());
    }
}
