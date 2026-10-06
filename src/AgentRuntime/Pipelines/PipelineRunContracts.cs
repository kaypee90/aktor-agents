using AgentRuntime.Contracts;

namespace AgentRuntime.Pipelines;

public enum PipelineRunStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
    TimedOut
}

public enum StageRunStatus
{
    /// <summary>Waiting for its inputs.</summary>
    Pending,
    Running,
    Completed,
    Failed,
    /// <summary>Not run: an input failed and the run stopped, or the run was cancelled.</summary>
    Skipped
}

/// <summary>Everything a run needs, fixed when it starts: the pipeline at that version and the
/// workspace's limits, so a run never calls back into its workspace to find out.</summary>
[GenerateSerializer]
public sealed record PipelineRunRequest
{
    [Id(0)] public required string WorkspaceId { get; init; }
    [Id(1)] public required string WorkspaceName { get; init; }
    [Id(2)] public required string TenantId { get; init; }
    [Id(3)] public required PipelineDefinition Pipeline { get; init; }
    /// <summary>What to do this time: a task typed by a person, a webhook's payload, a watch's matches.</summary>
    [Id(4)] public required string Input { get; init; }
    /// <summary>"manual", "schedule", "webhook" or "watch".</summary>
    [Id(5)] public string Source { get; init; } = "manual";
    [Id(6)] public string? TriggerId { get; init; }
    [Id(7)] public string? TriggerName { get; init; }
    /// <summary>Who started it: a user id, "key:&lt;id&gt;", or "trigger".</summary>
    [Id(8)] public string? StartedBy { get; init; }
    /// <summary>The budget each stage's agent gets (from the workspace), unless the stage sets its own cost.</summary>
    [Id(9)] public required ResourceBudget StageBudget { get; init; }
    /// <summary>The workspace's team-shape rules, which apply to every stage's helpers too.</summary>
    [Id(10)] public Safety.TeamPolicy? Team { get; init; }
    /// <summary>The run's number in its workspace (Run #12).</summary>
    [Id(11)] public int Number { get; init; }
}

[GenerateSerializer]
public sealed class StageRun
{
    [Id(0)] public required string StageId { get; set; }
    [Id(1)] public StageRunStatus Status { get; set; } = StageRunStatus.Pending;
    /// <summary>The agent doing the stage now (the latest attempt's).</summary>
    [Id(2)] public string? AgentId { get; set; }
    [Id(3)] public int Attempts { get; set; }
    /// <summary>"completed" or "partial" from the agent's report.</summary>
    [Id(4)] public string? Outcome { get; set; }
    [Id(5)] public string? Summary { get; set; }
    [Id(6)] public List<string> Artifacts { get; set; } = [];
    [Id(7)] public string? Error { get; set; }
    [Id(8)] public DateTimeOffset? StartedAt { get; set; }
    [Id(9)] public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Every agent that worked on the stage, one per attempt.</summary>
    [Id(10)] public List<string> AgentIds { get; set; } = [];
}

[GenerateSerializer]
public sealed class PipelineRunState
{
    [Id(0)] public string RunId { get; set; } = string.Empty;
    [Id(1)] public PipelineRunRequest? Request { get; set; }
    [Id(2)] public PipelineRunStatus Status { get; set; } = PipelineRunStatus.Queued;
    [Id(3)] public Dictionary<string, StageRun> Stages { get; set; } = [];
    [Id(4)] public DateTimeOffset CreatedAt { get; set; }
    [Id(5)] public DateTimeOffset? StartedAt { get; set; }
    [Id(6)] public DateTimeOffset? CompletedAt { get; set; }
    [Id(7)] public string? Summary { get; set; }
    [Id(8)] public bool Paused { get; set; }
    /// <summary>Completion notices already applied (by message id), so a redelivered one is ignored.</summary>
    [Id(9)] public HashSet<string> HandledNotices { get; set; } = [];
    [Id(10)] public bool WorkspaceNotified { get; set; }
}

/// <summary>A run as the API and dashboard see it.</summary>
[GenerateSerializer]
public sealed record PipelineRunView
{
    [Id(0)] public required string RunId { get; init; }
    [Id(1)] public required string WorkspaceId { get; init; }
    [Id(2)] public int Number { get; init; }
    [Id(3)] public PipelineRunStatus Status { get; init; }
    [Id(4)] public bool Paused { get; init; }
    [Id(5)] public string Input { get; init; } = string.Empty;
    [Id(6)] public string Source { get; init; } = "manual";
    [Id(7)] public string? TriggerName { get; init; }
    [Id(8)] public string? StartedBy { get; init; }
    [Id(9)] public int PipelineVersion { get; init; }
    [Id(10)] public DateTimeOffset CreatedAt { get; init; }
    [Id(11)] public DateTimeOffset? StartedAt { get; init; }
    [Id(12)] public DateTimeOffset? CompletedAt { get; init; }
    [Id(13)] public string? Summary { get; init; }
    [Id(14)] public List<StageRunView> Stages { get; init; } = [];
}

[GenerateSerializer]
public sealed record StageRunView
{
    [Id(0)] public required string StageId { get; init; }
    [Id(1)] public required string Name { get; init; }
    [Id(2)] public StageRunStatus Status { get; init; }
    [Id(3)] public string? AgentId { get; init; }
    [Id(4)] public int Attempts { get; init; }
    [Id(5)] public string? Outcome { get; init; }
    [Id(6)] public string? Summary { get; init; }
    [Id(7)] public List<string> Artifacts { get; init; } = [];
    [Id(8)] public string? Error { get; init; }
    [Id(9)] public DateTimeOffset? StartedAt { get; init; }
    [Id(10)] public DateTimeOffset? CompletedAt { get; init; }
    [Id(11)] public List<string> Inputs { get; init; } = [];
}

/// <summary>How an agent ended, for whoever is waiting on it (a pipeline run).</summary>
[GenerateSerializer]
public sealed record AgentOutcome
{
    [Id(0)] public AgentStatus Status { get; init; }
    /// <summary>"completed" or "partial" when it reported; null otherwise.</summary>
    [Id(1)] public string? CompletionStatus { get; init; }
    [Id(2)] public string? Summary { get; init; }
    [Id(3)] public List<string> Artifacts { get; init; } = [];
    [Id(4)] public List<string> RemainingWork { get; init; } = [];
    [Id(5)] public string? FailureReason { get; init; }
}

/// <summary>A pipeline stage's agent, as the run asks the runtime to create it.</summary>
[GenerateSerializer]
public sealed record StageAgentLaunch
{
    [Id(0)] public required string AgentId { get; init; }
    [Id(1)] public required string RunId { get; init; }
    [Id(2)] public required string WorkspaceId { get; init; }
    [Id(3)] public required string TenantId { get; init; }
    [Id(4)] public required PipelineStage Stage { get; init; }
    [Id(5)] public required string Goal { get; init; }
    [Id(6)] public required string InitialContext { get; init; }
    [Id(7)] public required ResourceBudget Budget { get; init; }
    [Id(8)] public Safety.TeamPolicy? Team { get; init; }
    [Id(9)] public string? CorrelationId { get; init; }
}
