namespace AgentRuntime.Contracts;

[GenerateSerializer]
public sealed record AgentInitializationRequest
{
    [Id(0)] public required string AgentId { get; init; }
    [Id(1)] public string? ParentAgentId { get; init; }
    [Id(2)] public required string RootAgentId { get; init; }
    [Id(3)] public required string Name { get; init; }
    [Id(4)] public required string Role { get; init; }
    [Id(5)] public required string Goal { get; init; }
    [Id(6)] public List<string> Capabilities { get; init; } = [];
    [Id(7)] public List<string> AllowedTools { get; init; } = [];
    [Id(8)] public ToolPermission GrantedPermissions { get; init; } = ToolPermission.None;
    [Id(9)] public required ResourceBudget Budget { get; init; }
    [Id(10)] public int Depth { get; init; }
    [Id(11)] public string? InitialContext { get; init; }
    [Id(12)] public string TaskId { get; init; } = string.Empty;
    [Id(13)] public string? WorldId { get; init; }
    [Id(14)] public Dictionary<string, string> Metadata { get; init; } = [];
}

/// <summary>Tool-facing request produced by an agent's LLM turn asking to spawn a child.</summary>
[GenerateSerializer]
public sealed record SpawnAgentRequest
{
    [Id(0)] public required string Role { get; init; }
    [Id(1)] public required string Goal { get; init; }
    [Id(2)] public List<string> Capabilities { get; init; } = [];
    [Id(3)] public string? InitialContext { get; init; }
    [Id(4)] public List<string>? RequestedTools { get; init; }
    [Id(5)] public ResourceBudget? RequestedBudget { get; init; }
}

[GenerateSerializer]
public sealed record SpawnAgentResult
{
    [Id(0)] public required string AgentId { get; init; }
    [Id(1)] public required string Status { get; init; }
    [Id(2)] public string? RejectionReason { get; init; }
    [Id(3)] public ResourceBudget? GrantedBudget { get; init; }

    public bool Success => RejectionReason is null;
}

[GenerateSerializer]
public sealed record FindAgentsQuery
{
    [Id(0)] public List<string>? Capabilities { get; init; }
    [Id(1)] public AgentStatus? Status { get; init; }
    [Id(2)] public string? RootAgentId { get; init; }
}

[GenerateSerializer]
public sealed record AgentDirectoryEntry
{
    [Id(0)] public required string AgentId { get; init; }
    [Id(1)] public required string Role { get; init; }
    [Id(2)] public required string Goal { get; init; }
    [Id(3)] public AgentStatus Status { get; init; }
    [Id(4)] public List<string> Capabilities { get; init; } = [];
    [Id(5)] public string? ParentAgentId { get; init; }
    [Id(6)] public int Depth { get; init; }
    [Id(7)] public string RootAgentId { get; init; } = string.Empty;
}

[GenerateSerializer]
public sealed record SpawnValidationResult
{
    [Id(0)] public bool Allowed { get; init; }
    [Id(1)] public string? RejectionReason { get; init; }
    [Id(2)] public int AllowedDepth { get; init; }

    public static SpawnValidationResult Allow(int depth) => new() { Allowed = true, AllowedDepth = depth };
    public static SpawnValidationResult Reject(string reason) => new() { Allowed = false, RejectionReason = reason };
}

[GenerateSerializer]
public sealed record CompleteTaskRequest
{
    [Id(0)] public required string Status { get; init; }
    [Id(1)] public required string Summary { get; init; }
    [Id(2)] public List<string> Artifacts { get; init; } = [];
    [Id(3)] public List<string> Evidence { get; init; } = [];
    [Id(4)] public List<string> RemainingWork { get; init; } = [];
}

[GenerateSerializer]
public sealed record TaskResult
{
    [Id(0)] public required string Status { get; init; }
    [Id(1)] public required string Summary { get; init; }
    [Id(2)] public List<string> Findings { get; init; } = [];
    [Id(3)] public List<string> Artifacts { get; init; } = [];
    [Id(4)] public int ParticipatingAgents { get; init; }
    [Id(5)] public List<string> UnresolvedItems { get; init; } = [];
    [Id(6)] public Dictionary<string, string> Metrics { get; init; } = [];
}

[GenerateSerializer]
public sealed record AgentSnapshot
{
    [Id(0)] public required string AgentId { get; init; }
    [Id(1)] public string? ParentAgentId { get; init; }
    [Id(2)] public required string RootAgentId { get; init; }
    [Id(3)] public required string Name { get; init; }
    [Id(4)] public required string Role { get; init; }
    [Id(5)] public required string Goal { get; init; }
    [Id(6)] public AgentStatus Status { get; init; }
    [Id(7)] public List<string> Capabilities { get; init; } = [];
    [Id(8)] public List<string> AllowedTools { get; init; } = [];
    [Id(18)] public ToolPermission GrantedPermissions { get; init; } = ToolPermission.None;
    [Id(9)] public DateTimeOffset CreatedAt { get; init; }
    [Id(10)] public DateTimeOffset? StartedAt { get; init; }
    [Id(11)] public DateTimeOffset? CompletedAt { get; init; }
    [Id(12)] public string? CurrentTask { get; init; }
    [Id(13)] public List<string> Children { get; init; } = [];
    [Id(14)] public ResourceBudget Budget { get; init; } = new();
    [Id(15)] public ResourceUsage Usage { get; init; } = new();
    [Id(16)] public int Depth { get; init; }
    [Id(19)] public string TaskId { get; init; } = string.Empty;
    [Id(17)] public string? FailureReason { get; init; }
    [Id(20)] public string? WorldId { get; init; }
}

[GenerateSerializer]
public sealed record ArtifactRef
{
    [Id(0)] public required string ArtifactId { get; init; }
    [Id(1)] public ArtifactType Type { get; init; }
    [Id(2)] public required string Location { get; init; }
    [Id(3)] public required string CreatedByAgent { get; init; }
    [Id(4)] public DateTimeOffset CreatedAt { get; init; }
    [Id(5)] public Dictionary<string, string> Metadata { get; init; } = [];
}
