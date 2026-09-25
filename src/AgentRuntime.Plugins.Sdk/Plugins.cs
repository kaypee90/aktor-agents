using AgentRuntime.Tools;

namespace AgentRuntime.Plugins;

/// <summary>
/// A plugin adds an integration to the platform (docs/plugins.md). Users install it into a
/// workspace as a <em>connection</em> (settings plus secrets). A plugin can do any combination of:
/// <list type="bullet">
/// <item>provide tools for the workspace's agents (implement <see cref="IToolProviderPlugin"/>);</item>
/// <item>deliver the workspace's notifications to the user (<see cref="INotificationChannelPlugin"/>);</item>
/// <item>turn inbound messages into user commands (<see cref="IInboundChannelPlugin"/>).</item>
/// </list>
/// Plugins are constructed with dependency injection (e.g. <c>IHttpClientFactory</c>, logging).
/// The runtime, not the plugin, enforces permissions, budgets, deduplication and secrecy: plugin
/// code runs only when the runtime decides it should.
/// </summary>
public interface IAgentPlugin
{
    PluginManifest Manifest { get; }

    /// <summary>Checks settings and secrets when a connection is added (e.g. calls an auth
    /// endpoint), so mistakes surface immediately rather than on the first agent action.</summary>
    Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken);
}

/// <summary>Contributes tools to agents. The runtime exposes each tool as
/// <c>{connection}__{tool}</c>, so two connections of the same plugin don't collide.</summary>
public interface IToolProviderPlugin : IAgentPlugin
{
    /// <summary>The tools this connection offers. Called when the connection is added or
    /// refreshed; the result is cached, so this can be slow (e.g. an MCP tools/list).</summary>
    Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken);

    /// <summary>Runs one tool. <paramref name="toolName"/> is the plugin's own (un-namespaced)
    /// name. Pass <see cref="ToolExecutionRequest.IdempotencyKey"/> to the external system when it
    /// supports idempotency, and declare the tool's <see cref="ToolDefinition.SideEffects"/> honestly.</summary>
    Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request);
}

/// <summary>Delivers notifications (an agent's notify_user) to the user through this connection.</summary>
public interface INotificationChannelPlugin : IAgentPlugin
{
    /// <summary>Send one notification. <see cref="OutboundNotification.DeliveryId"/> is stable
    /// across retries: pass it on when the channel can deduplicate. Throw or return a failure to
    /// have the runtime retry with backoff.</summary>
    Task<DeliveryResult> SendNotificationAsync(PluginConnection connection, OutboundNotification notification, CancellationToken cancellationToken);
}

/// <summary>Turns an inbound request (an SMS reply, a chat message) into a command for the
/// workspace. The runtime has already checked the connection's secret URL; the plugin verifies
/// provider signatures and extracts sender and text. The runtime then checks the sender against
/// the connection's allowed senders.</summary>
public interface IInboundChannelPlugin : IAgentPlugin
{
    Task<InboundResult> ParseInboundAsync(PluginConnection connection, InboundRequest request, CancellationToken cancellationToken);
}

public enum PluginCategory
{
    Tools,
    Messaging,
    Data,
    Other
}

public sealed record PluginManifest
{
    /// <summary>Stable id, e.g. "twilio-sms". Lowercase letters, digits and dashes.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string Version { get; init; } = "1.0.0";
    public PluginCategory Category { get; init; } = PluginCategory.Other;
    public IReadOnlyList<PluginSettingField> Settings { get; init; } = [];
    /// <summary>Shown to users adding a connection: where to get keys, what to configure.</summary>
    public string? SetupHelp { get; init; }
}

public sealed record PluginSettingField
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    /// <summary>Stored encrypted in the vault; never shown back, never given to agents.</summary>
    public bool Secret { get; init; }
    public bool Required { get; init; }
    public string? Placeholder { get; init; }
    public string? DefaultValue { get; init; }
    /// <summary>For a fixed set of choices.</summary>
    public IReadOnlyList<string>? Options { get; init; }
}

/// <summary>One installed instance of a plugin, as handed to plugin code for a single operation.
/// Secrets are decrypted for the duration of the call only.</summary>
public sealed record PluginConnection
{
    public required string ConnectionId { get; init; }
    public required string WorkspaceId { get; init; }
    /// <summary>The connection's short name (also its tool prefix), e.g. "shop".</summary>
    public required string Name { get; init; }
    public required IReadOnlyDictionary<string, string> Settings { get; init; }
    public required IReadOnlyDictionary<string, string> Secrets { get; init; }
    /// <summary>Public URL for this connection's inbound endpoint, when the server knows its public
    /// address (Integrations:PublicBaseUrl); e.g. to register a Telegram webhook.</summary>
    public string? InboundUrl { get; init; }
    /// <summary>Per-connection secret embedded in the inbound URL; also usable as a shared secret
    /// with providers that support one (e.g. Telegram's secret_token).</summary>
    public string? InboundSecret { get; init; }

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public string Secret(string key) =>
        Secrets.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new InvalidOperationException($"The connection '{Name}' has no '{key}' secret configured.");
}

public sealed record ConnectionCheck
{
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;

    public static ConnectionCheck Success(string message = "Connected.") => new() { Ok = true, Message = message };
    public static ConnectionCheck Failure(string message) => new() { Ok = false, Message = message };
}

public sealed record OutboundNotification
{
    /// <summary>Stable across retries of this notification to this connection.</summary>
    public required string DeliveryId { get; init; }
    public required string WorkspaceName { get; init; }
    public required string AuthorName { get; init; }
    public required string Text { get; init; }
    /// <summary>"info", "warning" or "urgent".</summary>
    public string Urgency { get; init; } = "info";
}

public sealed record DeliveryResult
{
    public bool Delivered { get; init; }
    public string? Error { get; init; }
    /// <summary>False for errors retrying can't fix (bad credentials, invalid number).</summary>
    public bool Retryable { get; init; } = true;

    public static DeliveryResult Ok() => new() { Delivered = true };
    public static DeliveryResult Failed(string error, bool retryable = true) => new() { Delivered = false, Error = error, Retryable = retryable };
}

public sealed record InboundRequest
{
    public required string Body { get; init; }
    public string? ContentType { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();
    /// <summary>The full public URL the provider called, for signature schemes that sign it (Twilio).</summary>
    public string? PublicUrl { get; init; }
}

public sealed record InboundResult
{
    /// <summary>False: not a user message (a delivery receipt, a bot's own message) — ignore it.</summary>
    public bool IsMessage { get; init; }
    /// <summary>The signature check failed: reject and log.</summary>
    public bool Rejected { get; init; }
    public string? SenderId { get; init; }
    public string? SenderName { get; init; }
    public string? Text { get; init; }
    /// <summary>The provider's message id, so a redelivered message is handled once.</summary>
    public string? MessageId { get; init; }
    /// <summary>What to answer the provider with (e.g. an empty TwiML response).</summary>
    public string ResponseBody { get; init; } = string.Empty;
    public string ResponseContentType { get; init; } = "text/plain";

    public static InboundResult Ignore(string responseBody = "", string contentType = "text/plain") =>
        new() { IsMessage = false, ResponseBody = responseBody, ResponseContentType = contentType };
    public static InboundResult Reject() => new() { Rejected = true };
}
