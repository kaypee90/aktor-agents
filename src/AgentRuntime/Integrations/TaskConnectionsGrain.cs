using AgentRuntime.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;

namespace AgentRuntime.Integrations;

[GenerateSerializer]
public sealed class TaskConnectionsState
{
    /// <summary>The organization the task belongs to, set by the first connection.</summary>
    [Id(0)] public string? TenantId { get; set; }
    [Id(1)] public Dictionary<string, ConnectionDefinition> Connections { get; set; } = [];
}

/// <summary>
/// The tool connections (MCP servers, HTTP APIs) the owner of one task added for its agents. Keyed
/// by task id. Works like a workspace's connections: settings in state, secrets in the encrypted
/// vault under the task's scope, tools discovered when added and switchable one by one. Only tool
/// providers can be connected: a task has no inbound channel or notifications. Every agent of the
/// task with the Integrations permission (the root, and the agents it starts) sees the enabled tools
/// from its next step, as {connection}__{tool}.
/// </summary>
public interface ITaskConnectionsGrain : IGrainWithStringKey
{
    /// <summary>Validates the connection and discovers its tools; refuses another organization's task.</summary>
    Task<ConnectionResult> AddConnection(string tenantId, ConnectionRequest request);

    Task<ConnectionResult> UpdateConnection(string connectionId, ConnectionUpdate update);

    Task<ConnectionResult> RefreshConnectionTools(string connectionId);

    /// <summary>Removes the connection and deletes its secrets.</summary>
    Task RemoveConnection(string connectionId);

    [AlwaysInterleave]
    Task<IReadOnlyList<ConnectionView>> ListConnections();

    /// <summary>Read by agents on every step, so it interleaves with a connection being added.</summary>
    [AlwaysInterleave]
    Task<IReadOnlyList<ConnectionToolDescriptor>> GetConnectionTools();

    [AlwaysInterleave]
    Task<ConnectionToolTarget?> ResolveConnectionTool(string exposedName);
}

/// <inheritdoc cref="ITaskConnectionsGrain"/>
public sealed class TaskConnectionsGrain(
    [PersistentState("taskconnections", "Default")] IPersistentState<TaskConnectionsState> state,
    PluginCatalog plugins,
    IntegrationService integrations,
    ISecretStore secrets,
    IOptions<IntegrationsOptions> options,
    ILogger<TaskConnectionsGrain> logger) : Grain, ITaskConnectionsGrain
{
    private string TaskId => this.GetPrimaryKeyString();
    private TaskConnectionsState S => state.State;

    public async Task<ConnectionResult> AddConnection(string tenantId, ConnectionRequest request)
    {
        if (S.TenantId is { } owner && !Tenancy.TenantIds.Same(owner, tenantId)) return ConnectionResult.Fail("No such task.");
        if (S.Connections.Count >= options.Value.MaxConnectionsPerWorkspace)
        {
            return ConnectionResult.Fail($"A task can have at most {options.Value.MaxConnectionsPerWorkspace} connections.");
        }

        var plugin = plugins.Get(request.PluginId);
        if (plugin is null) return ConnectionResult.Fail($"No plugin '{request.PluginId}' is installed.");
        if (plugin is not Plugins.IToolProviderPlugin)
        {
            return ConnectionResult.Fail($"{plugin.Manifest.Name} doesn't provide tools. A task can connect tool servers like MCP; use a workspace for messaging channels.");
        }

        var name = ConnectionNames.Slug(string.IsNullOrWhiteSpace(request.Name) ? plugin.Manifest.Id : request.Name);
        if (S.Connections.Values.Any(c => c.Name == name)) return ConnectionResult.Fail($"This task already has a connection named '{name}'.");

        // Only fields the plugin declares are kept: secrets go to the vault, settings to state.
        var settings = new Dictionary<string, string>();
        var secretValues = new Dictionary<string, string>();
        foreach (var field in plugin.Manifest.Settings)
        {
            var source = field.Secret ? request.Secrets : request.Settings;
            source.TryGetValue(field.Key, out var value);
            if (string.IsNullOrWhiteSpace(value)) value = field.Secret ? null : field.DefaultValue;
            if (string.IsNullOrWhiteSpace(value))
            {
                if (field.Required) return ConnectionResult.Fail($"'{field.Label}' is required.");
                continue;
            }

            if (field.Secret) secretValues[field.Key] = value.Trim();
            else settings[field.Key] = value.Trim();
        }

        var definition = new ConnectionDefinition
        {
            ConnectionId = DeterministicId.FromOrNew("conn-", null),
            PluginId = plugin.Manifest.Id,
            Name = name,
            Settings = settings,
            SecretKeys = secretValues.Keys.ToList(),
            SupportsTools = true
        };

        var scope = ConnectionNames.Scope(TaskId, definition.ConnectionId);
        foreach (var (key, value) in secretValues) await secrets.PutAsync(scope, key, value);

        var (check, tools) = await integrations.InspectAsync(TaskId, definition);
        if (!check.Ok)
        {
            await secrets.DeleteScopeAsync(scope);
            return ConnectionResult.Fail(check.Message);
        }

        definition.Tools = tools;
        S.TenantId ??= Tenancy.TenantIds.Normalize(tenantId);
        S.Connections[definition.ConnectionId] = definition;
        await state.WriteStateAsync();
        logger.LogInformation("Task {TaskId} connected {Plugin} as {Name} ({Tools} tools)", TaskId, plugin.Manifest.Id, name, tools.Count);
        return ConnectionResult.Ok(ToView(definition),
            tools.Count == 0 ? $"Connected {plugin.Manifest.Name} as '{name}', but it offers no tools."
                : $"Connected {plugin.Manifest.Name} as '{name}': {tools.Count(t => t.Enabled)} of {tools.Count} tools enabled for the task's agents.");
    }

    public async Task<ConnectionResult> UpdateConnection(string connectionId, ConnectionUpdate update)
    {
        if (!S.Connections.TryGetValue(connectionId, out var c)) return ConnectionResult.Fail("No such connection.");
        if (update.EnabledTools is { } enabled)
        {
            var set = enabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var t in c.Tools) t.Enabled = set.Contains(t.ExposedName) || set.Contains(t.LocalName);
        }

        if (update.Settings is { } changes)
        {
            if (plugins.Get(c.PluginId) is not { } plugin) return ConnectionResult.Fail($"The plugin '{c.PluginId}' isn't installed.");
            var merged = ConnectionSettings.Merge(plugin.Manifest, c.Settings, changes, out var invalid);
            if (invalid is not null) return ConnectionResult.Fail(invalid);
            var candidate = new ConnectionDefinition
            {
                ConnectionId = c.ConnectionId, PluginId = c.PluginId, Name = c.Name, Settings = merged, SecretKeys = c.SecretKeys, Tools = c.Tools
            };
            var (check, tools) = await integrations.InspectAsync(TaskId, candidate);
            if (!check.Ok) return ConnectionResult.Fail(check.Message);
            c.Settings = merged;
            c.Tools = tools;
            c.LastError = null;
        }

        await state.WriteStateAsync();
        return ConnectionResult.Ok(ToView(c), "Updated.");
    }

    public async Task<ConnectionResult> RefreshConnectionTools(string connectionId)
    {
        if (!S.Connections.TryGetValue(connectionId, out var c)) return ConnectionResult.Fail("No such connection.");
        var (check, tools) = await integrations.InspectAsync(TaskId, c);
        c.LastError = check.Ok ? null : check.Message;
        if (check.Ok) c.Tools = tools;
        await state.WriteStateAsync();
        return check.Ok ? ConnectionResult.Ok(ToView(c), $"{tools.Count} tools.") : ConnectionResult.Fail(check.Message);
    }

    public async Task RemoveConnection(string connectionId)
    {
        if (!S.Connections.Remove(connectionId)) return;
        await secrets.DeleteScopeAsync(ConnectionNames.Scope(TaskId, connectionId));
        await state.WriteStateAsync();
    }

    public Task<IReadOnlyList<ConnectionView>> ListConnections() =>
        Task.FromResult<IReadOnlyList<ConnectionView>>(S.Connections.Values.Select(ToView).ToList());

    public Task<IReadOnlyList<ConnectionToolDescriptor>> GetConnectionTools() =>
        Task.FromResult<IReadOnlyList<ConnectionToolDescriptor>>(S.Connections.Values
            .SelectMany(c => c.Tools.Where(t => t.Enabled).Select(t => new ConnectionToolDescriptor
            {
                Name = t.ExposedName,
                Description = $"[{c.Name}, {c.PluginId}] {t.Description}",
                JsonSchema = t.JsonSchema,
                SideEffects = t.SideEffects
            }))
            .ToList());

    public Task<ConnectionToolTarget?> ResolveConnectionTool(string exposedName)
    {
        foreach (var c in S.Connections.Values)
        {
            if (c.Tools.FirstOrDefault(t => t.Enabled && t.ExposedName == exposedName) is { } tool)
            {
                return Task.FromResult<ConnectionToolTarget?>(new ConnectionToolTarget { Connection = c, LocalName = tool.LocalName, SideEffects = tool.SideEffects });
            }
        }

        return Task.FromResult<ConnectionToolTarget?>(null);
    }

    private static ConnectionView ToView(ConnectionDefinition c) => new()
    {
        ConnectionId = c.ConnectionId,
        PluginId = c.PluginId,
        Name = c.Name,
        Settings = new Dictionary<string, string>(c.Settings),
        SecretKeys = c.SecretKeys.ToList(),
        Tools = c.Tools.Select(t => new ConnectionToolView { Name = t.ExposedName, Description = t.Description, SideEffects = t.SideEffects, Enabled = t.Enabled }).ToList(),
        CreatedAt = c.CreatedAt,
        LastError = c.LastError,
        SupportsTools = true
    };
}
