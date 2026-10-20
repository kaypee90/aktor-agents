using AgentRuntime.Infrastructure.Persistence;
using AgentRuntime.Integrations;
using AgentRuntime.Tenancy;
using AgentRuntime.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Api.Platform;

public sealed class GlobalConnectorException(string message, int status = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>What a person may set on a global connector. Null leaves a value as it is.</summary>
public sealed record GlobalConnectorInput
{
    public string? Name { get; init; }
    public string? PluginId { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, string>? Settings { get; init; }
    /// <summary>New secret values; a secret left out (or empty) keeps its stored value.</summary>
    public Dictionary<string, string>? Secrets { get; init; }
    public bool? UsableInTasks { get; init; }
}

/// <summary>Where a global connector is used, as the dashboard shows it.</summary>
public sealed record GlobalConnectorUse(string OwnerKind, string OwnerId, string? OwnerName, string ConnectionId, string ConnectionName);

/// <summary>
/// Global connectors (docs/plugins.md#global-connectors): an organization's integrations set up
/// once, with their secrets in one vault scope, and linked from workspaces, tasks and solutions.
/// Every change is checked against the service first (nothing is saved if it doesn't connect), and
/// a change to settings or secrets refreshes the tools of every connection that links it.
/// </summary>
public sealed class GlobalConnectorService(
    IGlobalConnectorStore store,
    ISecretStore secrets,
    PluginCatalog plugins,
    IntegrationService integrations,
    IGrainFactory grains,
    IDbContextFactory<AgentDbContext> dbFactory,
    TimeProvider clock,
    ILogger<GlobalConnectorService> logger)
{
    public Task<IReadOnlyList<GlobalConnector>> ListAsync(string tenantId, CancellationToken ct) => store.ListAsync(tenantId, ct);

    public async Task<GlobalConnector> GetAsync(string tenantId, string idOrName, CancellationToken ct) =>
        await store.GetAsync(tenantId, idOrName, ct) ?? throw new GlobalConnectorException($"No global connector '{idOrName}'.", StatusCodes.Status404NotFound);

    /// <summary>Where each connector is used; workspaces the caller can't see are left out.</summary>
    public async Task<Dictionary<string, List<GlobalConnectorUse>>> UsesAsync(string tenantId, ISet<string> hiddenWorkspaces, CancellationToken ct)
    {
        var links = (await store.LinksAsync(tenantId, null, ct)).Where(l => !hiddenWorkspaces.Contains(l.OwnerId)).ToList();
        var workspaceIds = links.Where(l => l.OwnerKind == GlobalConnectors.WorkspaceOwner).Select(l => l.OwnerId).Distinct().ToList();
        var taskIds = links.Where(l => l.OwnerKind == GlobalConnectors.TaskOwner).Select(l => l.OwnerId).Distinct().ToList();
        var tenant = TenantIds.Normalize(tenantId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var workspaceNames = await db.Workspaces.AsNoTracking().Where(w => w.TenantId == tenant && workspaceIds.Contains(w.WorkspaceId))
            .ToDictionaryAsync(w => w.WorkspaceId, w => w.Name, ct);
        var taskNames = await db.Tasks.AsNoTracking().Where(t => t.TenantId == tenant && taskIds.Contains(t.TaskId))
            .ToDictionaryAsync(t => t.TaskId, t => t.Goal, ct);
        return links.GroupBy(l => l.ConnectorId).ToDictionary(g => g.Key, g => g.Select(l => new GlobalConnectorUse(
            l.OwnerKind, l.OwnerId,
            l.OwnerKind == GlobalConnectors.WorkspaceOwner ? workspaceNames.GetValueOrDefault(l.OwnerId) : Clip(taskNames.GetValueOrDefault(l.OwnerId), 80),
            l.ConnectionId, l.ConnectionName)).ToList());
    }

    public async Task<GlobalConnector> CreateAsync(string tenantId, string by, GlobalConnectorInput input, CancellationToken ct)
    {
        var plugin = plugins.Get(input.PluginId ?? string.Empty) ?? throw new GlobalConnectorException($"No plugin '{input.PluginId}' is installed.");
        if (plugin.Manifest.Id == Infrastructure.Solutions.RecordsPlugin.PluginId) throw new GlobalConnectorException("A solution's records connection can't be shared.");
        var name = ConnectionNames.Slug(string.IsNullOrWhiteSpace(input.Name) ? plugin.Manifest.Id : input.Name);
        if (await store.GetAsync(tenantId, name, ct) is not null) throw new GlobalConnectorException($"There's already a global connector named '{name}'.", StatusCodes.Status409Conflict);

        var (settings, secretValues) = Split(plugin.Manifest, new Dictionary<string, string>(), input.Settings ?? [], new Dictionary<string, string>(), input.Secrets ?? []);
        await CheckAsync(plugin.Manifest.Id, name, settings, secretValues, ct);

        var now = clock.GetUtcNow();
        var connector = new GlobalConnector
        {
            ConnectorId = GlobalConnectors.NewId(),
            TenantId = TenantIds.Normalize(tenantId),
            Name = name,
            PluginId = plugin.Manifest.Id,
            Description = Clip(input.Description?.Trim(), 500),
            Settings = settings,
            SecretKeys = secretValues.Keys.ToList(),
            UsableInTasks = input.UsableInTasks ?? false,
            CreatedBy = by,
            CreatedAt = now,
            UpdatedAt = now
        };
        foreach (var (key, value) in secretValues) await secrets.PutAsync(GlobalConnectors.Scope(connector.ConnectorId), key, value, ct);
        await store.SaveAsync(connector, ct);
        logger.LogInformation("Global connector {Name} ({Plugin}) created as {ConnectorId} for {TenantId} by {By}", name, plugin.Manifest.Id, connector.ConnectorId, tenantId, by);
        return connector;
    }

    public async Task<(GlobalConnector Connector, int Refreshed, List<string> Problems)> UpdateAsync(string tenantId, string idOrName, GlobalConnectorInput input, CancellationToken ct)
    {
        var current = await GetAsync(tenantId, idOrName, ct);
        if (input.Name is { } rename && ConnectionNames.Slug(rename) != current.Name)
        {
            throw new GlobalConnectorException("A global connector's name can't change: it identifies it everywhere it's linked. Create a new one instead.");
        }

        var plugin = plugins.Get(current.PluginId) ?? throw new GlobalConnectorException($"The plugin '{current.PluginId}' isn't installed.");
        var scope = GlobalConnectors.Scope(current.ConnectorId);
        var stored = new Dictionary<string, string>();
        foreach (var key in current.SecretKeys)
        {
            if (await secrets.GetAsync(scope, key, ct) is { Length: > 0 } value) stored[key] = value;
        }

        var changesConnection = input.Settings is not null || input.Secrets is { Count: > 0 };
        var (settings, secretValues) = Split(plugin.Manifest, current.Settings, input.Settings ?? [], stored, input.Secrets ?? []);
        if (changesConnection) await CheckAsync(plugin.Manifest.Id, current.Name, settings, secretValues, ct);

        var updated = current with
        {
            Description = input.Description is null ? current.Description : Clip(input.Description.Trim(), 500),
            Settings = settings,
            SecretKeys = secretValues.Keys.ToList(),
            UsableInTasks = input.UsableInTasks ?? current.UsableInTasks,
            UpdatedAt = clock.GetUtcNow()
        };
        foreach (var (key, value) in secretValues.Where(s => stored.GetValueOrDefault(s.Key) != s.Value)) await secrets.PutAsync(scope, key, value, ct);
        await store.SaveAsync(updated, ct);

        // Links read settings and secrets live; their cached tool lists are what may be stale.
        var refreshed = 0;
        var problems = new List<string>();
        if (changesConnection)
        {
            foreach (var link in await store.LinksAsync(tenantId, current.ConnectorId, ct))
            {
                var result = link.OwnerKind == GlobalConnectors.WorkspaceOwner
                    ? await grains.GetGrain<IWorkspaceGrain>(link.OwnerId).RefreshConnectionTools(link.ConnectionId)
                    : await grains.GetGrain<ITaskConnectionsGrain>(link.OwnerId).RefreshConnectionTools(link.ConnectionId);
                if (result.Success) refreshed++;
                else problems.Add($"{link.OwnerKind} {link.OwnerId} ('{link.ConnectionName}'): {result.Message}");
            }
        }

        return (updated, refreshed, problems);
    }

    /// <summary>Deletes it and its secrets. Refused while something links it, unless
    /// <paramref name="force"/>: then those connections are removed first.</summary>
    public async Task<int> DeleteAsync(string tenantId, string idOrName, bool force, CancellationToken ct)
    {
        var current = await GetAsync(tenantId, idOrName, ct);
        var links = await store.LinksAsync(tenantId, current.ConnectorId, ct);
        if (links.Count > 0 && !force)
        {
            throw new GlobalConnectorException(
                $"'{current.Name}' is used by {links.Count} connection{(links.Count == 1 ? "" : "s")}. Remove them first, or delete it anyway to remove them too.",
                StatusCodes.Status409Conflict);
        }

        foreach (var link in links)
        {
            if (link.OwnerKind == GlobalConnectors.WorkspaceOwner) await grains.GetGrain<IWorkspaceGrain>(link.OwnerId).RemoveConnection(link.ConnectionId);
            else await grains.GetGrain<ITaskConnectionsGrain>(link.OwnerId).RemoveConnection(link.ConnectionId);
        }

        await secrets.DeleteScopeAsync(GlobalConnectors.Scope(current.ConnectorId), ct);
        await store.DeleteAsync(tenantId, current.ConnectorId, ct);
        logger.LogInformation("Global connector {Name} ({ConnectorId}) deleted for {TenantId}; {Links} linked connections removed", current.Name, current.ConnectorId, tenantId, links.Count);
        return links.Count;
    }

    /// <summary>Checks it against the service again with what's stored now.</summary>
    public async Task<string> TestAsync(string tenantId, string idOrName, CancellationToken ct)
    {
        var current = await GetAsync(tenantId, idOrName, ct);
        var values = new Dictionary<string, string>();
        foreach (var key in current.SecretKeys)
        {
            if (await secrets.GetAsync(GlobalConnectors.Scope(current.ConnectorId), key, ct) is { Length: > 0 } value) values[key] = value;
        }

        return await CheckAsync(current.PluginId, current.Name, current.Settings, values, ct);
    }

    private async Task<string> CheckAsync(string pluginId, string name, Dictionary<string, string> settings, Dictionary<string, string> secretValues, CancellationToken ct)
    {
        var (check, tools) = await integrations.CheckAsync(pluginId, name, settings, secretValues, ct);
        if (!check.Ok) throw new GlobalConnectorException($"It doesn't connect: {check.Message}");
        return tools > 0 ? $"{check.Message} {tools} tools.".Trim() : check.Message;
    }

    /// <summary>Only the fields the plugin declares, each on the right side of the line (settings
    /// in the database, secrets in the vault), with required ones present.</summary>
    private static (Dictionary<string, string> Settings, Dictionary<string, string> Secrets) Split(Plugins.PluginManifest manifest,
        IReadOnlyDictionary<string, string> currentSettings, IReadOnlyDictionary<string, string> settingChanges,
        IReadOnlyDictionary<string, string> currentSecrets, IReadOnlyDictionary<string, string> secretChanges)
    {
        var settings = new Dictionary<string, string>();
        var secretValues = new Dictionary<string, string>();
        foreach (var field in manifest.Settings)
        {
            string? value;
            if (field.Secret)
            {
                value = secretChanges.GetValueOrDefault(field.Key) is { Length: > 0 } given ? given : currentSecrets.GetValueOrDefault(field.Key);
            }
            else
            {
                value = settingChanges.TryGetValue(field.Key, out var changed) ? changed : currentSettings.GetValueOrDefault(field.Key);
                if (string.IsNullOrWhiteSpace(value)) value = field.DefaultValue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                if (field.Required) throw new GlobalConnectorException($"'{field.Label}' is required.");
                continue;
            }

            if (field.Secret) secretValues[field.Key] = value.Trim();
            else settings[field.Key] = value.Trim();
        }

        return (settings, secretValues);
    }

    private static string? Clip(string? s, int max) => s is null || s.Length <= max ? s : s[..max] + "…";
}
