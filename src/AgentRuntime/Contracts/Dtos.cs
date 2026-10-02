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

    /// <summary>Start the agent's first turn as part of initialization — one durable write, so a
    /// crash can't leave an agent created but never started.</summary>
    [Id(15)] public bool AutoStart { get; init; }
    [Id(16)] public string? WorkspaceId { get; init; }
    [Id(17)] public bool Standing { get; init; }
    [Id(18)] public int ContextWindow { get; init; }
    [Id(19)] public string TenantId { get; init; } = string.Empty;
    /// <summary>The caller's correlation id (API, MCP, A2A), stamped on every event of the agent
    /// tree so a run can be traced across systems.</summary>
    [Id(20)] public string? CorrelationId { get; init; }
    /// <summary>The task's or workspace's team-shape policy, inherited by every agent of the tree.</summary>
    [Id(21)] public Safety.TeamPolicy? TeamPolicy { get; init; }
    /// <summary>Where the agent sits in its tree, for the step journal: "r", then "/{spawn call id}" per level.</summary>
    [Id(22)] public string? JournalPath { get; init; }
    [Id(23)] public Durability.ReplaySpec? Replay { get; init; }
    /// <summary>A model chosen for this agent; null follows the task's model.</summary>
    [Id(24)] public string? ModelProfileId { get; init; }
}

/// <summary>How a task's root agent is launched. Every entry point (REST, MCP, A2A, ACP) goes
/// through the same options, so none of them can bypass a limit the others enforce.</summary>
[GenerateSerializer]
public sealed record TaskLaunchOptions
{
    [Id(0)] public ResourceBudget? Budget { get; init; }
    [Id(1)] public string? TenantId { get; init; }
    [Id(2)] public string? CorrelationId { get; init; }
    /// <summary>Team-shape rules for this task, on top of the server's (they can only tighten them).</summary>
    [Id(3)] public Safety.TeamPolicy? TeamPolicy { get; init; }
    /// <summary>Replay a past run from its step journal (roadmap P6).</summary>
    [Id(4)] public Durability.ReplaySpec? Replay { get; init; }
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
    /// <summary>Inside a workspace: the child is a standing agent (monitor, responder) rather than
    /// a one-shot worker.</summary>
    [Id(6)] public bool Standing { get; init; }
    /// <summary>The spawning agent's stated reason for not doing the work itself (shown in events).</summary>
    [Id(7)] public string? Justification { get; init; }
    /// <summary>The model the new agent should run on: an organization model's id or name.
    /// Without one it runs on its parent's model.</summary>
    [Id(8)] public string? Model { get; init; }
}

[GenerateSerializer]
public sealed record SpawnAgentResult
{
    [Id(0)] public required string AgentId { get; init; }
    [Id(1)] public required string Status { get; init; }
    [Id(2)] public string? RejectionReason { get; init; }
    [Id(3)] public ResourceBudget? GrantedBudget { get; init; }
    /// <summary>What the new agent may cost and how much spawn allowance is left, for the spawner.</summary>
    [Id(4)] public string? Note { get; init; }
    /// <summary>Machine-readable reason for a rejection (e.g. "duplicate_role") and its details.</summary>
    [Id(5)] public string? RejectionRule { get; init; }
    [Id(6)] public string? RejectionDetailsJson { get; init; }

    public bool Success => RejectionReason is null;
}

[GenerateSerializer]
public sealed record FindAgentsQuery
{
    [Id(0)] public List<string>? Capabilities { get; init; }
    [Id(1)] public AgentStatus? Status { get; init; }
    [Id(2)] public string? RootAgentId { get; init; }
    [Id(3)] public string? TenantId { get; init; }
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
    [Id(8)] public string TenantId { get; init; } = string.Empty;
}

[GenerateSerializer]
public sealed record SpawnValidationResult
{
    [Id(0)] public bool Allowed { get; init; }
    [Id(1)] public string? RejectionReason { get; init; }
    [Id(2)] public int AllowedDepth { get; init; }
    /// <summary>The team-shape rule that refused the spawn (see Safety.TeamRules), if one did.</summary>
    [Id(3)] public string? Rule { get; init; }
    [Id(4)] public string? DetailsJson { get; init; }
    [Id(5)] public string? ExistingAgentId { get; init; }

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
    [Id(21)] public string? WorkspaceId { get; init; }
    [Id(22)] public bool Standing { get; init; }
    [Id(23)] public string TenantId { get; init; } = string.Empty;
    [Id(24)] public int SpawnsThisRequest { get; init; }
    [Id(25)] public int PlannedWorkersLeft { get; init; }
    [Id(26)] public string? PauseReason { get; init; }
    [Id(27)] public DateTimeOffset? PausedUntil { get; init; }
    [Id(28)] public string? CorrelationId { get; init; }
    [Id(29)] public Safety.TeamPolicy? TeamPolicy { get; init; }
    [Id(30)] public string JournalPath { get; init; } = "r";
    [Id(31)] public Durability.ReplaySpec? Replay { get; init; }
    /// <summary>The model chosen for this agent at spawn; null when it follows the task's model.</summary>
    [Id(32)] public string? ModelProfileId { get; init; }
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
