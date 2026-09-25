namespace AgentRuntime.Workspaces;

/// <summary>Limits for workspaces. The runtime enforces all of these, whatever an agent asks for.</summary>
public sealed class WorkspaceOptions
{
    public const string SectionName = "Workspaces";

    // ---- Workspace-wide daily budget (the single cost knob a user sets) ----
    public int DefaultDailyTokenLimit { get; set; } = 500_000;
    public decimal DefaultDailyCostLimitUsd { get; set; } = 2.00m;

    // ---- Per-agent budgets inside a workspace ----
    /// <summary>Standing agents get these per day, renewing daily.</summary>
    public int StandingAgentDailyTokens { get; set; } = 200_000;
    public int StandingAgentDailyToolCalls { get; set; } = 300;
    public decimal StandingAgentDailyCostUsd { get; set; } = 1.00m;
    /// <summary>One-shot workers get these for their whole life.</summary>
    public int WorkerTokens { get; set; } = 150_000;
    public int WorkerToolCalls { get; set; } = 150;
    public decimal WorkerCostUsd { get; set; } = 0.75m;
    public int WorkerMaxDurationSeconds { get; set; } = 1800;

    /// <summary>Recent transcript entries a standing agent's LLM calls see. Long-lived agents keep
    /// durable facts in memory (write_memory) instead of an ever-growing conversation.</summary>
    public int StandingContextWindow { get; set; } = 40;

    public int MaxAgentsPerWorkspace { get; set; } = 25;

    // ---- Triggers ----
    public int MaxTriggersPerWorkspace { get; set; } = 50;
    /// <summary>Shortest schedule interval. Short intervals on an LLM-backed agent burn tokens fast.</summary>
    public int MinScheduleIntervalSeconds { get; set; } = 60;
    /// <summary>Webhook deliveries beyond this per trigger per minute are dropped (and counted),
    /// so a chatty integration can't turn into an LLM bill.</summary>
    public int MaxWebhookEventsPerMinute { get; set; } = 30;
    /// <summary>Webhook bodies are truncated to this many characters before an agent sees them.</summary>
    public int MaxWebhookPayloadChars { get; set; } = 4000;
    public int MaxWebhookBodyBytes { get; set; } = 256 * 1024;

    // ---- Conversation ----
    public int MaxConversationEntries { get; set; } = 500;
    public int MaxMessageLength { get; set; } = 4000;
    public int MaxMessagesPerHour { get; set; } = 2000;
}
