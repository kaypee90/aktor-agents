using System.Text;
using System.Text.RegularExpressions;
using AgentRuntime.Tools;

namespace AgentRuntime.Integrations;

public sealed class IntegrationsOptions
{
    public const string SectionName = "Integrations";

    /// <summary>This server's public address (e.g. https://agents.example.com), used to build
    /// inbound URLs for providers (Twilio, Telegram) and verify their signatures. Leave empty for
    /// local use: outbound integrations still work, inbound ones need a public URL.</summary>
    public string? PublicBaseUrl { get; set; }

    public int MaxConnectionsPerWorkspace { get; set; } = 20;

    /// <summary>Tools enabled by default per connection. Every enabled tool's definition is sent
    /// with every LLM call of every agent in the workspace, so big MCP servers are capped; users can
    /// switch individual tools on or off.</summary>
    public int MaxEnabledToolsPerConnection { get; set; } = 20;
    public int MaxToolDescriptionChars { get; set; } = 400;
    /// <summary>Tool results are truncated to this before they enter an agent's transcript.</summary>
    public int MaxToolResultChars { get; set; } = 8000;

    public int NotificationMaxAttempts { get; set; } = 6;
    /// <summary>First retry delay for a failed notification; doubles each attempt (max 30 min).</summary>
    public int NotificationRetryBaseSeconds { get; set; } = 30;

    /// <summary>Allow MCP servers launched as local processes (stdio), always inside a Docker
    /// container. Off by default: it runs third-party code on this machine's Docker daemon.</summary>
    public bool AllowStdioMcp { get; set; }
    public string StdioMcpMemoryLimit { get; set; } = "512m";
    public string StdioMcpCpuLimit { get; set; } = "1.0";
}

/// <summary>Which notifications a connection forwards to the user.</summary>
public enum NotifyLevel
{
    Off,
    Urgent,
    /// <summary>Warnings and urgent notifications.</summary>
    Warning,
    All
}

[GenerateSerializer]
public sealed class CachedTool
{
    /// <summary>The plugin's own name for the tool.</summary>
    [Id(0)] public required string LocalName { get; set; }
    /// <summary>What agents see: {connection}__{tool}.</summary>
    [Id(1)] public required string ExposedName { get; set; }
    [Id(2)] public string Description { get; set; } = string.Empty;
    [Id(3)] public string JsonSchema { get; set; } = """{"type":"object","properties":{}}""";
    [Id(4)] public ToolSideEffects SideEffects { get; set; } = ToolSideEffects.NonIdempotent;
    [Id(5)] public bool Enabled { get; set; } = true;
}

/// <summary>An installed plugin in a workspace. Holds no secret values — only their keys; the
/// values live encrypted in the <see cref="ISecretStore"/>.</summary>
[GenerateSerializer]
public sealed class ConnectionDefinition
{
    [Id(0)] public required string ConnectionId { get; set; }
    [Id(1)] public required string PluginId { get; set; }
    [Id(2)] public required string Name { get; set; }
    [Id(3)] public Dictionary<string, string> Settings { get; set; } = [];
    [Id(4)] public List<string> SecretKeys { get; set; } = [];
    [Id(5)] public NotifyLevel NotifyLevel { get; set; } = NotifyLevel.Off;
    [Id(6)] public List<CachedTool> Tools { get; set; } = [];
    [Id(7)] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Id(8)] public string? LastError { get; set; }
    [Id(9)] public string InboundSecret { get; set; } = string.Empty;
    /// <summary>Only these sender ids (phone numbers, chat ids) may send commands inbound.</summary>
    [Id(10)] public List<string> AllowedSenders { get; set; } = [];
    [Id(11)] public bool SupportsTools { get; set; }
    [Id(12)] public bool SupportsNotifications { get; set; }
    [Id(13)] public bool SupportsInbound { get; set; }
}

[GenerateSerializer]
public sealed record ConnectionRequest
{
    [Id(0)] public required string PluginId { get; init; }
    [Id(1)] public required string Name { get; init; }
    [Id(2)] public Dictionary<string, string> Settings { get; init; } = [];
    /// <summary>Plain secret values, only in transit: written to the vault, never to grain state.</summary>
    [Id(3)] public Dictionary<string, string> Secrets { get; init; } = [];
    [Id(4)] public NotifyLevel? NotifyLevel { get; init; }
    [Id(5)] public List<string> AllowedSenders { get; init; } = [];
}

[GenerateSerializer]
public sealed record ConnectionUpdate
{
    [Id(0)] public NotifyLevel? NotifyLevel { get; init; }
    /// <summary>Exposed or local tool names to enable; everything else is disabled. Null leaves tools unchanged.</summary>
    [Id(1)] public List<string>? EnabledTools { get; init; }
    [Id(2)] public List<string>? AllowedSenders { get; init; }
}

[GenerateSerializer]
public sealed record ConnectionToolView
{
    [Id(0)] public required string Name { get; init; }
    [Id(1)] public string Description { get; init; } = string.Empty;
    [Id(2)] public ToolSideEffects SideEffects { get; init; }
    [Id(3)] public bool Enabled { get; init; }
}

[GenerateSerializer]
public sealed record ConnectionView
{
    [Id(0)] public required string ConnectionId { get; init; }
    [Id(1)] public required string PluginId { get; init; }
    [Id(2)] public required string Name { get; init; }
    [Id(3)] public Dictionary<string, string> Settings { get; init; } = [];
    /// <summary>Which secrets are set — never their values.</summary>
    [Id(4)] public List<string> SecretKeys { get; init; } = [];
    [Id(5)] public NotifyLevel NotifyLevel { get; init; }
    [Id(6)] public List<ConnectionToolView> Tools { get; init; } = [];
    [Id(7)] public DateTimeOffset CreatedAt { get; init; }
    [Id(8)] public string? LastError { get; init; }
    [Id(9)] public List<string> AllowedSenders { get; init; } = [];
    /// <summary>Relative inbound path (includes its secret) — shown to the owner only.</summary>
    [Id(10)] public string? InboundPath { get; init; }
    [Id(11)] public bool SupportsTools { get; init; }
    [Id(12)] public bool SupportsNotifications { get; init; }
    [Id(13)] public bool SupportsInbound { get; init; }
}

[GenerateSerializer]
public sealed record ConnectionResult
{
    [Id(0)] public bool Success { get; init; }
    [Id(1)] public string Message { get; init; } = string.Empty;
    [Id(2)] public ConnectionView? Connection { get; init; }

    public static ConnectionResult Ok(ConnectionView view, string message) => new() { Success = true, Connection = view, Message = message };
    public static ConnectionResult Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>A connection tool as offered to an agent's LLM call.</summary>
[GenerateSerializer]
public sealed record ConnectionToolDescriptor
{
    [Id(0)] public required string Name { get; init; }
    [Id(1)] public required string Description { get; init; }
    [Id(2)] public required string JsonSchema { get; init; }
    [Id(3)] public ToolSideEffects SideEffects { get; init; }
}

/// <summary>Where a connection tool call goes.</summary>
[GenerateSerializer]
public sealed record ConnectionToolTarget
{
    [Id(0)] public required ConnectionDefinition Connection { get; init; }
    [Id(1)] public required string LocalName { get; init; }
    [Id(2)] public ToolSideEffects SideEffects { get; init; }
}

[GenerateSerializer]
public sealed class NotificationDelivery
{
    [Id(0)] public required string DeliveryId { get; set; }
    [Id(1)] public required string ConnectionId { get; set; }
    [Id(2)] public required string AuthorName { get; set; }
    [Id(3)] public required string Text { get; set; }
    [Id(4)] public string Urgency { get; set; } = "info";
    [Id(5)] public int Attempts { get; set; }
    [Id(6)] public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
}

[GenerateSerializer]
public sealed record InboundRequestDto
{
    [Id(0)] public required string Body { get; init; }
    [Id(1)] public string? ContentType { get; init; }
    [Id(2)] public Dictionary<string, string> Headers { get; init; } = [];
    [Id(3)] public Dictionary<string, string> Query { get; init; } = [];
}

[GenerateSerializer]
public sealed record InboundResponseDto
{
    [Id(0)] public int StatusCode { get; init; } = 200;
    [Id(1)] public string Body { get; init; } = string.Empty;
    [Id(2)] public string ContentType { get; init; } = "text/plain";
}

/// <summary>Encrypted storage for connection secrets. Scoped by workspace and connection.</summary>
public interface ISecretStore
{
    Task PutAsync(string scope, string key, string value, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string scope, string key, CancellationToken cancellationToken = default);
    Task DeleteScopeAsync(string scope, CancellationToken cancellationToken = default);
}

public static partial class ConnectionNames
{
    public const string Separator = "__";

    /// <summary>A connection name agents and URLs can use: lowercase letters, digits, dashes and
    /// underscores (no double underscore, which separates connection from tool).</summary>
    public static string Slug(string name)
    {
        var s = SlugRegex().Replace(name.Trim().ToLowerInvariant(), "_").Trim('_');
        while (s.Contains(Separator)) s = s.Replace(Separator, "_");
        return s.Length == 0 ? "connection" : s.Length > 24 ? s[..24].TrimEnd('_') : s;
    }

    /// <summary>{connection}__{tool}, within LLM providers' tool-name rules (^[a-zA-Z0-9_-]{1,64}$).</summary>
    public static string Expose(string connectionName, string toolName)
    {
        var tool = ToolRegex().Replace(toolName, "_");
        var name = connectionName + Separator + tool;
        return name.Length <= 64 ? name : name[..64];
    }

    public static bool IsConnectionTool(string name) => name.Contains(Separator, StringComparison.Ordinal);

    public static string Scope(string workspaceId, string connectionId) => $"{workspaceId}/{connectionId}";

    public static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        var sb = new StringBuilder(text, 0, max, max + 64);
        sb.Append($"…(truncated, {text.Length:N0} chars total)");
        return sb.ToString();
    }

    [GeneratedRegex("[^a-z0-9_-]+")]
    private static partial Regex SlugRegex();

    [GeneratedRegex("[^a-zA-Z0-9_-]+")]
    private static partial Regex ToolRegex();
}
