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

/// <summary>
/// Runs plugin code on behalf of the runtime: builds a <see cref="PluginConnection"/> with its
/// secrets decrypted just for the call, and applies the runtime's limits around it (result size,
/// description size, exceptions become tool failures). Plugins never see which agent is calling
/// beyond the tool request, and agents never see secrets.
/// </summary>
public sealed class IntegrationService(
    PluginCatalog catalog,
    ISecretStore secrets,
    IOptions<IntegrationsOptions> options,
    ILogger<IntegrationService> logger)
{
    private readonly IntegrationsOptions _opts = options.Value;

    public async Task<PluginConnection> BuildConnectionAsync(string workspaceId, ConnectionDefinition definition, CancellationToken ct = default)
    {
        var scope = ConnectionNames.Scope(workspaceId, definition.ConnectionId);
        var values = new Dictionary<string, string>();
        foreach (var key in definition.SecretKeys)
        {
            if (await secrets.GetAsync(scope, key, ct) is { } value) values[key] = value;
        }

        return new PluginConnection
        {
            ConnectionId = definition.ConnectionId,
            WorkspaceId = workspaceId,
            Name = definition.Name,
            Settings = definition.Settings,
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

        var connection = await BuildConnectionAsync(workspaceId, definition, ct);
        ConnectionCheck check;
        try
        {
            check = await plugin.ValidateAsync(connection, ct);
        }
        catch (Exception ex)
        {
            check = ConnectionCheck.Failure(ex.Message);
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
