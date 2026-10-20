using System.Collections.Concurrent;
using AgentRuntime.Tenancy;

namespace AgentRuntime.Integrations;

/// <summary>
/// A connection set up once for a whole organization (docs/plugins.md#global-connectors): a plugin,
/// its settings, and its secrets in the vault under <see cref="GlobalConnectors.Scope"/>. Workspaces,
/// tasks and solutions link to it instead of holding their own copy of the secrets, so a key is
/// entered once and rotated in one place. Each link is still an ordinary connection in its owner
/// (its own tool switches, notifications, safety policy and audit log); only the plugin, settings
/// and secrets are shared.
/// </summary>
public sealed record GlobalConnector
{
    public required string ConnectorId { get; init; }
    public required string TenantId { get; init; }
    /// <summary>Unique in the organization, and the default name (tool prefix) of each link.</summary>
    public required string Name { get; init; }
    public required string PluginId { get; init; }
    public string? Description { get; init; }
    /// <summary>Non-secret settings: read live by every link, so a change reaches all of them.</summary>
    public Dictionary<string, string> Settings { get; init; } = [];
    /// <summary>Which secrets are set, never their values.</summary>
    public List<string> SecretKeys { get; init; } = [];
    /// <summary>Whether members may attach it to their own tasks (chats). Off by default: workspaces
    /// and solutions are set up by admins, but any member can start a task.</summary>
    public bool UsableInTasks { get; init; }
    public string? CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Where a global connector is used: one connection of a workspace or a task.</summary>
public sealed record GlobalConnectorLink
{
    public required string ConnectorId { get; init; }
    public required string TenantId { get; init; }
    /// <summary>"workspace" or "task".</summary>
    public required string OwnerKind { get; init; }
    public required string OwnerId { get; init; }
    public required string ConnectionId { get; init; }
    public string ConnectionName { get; init; } = string.Empty;
    public DateTimeOffset LinkedAt { get; init; }
}

/// <summary>Global connectors and their links. Never holds a secret value.</summary>
public interface IGlobalConnectorStore
{
    /// <summary>Within one organization only; null for another organization's connector.</summary>
    Task<GlobalConnector?> GetAsync(string tenantId, string idOrName, CancellationToken ct = default);

    /// <summary>By id alone, for links already checked to be in the right organization.</summary>
    Task<GlobalConnector?> GetByIdAsync(string connectorId, CancellationToken ct = default);

    Task<IReadOnlyList<GlobalConnector>> ListAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Creates or replaces it (by id).</summary>
    Task SaveAsync(GlobalConnector connector, CancellationToken ct = default);

    /// <summary>Deletes the connector and its links.</summary>
    Task DeleteAsync(string tenantId, string connectorId, CancellationToken ct = default);

    Task LinkAsync(GlobalConnectorLink link, CancellationToken ct = default);

    Task UnlinkAsync(string ownerId, string connectionId, CancellationToken ct = default);

    Task<IReadOnlyList<GlobalConnectorLink>> LinksAsync(string tenantId, string? connectorId = null, CancellationToken ct = default);
}

public static class GlobalConnectors
{
    public const string IdPrefix = "gc-";
    public const string WorkspaceOwner = "workspace";
    public const string TaskOwner = "task";

    public static string NewId() => IdPrefix + Guid.NewGuid().ToString("n")[..10];

    /// <summary>The vault scope of a global connector's secrets, shared by all its links.</summary>
    public static string Scope(string connectorId) => $"global/{connectorId}";

    /// <summary>Where a connection's secrets are: its global connector's, else its own.</summary>
    public static string SecretScope(string ownerId, ConnectionDefinition connection) =>
        connection.GlobalConnectorId is { Length: > 0 } id ? Scope(id) : ConnectionNames.Scope(ownerId, connection.ConnectionId);
}

/// <summary>Global connectors kept in process (tests and the standalone runtime).</summary>
public sealed class InMemoryGlobalConnectorStore : IGlobalConnectorStore
{
    private readonly ConcurrentDictionary<string, GlobalConnector> _connectors = new();
    private readonly ConcurrentDictionary<(string Owner, string Connection), GlobalConnectorLink> _links = new();

    public Task<GlobalConnector?> GetAsync(string tenantId, string idOrName, CancellationToken ct = default) =>
        Task.FromResult(_connectors.Values.FirstOrDefault(c => TenantIds.Same(c.TenantId, tenantId) &&
                                                              (c.ConnectorId == idOrName || string.Equals(c.Name, idOrName, StringComparison.OrdinalIgnoreCase))));

    public Task<GlobalConnector?> GetByIdAsync(string connectorId, CancellationToken ct = default) =>
        Task.FromResult(_connectors.GetValueOrDefault(connectorId));

    public Task<IReadOnlyList<GlobalConnector>> ListAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GlobalConnector>>(_connectors.Values.Where(c => TenantIds.Same(c.TenantId, tenantId)).OrderBy(c => c.Name).ToList());

    public Task SaveAsync(GlobalConnector connector, CancellationToken ct = default)
    {
        _connectors[connector.ConnectorId] = connector;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string tenantId, string connectorId, CancellationToken ct = default)
    {
        if (_connectors.TryGetValue(connectorId, out var c) && TenantIds.Same(c.TenantId, tenantId))
        {
            _connectors.TryRemove(connectorId, out _);
            foreach (var key in _links.Where(l => l.Value.ConnectorId == connectorId).Select(l => l.Key).ToList()) _links.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public Task LinkAsync(GlobalConnectorLink link, CancellationToken ct = default)
    {
        _links[(link.OwnerId, link.ConnectionId)] = link;
        return Task.CompletedTask;
    }

    public Task UnlinkAsync(string ownerId, string connectionId, CancellationToken ct = default)
    {
        _links.TryRemove((ownerId, connectionId), out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GlobalConnectorLink>> LinksAsync(string tenantId, string? connectorId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GlobalConnectorLink>>(_links.Values
            .Where(l => TenantIds.Same(l.TenantId, tenantId) && (connectorId is null || l.ConnectorId == connectorId))
            .OrderBy(l => l.LinkedAt).ToList());
}
