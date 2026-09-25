using AgentRuntime.Contracts;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Memory;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Infrastructure.Memory;

/// <summary>
/// Postgres-backed implementation of <see cref="IMemoryStore"/> (CLAUDE.md section 20). Search is
/// a simple substring match for the prototype; swapping in a vector index later only requires a
/// new implementation of this interface, not agent-runtime changes. Registered as a singleton (so
/// it can be depended on by singleton tools), it creates a short-lived DbContext per call via the
/// factory rather than holding a scoped one.
/// </summary>
public sealed class PostgresMemoryStore(IDbContextFactory<AgentDbContext> dbContextFactory) : IMemoryStore
{
    public async Task WriteAsync(MemoryRecord record, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.MemoryEntries
            .FirstOrDefaultAsync(m => m.TenantId == record.TenantId && m.AgentId == record.AgentId && m.Key == record.Key, cancellationToken);

        if (existing is not null)
        {
            existing.Value = record.Value;
            existing.Kind = record.Kind.ToString();
        }
        else
        {
            db.MemoryEntries.Add(new MemoryEntity
            {
                MemoryId = record.MemoryId,
                TenantId = record.TenantId,
                AgentId = record.AgentId,
                Kind = record.Kind.ToString(),
                Key = record.Key,
                Value = record.Value,
                CreatedAt = record.CreatedAt
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<MemoryRecord?> ReadAsync(string tenantId, string agentId, string key, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.MemoryEntries
            .Where(m => m.TenantId == tenantId && m.Key == key && (m.AgentId == agentId || m.Kind == nameof(MemoryKind.Shared)))
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<MemoryRecord>> SearchAsync(
        string tenantId, string query, MemoryKind? kind = null, string? agentId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var q = db.MemoryEntries.Where(m => m.TenantId == tenantId);

        if (kind is { } k) q = q.Where(m => m.Kind == k.ToString());
        if (agentId is not null) q = q.Where(m => m.AgentId == agentId);
        if (!string.IsNullOrWhiteSpace(query))
        {
            q = q.Where(m => EF.Functions.ILike(m.Key, $"%{query}%") || EF.Functions.ILike(m.Value, $"%{query}%"));
        }

        var entities = await q.OrderByDescending(m => m.CreatedAt).Take(50).ToListAsync(cancellationToken);
        return entities.Select(ToRecord).ToList();
    }

    private static MemoryRecord ToRecord(MemoryEntity e) => new()
    {
        MemoryId = e.MemoryId,
        TenantId = e.TenantId,
        AgentId = e.AgentId,
        Kind = Enum.Parse<MemoryKind>(e.Kind),
        Key = e.Key,
        Value = e.Value,
        CreatedAt = e.CreatedAt
    };
}
