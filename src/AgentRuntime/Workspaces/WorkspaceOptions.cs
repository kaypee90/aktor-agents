namespace AgentRuntime.Workspaces;

/// <summary>Limits for workspaces. The runtime enforces all of these, whatever an agent asks for.</summary>
public sealed class WorkspaceOptions
{
    public const string SectionName = "Workspaces";

    // ---- Workspace-wide daily budget (the single cost knob a user sets) ----
    public int DefaultDailyTokenLimit { get; set; } = 500_000;
    public decimal DefaultDailyCostLimitUsd { get; set; } = 2.00m;

    // ---- Per-agent budgets inside a workspace ----
    /// <summary>Each pipeline stage's agent (unless the stage sets its own cost) and each helper gets
    /// these for one run.</summary>
    public int WorkerTokens { get; set; } = 150_000;
    public int WorkerToolCalls { get; set; } = 150;
    public decimal WorkerCostUsd { get; set; } = 0.75m;
    public int WorkerMaxDurationSeconds { get; set; } = 1800;

    public int MaxAgentsPerWorkspace { get; set; } = 25;

    /// <summary>Helpers one stage's agent may start in a run, at most (a stage's own limit is
    /// usually lower). Bounds the cost of a model misjudging small work as big.</summary>
    public int MaxSpawnsPerRequest { get; set; } = 3;

    // ---- Triggers ----
    public int MaxTriggersPerWorkspace { get; set; } = 50;
    /// <summary>Shortest schedule interval. Every scheduled run costs model calls.</summary>
    public int MinScheduleIntervalSeconds { get; set; } = 60;
    /// <summary>Webhook deliveries beyond this per trigger per minute are dropped (and counted),
    /// so a chatty integration can't turn into an LLM bill.</summary>
    public int MaxWebhookEventsPerMinute { get; set; } = 30;
    /// <summary>Webhook bodies are truncated to this many characters in a run's input.</summary>
    public int MaxWebhookPayloadChars { get; set; } = 4000;
    public int MaxWebhookBodyBytes { get; set; } = 256 * 1024;

    // ---- Conversation ----
    public int MaxConversationEntries { get; set; } = 500;
    public int MaxMessageLength { get; set; } = 4000;
    public int MaxMessagesPerHour { get; set; } = 2000;
}
