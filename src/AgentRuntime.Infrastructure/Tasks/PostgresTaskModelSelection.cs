using System.Collections.Concurrent;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.LLM;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Infrastructure.Tasks;

/// <summary>
/// A task's model profile, kept on its row (docs/llm-settings.md). Every agent reads it at the start
/// of each step, so lookups are cached for a few seconds; a switch made through this service
/// applies at once on this server, and within the cache time on others.
/// </summary>
public sealed class PostgresTaskModelSelection(IDbContextFactory<AgentDbContext> dbFactory) : ITaskModelSelection
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string? ProfileId)> _cache = new();

    public async Task<string?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(taskId)) return null;
        if (_cache.TryGetValue(taskId, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheFor) return hit.ProfileId;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Workspaces and worlds have no task row: they use the organization's default.
        var profileId = await db.Tasks.AsNoTracking().Where(t => t.TaskId == taskId).Select(t => t.ModelProfileId).FirstOrDefaultAsync(cancellationToken);
        _cache[taskId] = (DateTimeOffset.UtcNow, profileId);
        return profileId;
    }

    public async Task SetAsync(string taskId, string? profileId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Tasks.Where(t => t.TaskId == taskId).ExecuteUpdateAsync(u => u.SetProperty(t => t.ModelProfileId, profileId), cancellationToken);
        _cache[taskId] = (DateTimeOffset.UtcNow, profileId);
    }
}
