using Orleans.Concurrency;

namespace AgentRuntime.Workspaces;

[GenerateSerializer]
public sealed record WebhookDelivery
{
    [Id(0)] public required string TriggerId { get; init; }
    [Id(1)] public required string Token { get; init; }
    [Id(2)] public required string Body { get; init; }
    /// <summary>The sender's delivery id (an Idempotency-Key or provider webhook-id header), used to drop redeliveries.</summary>
    [Id(3)] public string? DeliveryId { get; init; }
    [Id(4)] public string? ContentType { get; init; }
}

public enum WebhookOutcome
{
    Accepted,
    Duplicate,
    NotFound,
    Unauthorized,
    RateLimited,
    Inactive
}

/// <summary>
/// A long-running environment where a user's agents live (docs/workspaces.md): a standing
/// coordinator that takes the user's commands, the standing and one-shot agents it spawns,
/// schedules and webhooks that wake them, a conversation with the user, and one daily budget for
/// all of it. State is durable; schedules are Orleans reminders, so they survive crashes.
///
/// Deadlock rule (as for worlds): agents call into this grain from their own turns, so every call
/// it makes into an agent is interleaved (mailbox enqueues, snapshots) or one-way.
/// </summary>
public interface IWorkspaceGrain : IGrainWithStringKey
{
    Task Create(WorkspaceCreationRequest request);

    /// <summary>A command from the user, delivered to the coordinator (or a named agent).</summary>
    Task<ChatEntry> PostUserMessage(string text, string? toAgentId, string? clientMessageId);

    /// <summary>notify_user: an agent reporting to the user.</summary>
    Task<WorkspaceActionResult> Notify(string agentId, string text, string urgency, string idempotencyKey);

    /// <summary>Creates a schedule or webhook. The webhook's secret URL goes to the user's
    /// conversation (and to the API caller when <paramref name="revealSecret"/>), never to an agent.</summary>
    Task<WorkspaceActionResult> AddTrigger(TriggerSpec spec, string createdBy, string idempotencyKey, bool revealSecret);

    Task<WorkspaceActionResult> RemoveTrigger(string triggerId, string requestedBy);

    [AlwaysInterleave]
    Task<IReadOnlyList<TriggerView>> ListTriggers();

    Task<WebhookOutcome> DeliverWebhook(WebhookDelivery delivery);

    /// <summary>Asked by an agent before each LLM call. Interleaved and read-only, so it's cheap
    /// and can't deadlock against the asking agent.</summary>
    [AlwaysInterleave]
    Task<BudgetDecision> CheckBudget();

    [OneWay]
    Task RecordUsage(string agentId, long tokens, decimal costUsd);

    [AlwaysInterleave]
    Task<WorkspacePolicy> GetPolicy();

    /// <summary>The owning organization, or null if the workspace doesn't exist. The API checks it
    /// on every request, so another tenant's workspace looks like one that doesn't exist.</summary>
    [AlwaysInterleave]
    Task<string?> GetTenantId();

    Task UpdateBudget(int? dailyTokenLimit, decimal? dailyCostLimitUsd);

    Task Pause();

    Task Resume();

    Task Archive();

    [AlwaysInterleave]
    Task<WorkspaceSnapshot?> GetSnapshot();

    // ---- Integrations ----

    /// <summary>Installs a plugin as a connection: secrets go to the vault, the plugin validates
    /// the connection and lists its tools. The only time secret values pass through the grain,
    /// and they're never stored in its state.</summary>
    Task<Integrations.ConnectionResult> AddConnection(Integrations.ConnectionRequest request);

    Task<Integrations.ConnectionResult> UpdateConnection(string connectionId, Integrations.ConnectionUpdate update);

    Task<Integrations.ConnectionResult> RefreshConnectionTools(string connectionId);

    Task RemoveConnection(string connectionId);

    /// <summary>The owner's view, including inbound paths (which contain a secret).</summary>
    [AlwaysInterleave]
    Task<IReadOnlyList<Integrations.ConnectionView>> ListConnections();

    /// <summary>Enabled connection tools, for an agent's LLM call. Empty unless the workspace is active.</summary>
    [AlwaysInterleave]
    Task<IReadOnlyList<Integrations.ConnectionToolDescriptor>> GetConnectionTools();

    [AlwaysInterleave]
    Task<Integrations.ConnectionToolTarget?> ResolveConnectionTool(string exposedName);

    /// <summary>A message arriving through a connection (an SMS reply, a Telegram message).</summary>
    Task<Integrations.InboundResponseDto> HandleInbound(string connectionId, string token, Integrations.InboundRequestDto request);

    /// <summary>Delivers due notifications. One-way so notify_user never waits on an SMS provider.</summary>
    [OneWay]
    Task ProcessNotificationOutbox();

    // ---- Safety ----

    /// <summary>
    /// Asked by an agent before every tool call. Applies the workspace's policy; for a call that
    /// needs approval it creates (once, keyed by the call's idempotency key) an approval request
    /// and asks the user. Asking again for the same call returns its current state, which is how
    /// a parked agent learns it was approved — even after a restart.
    /// </summary>
    Task<Safety.ToolCallPermission> CheckToolCall(Safety.ToolCallPermissionRequest request);

    /// <summary>A human's decision, by approval id or its short code ("A7").</summary>
    Task<WorkspaceActionResult> DecideApproval(string approvalIdOrCode, bool approve, string? reason, string decidedBy, string channel);

    [AlwaysInterleave]
    Task<Safety.WorkspaceSafetyPolicy> GetSafetyPolicy();

    Task UpdateSafetyPolicy(Safety.WorkspaceSafetyPolicy policy, string changedBy);

    /// <summary>Posts the once-a-day "budget reached" notice (called one-way from CheckBudget,
    /// which must not write state itself).</summary>
    [OneWay]
    Task PostBudgetNotice(string reason);
}
