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
    Webhook,
    /// <summary>A compiled check: the runtime calls a read-only tool on a schedule and evaluates a
    /// rule in code, with no LLM involved unless something matches.</summary>
    Watch
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
    /// <summary>The organization that owns the workspace (set by the API from the caller, never by an agent).</summary>
    [Id(5)] public string TenantId { get; init; } = string.Empty;
    /// <summary>The template the workspace was made from (see WorkspaceTemplates), if any.</summary>
    [Id(6)] public string? TemplateId { get; init; }
    /// <summary>A safety policy to start with, in force before the first run.</summary>
    [Id(7)] public Safety.WorkspaceSafetyPolicy? SafetyPolicy { get; init; }
    /// <summary>The pipeline to start with (from a template, or drafted from the goal); null: one
    /// stage that does the goal.</summary>
    [Id(8)] public Pipelines.PipelineDefinition? Pipeline { get; init; }
    /// <summary>Who created it (a user id or "key:&lt;id&gt;"), recorded on the first pipeline version.</summary>
    [Id(9)] public string? CreatedBy { get; init; }
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
    // Id 3 held the agent a trigger woke (before pipelines: every trigger now starts a run).
    /// <summary>What the agent is told to do each time, e.g. "Summarize new support tickets and flag urgent ones".</summary>
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

    // ---- Watch (TriggerKind.Watch) ----
    [Id(15)] public WatchRule? Rule { get; set; }
    /// <summary>The read-only connection tool the watch calls, e.g. "shop__get".</summary>
    [Id(16)] public string? SourceTool { get; set; }
    [Id(17)] public string SourceArgumentsJson { get; set; } = "{}";
    /// <summary>"notify": the runtime alerts the user itself. "run": a run of the pipeline is started
    /// with only the matching items as its input, when judgement is needed.</summary>
    [Id(18)] public string WatchMode { get; set; } = "notify";
    [Id(19)] public string? MessageTemplate { get; set; }
    [Id(20)] public string Urgency { get; set; } = "warning";
    [Id(21)] public List<string> LastMatchedKeys { get; set; } = [];
    [Id(22)] public long Checks { get; set; }
    [Id(23)] public long Alerts { get; set; }
    [Id(24)] public string? LastError { get; set; }
    [Id(25)] public int ConsecutiveFailures { get; set; }
    [Id(26)] public int LastMatchCount { get; set; }
}

[GenerateSerializer]
public sealed record TriggerSpec
{
    [Id(0)] public required TriggerKind Kind { get; init; }
    [Id(1)] public required string Name { get; init; }
    [Id(3)] public string Instruction { get; init; } = string.Empty;
    [Id(4)] public double? EveryMinutes { get; init; }
    [Id(5)] public string? Cron { get; init; }

    // Watch
    [Id(6)] public WatchRule? Rule { get; init; }
    [Id(7)] public string? SourceTool { get; init; }
    [Id(8)] public string? SourceArgumentsJson { get; init; }
    [Id(9)] public string? WatchMode { get; init; }
    [Id(10)] public string? MessageTemplate { get; init; }
    [Id(11)] public string? Urgency { get; init; }
}

[GenerateSerializer]
public sealed record TriggerView
{
    [Id(0)] public required string TriggerId { get; init; }
    [Id(1)] public TriggerKind Kind { get; init; }
    [Id(2)] public required string Name { get; init; }
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
    [Id(14)] public string? WatchSummary { get; init; }
    [Id(15)] public long Checks { get; init; }
    [Id(16)] public long Alerts { get; init; }
    [Id(17)] public int LastMatchCount { get; init; }
    [Id(18)] public string? LastError { get; init; }
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
    /// <summary>What each stage's agent and each helper may spend in one run.</summary>
    [Id(1)] public required Contracts.ResourceBudget WorkerBudget { get; init; }
    [Id(3)] public int MaxAgents { get; init; }
    [Id(4)] public WorkspaceStatus Status { get; init; }
    /// <summary>Helpers one stage's agent may start in a run.</summary>
    [Id(5)] public int MaxSpawnsPerRequest { get; init; }
    /// <summary>What's left of the workspace's shared daily budget, told to agents when they spawn.</summary>
    [Id(6)] public long TokensLeftToday { get; init; }
    [Id(7)] public decimal CostLeftTodayUsd { get; init; }
    /// <summary>The workspace's team-shape rules (from its safety policy).</summary>
    [Id(8)] public Safety.TeamPolicy? Team { get; init; }
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
    /// <summary>The coordinator agent of workspaces made before pipelines; cleared once migrated.</summary>
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

    // ---- Integrations (phase 3) ----
    [Id(22)] public Dictionary<string, Integrations.ConnectionDefinition> Connections { get; set; } = [];
    /// <summary>Notifications owed to channels (SMS, Slack, ...). Saved with the chat entry that
    /// caused them and removed once delivered, so a crash neither loses nor forgets them.</summary>
    [Id(23)] public List<Integrations.NotificationDelivery> NotificationOutbox { get; set; } = [];

    /// <summary>Checks the runtime ran without an LLM call (watches), for the efficiency readout.</summary>
    [Id(24)] public long LlmCallsAvoided { get; set; }

    // ---- Safety (phase 5) ----
    [Id(25)] public Safety.WorkspaceSafetyPolicy SafetyPolicy { get; set; } = new();
    [Id(26)] public Dictionary<string, Safety.ApprovalRecord> Approvals { get; set; } = [];
    /// <summary>Tool call key → approval id, so a call re-checked after a restart finds its approval.</summary>
    [Id(27)] public Dictionary<string, string> ApprovalByCallKey { get; set; } = [];
    [Id(28)] public int NextApprovalNumber { get; set; } = 1;
    [Id(29)] public string TenantId { get; set; } = string.Empty;

    // Id 30 held standing agents' budget pause notices; don't reuse it.

    /// <summary>The day the "80% of today's budget used" warning was last posted.</summary>
    [Id(31)] public string? BudgetWarningDay { get; set; }
    [Id(32)] public string? TemplateId { get; set; }

    // ---- Pipeline ----
    [Id(33)] public Pipelines.PipelineDefinition? Pipeline { get; set; }
    /// <summary>Earlier versions, newest last (bounded), for undo.</summary>
    [Id(34)] public List<Pipelines.PipelineDefinition> PipelineHistory { get; set; } = [];
    /// <summary>Recent runs, newest last (bounded).</summary>
    [Id(35)] public List<WorkspaceRunSummary> Runs { get; set; } = [];
    [Id(36)] public List<string> ActiveRunIds { get; set; } = [];
    /// <summary>Runs waiting for a free slot (the pipeline's runs-at-once limit), oldest first.</summary>
    [Id(37)] public List<Pipelines.PipelineRunRequest> RunQueue { get; set; } = [];
    [Id(38)] public int NextRunNumber { get; set; } = 1;
    /// <summary>Runs refused because the queue was full.</summary>
    [Id(39)] public long DroppedRuns { get; set; }
}

/// <summary>One run in a workspace's history.</summary>
[GenerateSerializer]
public sealed record WorkspaceRunSummary
{
    [Id(0)] public required string RunId { get; init; }
    [Id(1)] public int Number { get; init; }
    [Id(2)] public Pipelines.PipelineRunStatus Status { get; init; }
    [Id(3)] public string Source { get; init; } = "manual";
    [Id(4)] public string? TriggerName { get; init; }
    [Id(5)] public string Input { get; init; } = string.Empty;
    [Id(6)] public string? StartedBy { get; init; }
    [Id(7)] public int PipelineVersion { get; init; }
    [Id(8)] public DateTimeOffset CreatedAt { get; init; }
    [Id(9)] public DateTimeOffset? CompletedAt { get; init; }
    [Id(10)] public string? Summary { get; init; }
}

/// <summary>The outcome of changing a workspace's pipeline.</summary>
[GenerateSerializer]
public sealed record PipelineChangeResult
{
    [Id(0)] public bool Success { get; init; }
    [Id(1)] public Pipelines.PipelineDefinition? Pipeline { get; init; }
    [Id(2)] public List<string> Errors { get; init; } = [];
    [Id(3)] public List<string> Changes { get; init; } = [];
    /// <summary>True when the pipeline changed since the version the change was based on.</summary>
    [Id(4)] public bool Conflict { get; init; }
}

/// <summary>The outcome of asking for a run.</summary>
[GenerateSerializer]
public sealed record RunStartResult
{
    [Id(0)] public bool Success { get; init; }
    [Id(1)] public string? RunId { get; init; }
    [Id(2)] public int Number { get; init; }
    /// <summary>"started", "queued", or why it was refused.</summary>
    [Id(3)] public string Message { get; init; } = string.Empty;
}

[GenerateSerializer]
public sealed record WorkspaceAgentView
{
    [Id(0)] public required string AgentId { get; init; }
    [Id(1)] public required string Role { get; init; }
    [Id(2)] public string Goal { get; init; } = string.Empty;
    [Id(3)] public string Status { get; init; } = string.Empty;
    [Id(4)] public string? ParentAgentId { get; init; }
    [Id(6)] public long TokensUsed { get; init; }
    [Id(7)] public decimal CostUsd { get; init; }
    [Id(8)] public string? CurrentTask { get; init; }
    [Id(9)] public long CachedInputTokens { get; init; }
    [Id(10)] public DateTimeOffset? CreatedAt { get; init; }
    [Id(11)] public DateTimeOffset? CompletedAt { get; init; }
    /// <summary>Why the runtime is holding the agent back (a budget or plan limit), if it is.</summary>
    [Id(12)] public string? PauseReason { get; init; }
    [Id(13)] public DateTimeOffset? PausedUntil { get; init; }
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
    [Id(7)] public List<ChatEntry> Conversation { get; init; } = [];
    [Id(8)] public List<TriggerView> Triggers { get; init; } = [];
    [Id(9)] public List<WorkspaceAgentView> Agents { get; init; } = [];
    [Id(10)] public int DailyTokenLimit { get; init; }
    [Id(11)] public decimal DailyCostLimitUsd { get; init; }
    [Id(12)] public long TokensToday { get; init; }
    [Id(13)] public decimal CostToday { get; init; }
    [Id(14)] public long TotalTokens { get; init; }
    [Id(15)] public decimal TotalCostUsd { get; init; }
    [Id(16)] public List<Integrations.ConnectionView> Connections { get; init; } = [];
    [Id(17)] public int PendingNotifications { get; init; }
    [Id(18)] public long LlmCallsAvoided { get; init; }
    [Id(19)] public Safety.WorkspaceSafetyPolicy SafetyPolicy { get; init; } = new();
    /// <summary>Pending first, then the most recent decided ones.</summary>
    [Id(20)] public List<Safety.ApprovalRecord> Approvals { get; init; } = [];
    [Id(21)] public string TenantId { get; init; } = string.Empty;
    [Id(22)] public string? TemplateId { get; init; }
    [Id(23)] public Pipelines.PipelineDefinition? Pipeline { get; init; }
    /// <summary>Recent runs, newest first.</summary>
    [Id(24)] public List<WorkspaceRunSummary> Runs { get; init; } = [];
    [Id(25)] public int QueuedRuns { get; init; }
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
