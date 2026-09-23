using System.Collections.Concurrent;
using AgentRuntime.Contracts;
using AgentRuntime.Memory;

namespace AgentRuntime.IntegrationTests.TestSupport;

/// <summary>Hermetic stand-in for <see cref="IMemoryStore"/> so integration tests don't need Postgres.</summary>
public sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly ConcurrentDictionary<string, MemoryRecord> _records = new();

    public Task WriteAsync(MemoryRecord record, CancellationToken cancellationToken = default)
    {
        _records[record.AgentId + "|" + record.Key] = record;
        return Task.CompletedTask;
    }

    public Task<MemoryRecord?> ReadAsync(string agentId, string key, CancellationToken cancellationToken = default)
    {
        _records.TryGetValue(agentId + "|" + key, out var record);
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<MemoryRecord>> SearchAsync(
        string query, MemoryKind? kind = null, string? agentId = null, CancellationToken cancellationToken = default)
    {
        IEnumerable<MemoryRecord> results = _records.Values;
        if (kind is { } k) results = results.Where(r => r.Kind == k);
        if (agentId is not null) results = results.Where(r => r.AgentId == agentId);
        return Task.FromResult<IReadOnlyList<MemoryRecord>>(results.ToList());
    }
}
