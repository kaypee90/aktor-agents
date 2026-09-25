namespace AgentRuntime.Workspaces;

public enum WorkspaceStatus
{
    Active,
    /// <summary>Triggers don't fire and agents are paused; nothing spends tokens.</summary>
    Paused,
    /// <summary>Permanently stopped: agents retired, triggers removed.</summary>
    Archived
}

public enum TriggerKind
{
    Schedule,
    Webhook
}

public enum ChatAuthorKind
{
    User,
    Agent,
    System
}

public static class WorkspaceIds
{
    public const string Prefix = "ws-";

    public static string New() => Prefix + Guid.NewGuid().ToString("n")[..10];

    /// <summary>Workspace agents use the workspace id as their task id.</summary>
    public static bool IsWorkspace(string? taskId) => taskId?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static string CoordinatorId(string workspaceId) => $"coord-{workspaceId[Prefix.Length..]}";
}

[GenerateSerializer]
public sealed record WorkspaceCreationRequest
{
    [Id(0)] public required string Name { get; init; }
    /// <summary>What the user wants this workspace to do; the coordinator's first instruction.</summary>
    [Id(1)] public required string Goal { get; init; }
    [Id(2)] public int? DailyTokenLimit { get; init; }
    [Id(3)] public decimal? DailyCostLimitUsd { get; init; }
    [Id(4)] public string OwnerId { get; init; } = "local";
}

[GenerateSerializer]
public sealed record ChatEntry
{
    [Id(0)] public long Seq { get; init; }
    [Id(1)] public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    [Id(2)] public ChatAuthorKind AuthorKind { get; init; }
    /// <summary>"user", an agent id, or "system".</summary>
    [Id(3)] public required string AuthorId { get; init; }
    [Id(4)] public required string AuthorName { get; init; }
    [Id(5)] public required string Text { get; init; }
    /// <summary>For agent notices: "info", "warning" or "urgent" — lets connectors (SMS, Slack) decide what to forward.</summary>
    [Id(6)] public string Urgency { get; init; } = "info";
}

[GenerateSerializer]
public sealed class TriggerDefinition
{
    [Id(0)] public required string TriggerId { get; set; }
    [Id(1)] public required TriggerKind Kind { get; set; }
    [Id(2)] public required string Name { get; set; }
    /// <summary>The agent woken when the trigger fires.</summary>
    [Id(3)] public required string TargetAgentId { get; set; }
    /// <summary>What the agent is told to do each time, e.g. "Check stock levels and alert on anything below 10".</summary>
    [Id(4)] public string Instruction { get; set; } = string.Empty;
    [Id(5)] public int? IntervalSeconds { get; set; }
    [Id(6)] public string? Cron { get; set; }
    /// <summary>Webhook URL secret. Shown to the user when created; never given to an agent.</summary>
    [Id(7)] public string? Secret { get; set; }
    [Id(8)] public string CreatedBy { get; set; } = "user";
    [Id(9)] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Id(10)] public bool Enabled { get; set; } = true;
    [Id(11)] public DateTimeOffset? LastFiredAt { get; set; }
    [Id(12)] public long FireCount { get; set; }
    [Id(13)] public DateTimeOffset? NextDueAt { get; set; }
    [Id(14)] public long DroppedCount { get; set; }
}

[GenerateSerializer]
public sealed record TriggerSpec
{
    [Id(0)] public required TriggerKind Kind { get; init; }
    [Id(1)] public required string Name { get; init; }
    [Id(2)] public string? TargetAgentId { get; init; }
    [Id(3)] public string Instruction { get; init; } = string.Empty;
    [Id(4)] public double? EveryMinutes { get; init; }
    [Id(5)] public string? Cron { get; init; }
}

[GenerateSerializer]
public sealed record TriggerView
{
    [Id(0)] public required string TriggerId { get; init; }
    [Id(1)] public TriggerKind Kind { get; init; }
    [Id(2)] public required string Name { get; init; }
    [Id(3)] public required string TargetAgentId { get; init; }
    [Id(4)] public string Instruction { get; init; } = string.Empty;
    [Id(5)] public int? IntervalSeconds { get; init; }
    [Id(6)] public string? Cron { get; init; }
    /// <summary>Relative webhook path including its secret — only returned to the user/API.</summary>
    [Id(7)] public string? WebhookPath { get; init; }
    [Id(8)] public bool Enabled { get; init; }
    [Id(9)] public DateTimeOffset? LastFiredAt { get; init; }
    [Id(10)] public long FireCount { get; init; }
    [Id(11)] public DateTimeOffset? NextDueAt { get; init; }
    [Id(12)] public string CreatedBy { get; init; } = "user";
    [Id(13)] public long DroppedCount { get; init; }
}

[GenerateSerializer]
public sealed record WorkspaceActionResult
{
    [Id(0)] public bool Success { get; init; }
    [Id(1)] public string Message { get; init; } = string.Empty;
    [Id(2)] public string? ResultJson { get; init; }

    public static WorkspaceActionResult Ok(string message, string? json = null) => new() { Success = true, Message = message, ResultJson = json };
    public static WorkspaceActionResult Fail(string message) => new() { Success = false, Message = message };
}

[GenerateSerializer]
public sealed record BudgetDecision
{
    [Id(0)] public bool Allowed { get; init; }
    [Id(1)] public string? Reason { get; init; }
}

/// <summary>Budgets the workspace hands its agents (the runtime, not the parent agent, decides).</summary>
[GenerateSerializer]
public sealed record WorkspacePolicy
{
    [Id(0)] public required Contracts.ResourceBudget StandingBudget { get; init; }
    [Id(1)] public required Contracts.ResourceBudget WorkerBudget { get; init; }
    [Id(2)] public int StandingContextWindow { get; init; }
    [Id(3)] public int MaxAgents { get; init; }
    [Id(4)] public WorkspaceStatus Status { get; init; }
}

[GenerateSerializer]
public sealed record RateWindow
{
    [Id(0)] public long Minute { get; init; }
    [Id(1)] public int Count { get; init; }
}

[GenerateSerializer]
public sealed class WorkspaceState
{
    [Id(0)] public string WorkspaceId { get; set; } = string.Empty;
    [Id(1)] public string Name { get; set; } = string.Empty;
    [Id(2)] public string Goal { get; set; } = string.Empty;
    [Id(3)] public string OwnerId { get; set; } = "local";
    [Id(4)] public WorkspaceStatus Status { get; set; } = WorkspaceStatus.Active;
    [Id(5)] public DateTimeOffset CreatedAt { get; set; }
    [Id(6)] public string CoordinatorAgentId { get; set; } = string.Empty;
    [Id(7)] public List<ChatEntry> Conversation { get; set; } = [];
    [Id(8)] public long NextChatSeq { get; set; } = 1;
    [Id(9)] public Dictionary<string, TriggerDefinition> Triggers { get; set; } = [];

    // Daily budget ledger (UTC days).
    [Id(10)] public int DailyTokenLimit { get; set; }
    [Id(11)] public decimal DailyCostLimitUsd { get; set; }
    [Id(12)] public string UsageDay { get; set; } = string.Empty;
    [Id(13)] public long TokensToday { get; set; }
    [Id(14)] public decimal CostToday { get; set; }
    [Id(15)] public string? BudgetNoticeDay { get; set; }
    [Id(16)] public long TotalTokens { get; set; }
    [Id(17)] public decimal TotalCostUsd { get; set; }

    /// <summary>Recent action/delivery keys (bounded), so replays and redelivered webhooks are dropped.</summary>
    [Id(18)] public Dictionary<string, WorkspaceActionResult> ActionResults { get; set; } = [];
    [Id(19)] public List<string> ActionResultOrder { get; set; } = [];

    /// <summary>Per-trigger webhook arrivals in the current minute, for rate limiting.</summary>
    [Id(20)] public Dictionary<string, RateWindow> WebhookRate { get; set; } = [];

    [Id(21)] public DateTimeOffset UpdatedAt { get; set; }
}

[GenerateSerializer]
public sealed record WorkspaceAgentView
{
    [Id(0)] public required string AgentId { get; init; }
    [Id(1)] public required string Role { get; init; }
    [Id(2)] public string Goal { get; init; } = string.Empty;
    [Id(3)] public string Status { get; init; } = string.Empty;
    [Id(4)] public string? ParentAgentId { get; init; }
    [Id(5)] public bool Standing { get; init; }
    [Id(6)] public long TokensUsed { get; init; }
    [Id(7)] public decimal CostUsd { get; init; }
    [Id(8)] public string? CurrentTask { get; init; }
}

[GenerateSerializer]
public sealed record WorkspaceSnapshot
{
    [Id(0)] public required string WorkspaceId { get; init; }
    [Id(1)] public required string Name { get; init; }
    [Id(2)] public string Goal { get; init; } = string.Empty;
    [Id(3)] public WorkspaceStatus Status { get; init; }
    [Id(4)] public DateTimeOffset CreatedAt { get; init; }
    [Id(5)] public DateTimeOffset UpdatedAt { get; init; }
    [Id(6)] public string CoordinatorAgentId { get; init; } = string.Empty;
    [Id(7)] public List<ChatEntry> Conversation { get; init; } = [];
    [Id(8)] public List<TriggerView> Triggers { get; init; } = [];
    [Id(9)] public List<WorkspaceAgentView> Agents { get; init; } = [];
    [Id(10)] public int DailyTokenLimit { get; init; }
    [Id(11)] public decimal DailyCostLimitUsd { get; init; }
    [Id(12)] public long TokensToday { get; init; }
    [Id(13)] public decimal CostToday { get; init; }
    [Id(14)] public long TotalTokens { get; init; }
    [Id(15)] public decimal TotalCostUsd { get; init; }
}

/// <summary>Durable list of workspaces for the API (the grain holds the live state).</summary>
public interface IWorkspaceArchive
{
    Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default);
}

public sealed class NullWorkspaceArchive : IWorkspaceArchive
{
    public Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
