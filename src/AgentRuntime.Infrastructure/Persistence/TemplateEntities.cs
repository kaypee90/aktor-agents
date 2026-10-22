namespace AgentRuntime.Infrastructure.Persistence;

/// <summary>A workspace template an organization made from one of its workspaces (docs/templates.md):
/// the workspace's definition without secrets, chat, runs or files.</summary>
public sealed class OrganizationTemplateRecord
{
    public required string TemplateId { get; set; }
    public string TenantId { get; set; } = "default";
    public required string Name { get; set; }
    public string Category { get; set; } = "Custom";
    public string Description { get; set; } = string.Empty;
    /// <summary>The <c>WorkspaceDefinition</c> as JSON.</summary>
    public string DefinitionJson { get; set; } = "{}";
    public string? SampleInput { get; set; }
    public string? SourceWorkspaceId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
