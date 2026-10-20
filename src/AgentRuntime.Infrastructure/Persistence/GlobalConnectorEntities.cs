namespace AgentRuntime.Infrastructure.Persistence;

/// <summary>An organization's global connector (docs/plugins.md#global-connectors). Its secrets are
/// in the vault, never here.</summary>
public sealed class GlobalConnectorRecord
{
    public required string ConnectorId { get; set; }
    public string TenantId { get; set; } = "default";
    public required string Name { get; set; }
    public required string PluginId { get; set; }
    public string? Description { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public string SecretKeysJson { get; set; } = "[]";
    public bool UsableInTasks { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A workspace's or task's connection that links a global connector.</summary>
public sealed class GlobalConnectorLinkRecord
{
    public required string OwnerId { get; set; }
    public required string ConnectionId { get; set; }
    public required string ConnectorId { get; set; }
    public string TenantId { get; set; } = "default";
    public required string OwnerKind { get; set; }
    public string ConnectionName { get; set; } = string.Empty;
    public DateTimeOffset LinkedAt { get; set; }
}
