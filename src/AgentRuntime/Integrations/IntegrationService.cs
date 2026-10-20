using AgentRuntime.Plugins;
using AgentRuntime.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Integrations;

/// <summary>All installed plugins (built-in and loaded from the plugins folder), by id.</summary>
public sealed class PluginCatalog(IEnumerable<IAgentPlugin> plugins, ILogger<PluginCatalog> logger)
{
    private readonly Dictionary<string, IAgentPlugin> _plugins = Index(plugins, logger);

    public IReadOnlyCollection<IAgentPlugin> All => _plugins.Values;

    public IAgentPlugin? Get(string pluginId) => _plugins.GetValueOrDefault(pluginId);

    private static Dictionary<string, IAgentPlugin> Index(IEnumerable<IAgentPlugin> plugins, ILogger logger)
    {
        var index = new Dictionary<string, IAgentPlugin>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins)
        {
            if (!index.TryAdd(plugin.Manifest.Id, plugin))
            {
                logger.LogWarning("Duplicate plugin id {PluginId} ({Type}) ignored", plugin.Manifest.Id, plugin.GetType().FullName);
            }
        }

        return index;
    }
}

/// <summary>Changing a connection's settings after it was made: only the plugin's declared,
/// non-secret fields (secrets are set once, when connecting).</summary>
public static class ConnectionSettings
{
    public static Dictionary<string, string> Merge(Plugins.PluginManifest manifest, IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> changes, out string? error)
    {
        error = null;
        var merged = new Dictionary<string, string>(current);
        foreach (var field in manifest.Settings.Where(f => !f.Secret))
        {
            if (!changes.TryGetValue(field.Key, out var value)) continue;
            value = value?.Trim();
            if (string.IsNullOrEmpty(value)) value = field.DefaultValue;
            if (string.IsNullOrEmpty(value))
            {
                if (field.Required)
                {
                    error = $"'{field.Label}' is required.";
                    return merged;
                }

                merged.Remove(field.Key);
            }
            else
            {
                merged[field.Key] = value;
            }
        }

        return merged;
    }
}

/// <summary>
/// Runs plugin code on behalf of the runtime: builds a <see cref="PluginConnection"/> with its
/// secrets decrypted just for the call, and applies the runtime's limits around it (result size,
/// description size, exceptions become tool failures). Plugins never see which agent is calling
/// beyond the tool request, and agents never see secrets.
/// </summary>
public sealed class IntegrationService(
    PluginCatalog catalog,
    ISecretStore secrets,
    IGlobalConnectorStore globals,
    IOptions<IntegrationsOptions> options,
    ILogger<IntegrationService> logger)
{
    private readonly IntegrationsOptions _opts = options.Value;

    public async Task<PluginConnection> BuildConnectionAsync(string workspaceId, ConnectionDefinition definition, CancellationToken ct = default)
    {
        // A linked global connector is read live: its current settings, and every secret its plugin
        // declares (one may have been added or rotated since the link was made).
        var settings = definition.Settings;
        IEnumerable<string> secretKeys = definition.SecretKeys;
        if (definition.GlobalConnectorId is { Length: > 0 } globalId)
        {
            var global = await globals.GetByIdAsync(globalId, ct)
                         ?? throw new InvalidOperationException($"The global connector behind '{definition.Name}' was deleted.");
            settings = global.Settings;
            secretKeys = catalog.Get(global.PluginId)?.Manifest.Settings.Where(f => f.Secret).Select(f => f.Key) ?? global.SecretKeys;
        }

        var scope = GlobalConnectors.SecretScope(workspaceId, definition);
        var values = new Dictionary<string, string>();
        foreach (var key in secretKeys)
        {
            if (await secrets.GetAsync(scope, key, ct) is { } value) values[key] = value;
        }

        return new PluginConnection
        {
            ConnectionId = definition.ConnectionId,
            WorkspaceId = workspaceId,
            Name = definition.Name,
            Settings = settings,
            Secrets = values,
            InboundSecret = definition.InboundSecret,
            InboundUrl = InboundUrl(workspaceId, definition)
        };
    }

    public string InboundPath(string workspaceId, ConnectionDefinition definition) =>
        $"/api/channels/{workspaceId}/{definition.ConnectionId}/{definition.InboundSecret}";

    public string? InboundUrl(string workspaceId, ConnectionDefinition definition) =>
        string.IsNullOrWhiteSpace(_opts.PublicBaseUrl) ? null : _opts.PublicBaseUrl.TrimEnd('/') + InboundPath(workspaceId, definition);

    public async Task<ToolExecutionResult> ExecuteToolAsync(string workspaceId, ConnectionToolTarget target, ToolExecutionRequest request)
    {
        if (catalog.Get(target.Connection.PluginId) is not IToolProviderPlugin plugin)
        {
            return ToolExecutionResult.Fail($"The plugin '{target.Connection.PluginId}' for connection '{target.Connection.Name}' isn't installed.");
        }

        try
        {
            var connection = await BuildConnectionAsync(workspaceId, target.Connection, request.CancellationToken);
            var result = await plugin.ExecuteToolAsync(connection, target.LocalName, request);
            return result.Success
                ? result with { ResultJson = ConnectionNames.Truncate(result.ResultJson, _opts.MaxToolResultChars) }
                : result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Connection tool {Connection}.{Tool} failed", target.Connection.Name, target.LocalName);
            return ToolExecutionResult.Fail($"{target.Connection.Name}.{target.LocalName} failed: {ex.Message}");
        }
    }

    /// <summary>Validates a new connection and discovers its tools. Returns the tools to cache.</summary>
    public async Task<(ConnectionCheck Check, List<CachedTool> Tools)> InspectAsync(string workspaceId, ConnectionDefinition definition, CancellationToken ct = default)
    {
        var plugin = catalog.Get(definition.PluginId);
        if (plugin is null) return (ConnectionCheck.Failure($"No plugin '{definition.PluginId}' is installed."), []);

        PluginConnection connection;
        ConnectionCheck check;
        try
        {
            connection = await BuildConnectionAsync(workspaceId, definition, ct);
            check = await plugin.ValidateAsync(connection, ct);
        }
        catch (Exception ex)
        {
            return (ConnectionCheck.Failure(ex.Message), []);
        }

        if (!check.Ok || plugin is not IToolProviderPlugin tools) return (check, []);

        try
        {
            var listed = await tools.ListToolsAsync(connection, ct);
            var cached = listed.Select((t, i) => new CachedTool
            {
                LocalName = t.Name,
                ExposedName = ConnectionNames.Expose(definition.Name, t.Name),
                Description = ConnectionNames.Truncate(t.Description, _opts.MaxToolDescriptionChars),
                JsonSchema = string.IsNullOrWhiteSpace(t.JsonSchema) ? """{"type":"object","properties":{}}""" : t.JsonSchema,
                SideEffects = t.SideEffects,
                // Big servers start with only the first N enabled: every enabled tool costs tokens on every call.
                Enabled = i < _opts.MaxEnabledToolsPerConnection
            }).ToList();

            // Keep the previous on/off choices when refreshing.
            var previous = definition.Tools.ToDictionary(t => t.LocalName, t => t.Enabled);
            foreach (var t in cached)
            {
                if (previous.TryGetValue(t.LocalName, out var enabled)) t.Enabled = enabled;
            }

            return (check, cached);
        }
        catch (Exception ex)
        {
            return (ConnectionCheck.Failure($"Connected, but listing its tools failed: {ex.Message}"), []);
        }
    }

    public async Task<DeliveryResult> SendNotificationAsync(string workspaceId, string workspaceName, ConnectionDefinition definition, NotificationDelivery delivery, CancellationToken ct = default)
    {
        if (catalog.Get(definition.PluginId) is not INotificationChannelPlugin plugin)
        {
            return DeliveryResult.Failed($"The plugin '{definition.PluginId}' can't send notifications.", retryable: false);
        }

        try
        {
            var connection = await BuildConnectionAsync(workspaceId, definition, ct);
            return await plugin.SendNotificationAsync(connection, new OutboundNotification
            {
                DeliveryId = delivery.DeliveryId,
                WorkspaceName = workspaceName,
                AuthorName = delivery.AuthorName,
                Text = delivery.Text,
                Urgency = delivery.Urgency
            }, ct);
        }
        catch (Exception ex)
        {
            return DeliveryResult.Failed(ex.Message);
        }
    }

    /// <summary>Checks a plugin's settings and secrets before anything is saved (a new or changed
    /// global connector): whether it connects, and how many tools it offers.</summary>
    public async Task<(ConnectionCheck Check, int Tools)> CheckAsync(string pluginId, string name, IReadOnlyDictionary<string, string> settings,
        IReadOnlyDictionary<string, string> secretValues, CancellationToken ct = default)
    {
        if (catalog.Get(pluginId) is not { } plugin) return (ConnectionCheck.Failure($"No plugin '{pluginId}' is installed."), 0);
        var connection = new PluginConnection
        {
            ConnectionId = "check",
            WorkspaceId = "global",
            Name = name,
            Settings = new Dictionary<string, string>(settings),
            Secrets = new Dictionary<string, string>(secretValues)
        };
        try
        {
            var check = await plugin.ValidateAsync(connection, ct);
            if (!check.Ok || plugin is not IToolProviderPlugin tools) return (check, 0);
            return (check, (await tools.ListToolsAsync(connection, ct)).Count);
        }
        catch (Exception ex)
        {
            return (ConnectionCheck.Failure(ex.Message), 0);
        }
    }

    /// <summary>
    /// A request to link a global connector, turned into the connection it makes: the connector's
    /// plugin and settings, no secrets of its own (they stay in the connector's vault scope), and
    /// its name unless one was given. Refuses another organization's connector as if it didn't exist.
    /// A request without <see cref="ConnectionRequest.GlobalConnector"/> comes back unchanged.
    /// </summary>
    public async Task<(ConnectionRequest Request, GlobalConnector? Global, string? Error)> ResolveGlobalAsync(string? tenantId, ConnectionRequest request,
        string ownerKind = GlobalConnectors.WorkspaceOwner, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.GlobalConnector)) return (request, null, null);
        var global = await globals.GetAsync(Tenancy.TenantIds.Normalize(tenantId), request.GlobalConnector.Trim(), ct);
        if (global is null) return (request, null, $"There's no global connector '{request.GlobalConnector}' in this organization.");
        if (ownerKind == GlobalConnectors.TaskOwner && !global.UsableInTasks && !request.AdminChosenGlobal)
        {
            return (request, null, $"The global connector '{global.Name}' isn't available to tasks; an admin can allow it under Connectors.");
        }

        return (request with
        {
            PluginId = global.PluginId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? global.Name : request.Name,
            Settings = new Dictionary<string, string>(global.Settings),
            Secrets = []
        }, global, null);
    }

    /// <summary>Records where a global connector is used, so it can be refreshed and isn't deleted under its users.</summary>
    public Task LinkGlobalAsync(string? tenantId, string ownerKind, string ownerId, ConnectionDefinition connection, CancellationToken ct = default) =>
        connection.GlobalConnectorId is not { Length: > 0 } id
            ? Task.CompletedTask
            : globals.LinkAsync(new GlobalConnectorLink
            {
                ConnectorId = id,
                TenantId = Tenancy.TenantIds.Normalize(tenantId),
                OwnerKind = ownerKind,
                OwnerId = ownerId,
                ConnectionId = connection.ConnectionId,
                ConnectionName = connection.Name,
                LinkedAt = DateTimeOffset.UtcNow
            }, ct);

    /// <summary>
    /// Forgets a connection's secrets when it's removed: its own vault scope, or, for a link to a
    /// global connector, only the link (the connector's secrets belong to everyone using it).
    /// </summary>
    public async Task ForgetConnectionAsync(string ownerId, ConnectionDefinition connection, CancellationToken ct = default)
    {
        if (connection.GlobalConnectorId is { Length: > 0 }) await globals.UnlinkAsync(ownerId, connection.ConnectionId, ct);
        else await secrets.DeleteScopeAsync(ConnectionNames.Scope(ownerId, connection.ConnectionId), ct);
    }

    public async Task<InboundResult> ParseInboundAsync(string workspaceId, ConnectionDefinition definition, InboundRequestDto request, CancellationToken ct = default)
    {
        if (catalog.Get(definition.PluginId) is not IInboundChannelPlugin plugin) return InboundResult.Reject();

        var connection = await BuildConnectionAsync(workspaceId, definition, ct);
        return await plugin.ParseInboundAsync(connection, new InboundRequest
        {
            Body = request.Body,
            ContentType = request.ContentType,
            Headers = request.Headers,
            Query = request.Query,
            PublicUrl = InboundUrl(workspaceId, definition)
        }, ct);
    }
}
