using System.Collections.Concurrent;
using System.Text.Json;
using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Integrations;
using AgentRuntime.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Infrastructure.Integrations;

/// <summary>Global connectors in Postgres. Every tool call of a linked connection reads its
/// connector, so lookups by id are cached briefly: a change on this server is seen at once, on
/// other servers within <see cref="Ttl"/>.</summary>
public sealed class EfGlobalConnectorStore(IDbContextFactory<AgentDbContext> dbFactory, TimeProvider clock) : IGlobalConnectorStore
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (GlobalConnector? Connector, DateTimeOffset At)> _cache = new();

    public async Task<GlobalConnector?> GetAsync(string tenantId, string idOrName, CancellationToken ct = default)
    {
        var tenant = TenantIds.Normalize(tenantId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lowered = idOrName.ToLowerInvariant();
        var row = await db.GlobalConnectors.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenant && (c.ConnectorId == idOrName || c.Name == lowered), ct);
        return row is null ? null : ToConnector(row);
    }

    public async Task<GlobalConnector?> GetByIdAsync(string connectorId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (!_cache.TryGetValue(connectorId, out var cached) || now - cached.At > Ttl)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.GlobalConnectors.AsNoTracking().FirstOrDefaultAsync(c => c.ConnectorId == connectorId, ct);
            cached = (row is null ? null : ToConnector(row), now);
            _cache[connectorId] = cached;
        }

        return cached.Connector;
    }

    public async Task<IReadOnlyList<GlobalConnector>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        var tenant = TenantIds.Normalize(tenantId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.GlobalConnectors.AsNoTracking().Where(c => c.TenantId == tenant).OrderBy(c => c.Name).ToListAsync(ct);
        return rows.Select(ToConnector).ToList();
    }

    public async Task SaveAsync(GlobalConnector connector, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.GlobalConnectors.FirstOrDefaultAsync(c => c.ConnectorId == connector.ConnectorId, ct);
        if (row is null)
        {
            row = new GlobalConnectorRecord { ConnectorId = connector.ConnectorId, Name = connector.Name, PluginId = connector.PluginId };
            db.GlobalConnectors.Add(row);
        }

        row.TenantId = TenantIds.Normalize(connector.TenantId);
        row.Name = connector.Name;
        row.PluginId = connector.PluginId;
        row.Description = connector.Description;
        row.SettingsJson = JsonSerializer.Serialize(connector.Settings);
        row.SecretKeysJson = JsonSerializer.Serialize(connector.SecretKeys);
        row.UsableInTasks = connector.UsableInTasks;
        row.CreatedBy = connector.CreatedBy;
        row.CreatedAt = connector.CreatedAt;
        row.UpdatedAt = connector.UpdatedAt;
        await db.SaveChangesAsync(ct);
        _cache.TryRemove(connector.ConnectorId, out _);
    }

    public async Task DeleteAsync(string tenantId, string connectorId, CancellationToken ct = default)
    {
        var tenant = TenantIds.Normalize(tenantId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.GlobalConnectorLinks.Where(l => l.TenantId == tenant && l.ConnectorId == connectorId).ExecuteDeleteAsync(ct);
        await db.GlobalConnectors.Where(c => c.TenantId == tenant && c.ConnectorId == connectorId).ExecuteDeleteAsync(ct);
        _cache.TryRemove(connectorId, out _);
    }

    public async Task LinkAsync(GlobalConnectorLink link, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.GlobalConnectorLinks.FirstOrDefaultAsync(l => l.OwnerId == link.OwnerId && l.ConnectionId == link.ConnectionId, ct);
        if (row is null)
        {
            row = new GlobalConnectorLinkRecord { OwnerId = link.OwnerId, ConnectionId = link.ConnectionId, ConnectorId = link.ConnectorId, OwnerKind = link.OwnerKind };
            db.GlobalConnectorLinks.Add(row);
        }

        row.ConnectorId = link.ConnectorId;
        row.TenantId = TenantIds.Normalize(link.TenantId);
        row.OwnerKind = link.OwnerKind;
        row.ConnectionName = link.ConnectionName;
        row.LinkedAt = link.LinkedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task UnlinkAsync(string ownerId, string connectionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.GlobalConnectorLinks.Where(l => l.OwnerId == ownerId && l.ConnectionId == connectionId).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<GlobalConnectorLink>> LinksAsync(string tenantId, string? connectorId = null, CancellationToken ct = default)
    {
        var tenant = TenantIds.Normalize(tenantId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.GlobalConnectorLinks.AsNoTracking().Where(l => l.TenantId == tenant);
        if (connectorId is not null) query = query.Where(l => l.ConnectorId == connectorId);
        return (await query.OrderBy(l => l.LinkedAt).ToListAsync(ct)).Select(l => new GlobalConnectorLink
        {
            ConnectorId = l.ConnectorId,
            TenantId = l.TenantId,
            OwnerKind = l.OwnerKind,
            OwnerId = l.OwnerId,
            ConnectionId = l.ConnectionId,
            ConnectionName = l.ConnectionName,
            LinkedAt = l.LinkedAt
        }).ToList();
    }

    private static GlobalConnector ToConnector(GlobalConnectorRecord row) => new()
    {
        ConnectorId = row.ConnectorId,
        TenantId = row.TenantId,
        Name = row.Name,
        PluginId = row.PluginId,
        Description = row.Description,
        Settings = JsonSerializer.Deserialize<Dictionary<string, string>>(row.SettingsJson) ?? [],
        SecretKeys = JsonSerializer.Deserialize<List<string>>(row.SecretKeysJson) ?? [],
        UsableInTasks = row.UsableInTasks,
        CreatedBy = row.CreatedBy,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt
    };
}
