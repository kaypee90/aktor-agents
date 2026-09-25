using Orleans.Concurrency;

namespace AgentRuntime.Workspaces;

[GenerateSerializer]
public sealed record WebhookDelivery
{
    [Id(0)] public required string TriggerId { get; init; }
    [Id(1)] public required string Token { get; init; }
    [Id(2)] public required string Body { get; init; }
    /// <summary>The sender's delivery id (e.g. X-Shopify-Webhook-Id), used to drop redeliveries.</summary>
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

    Task UpdateBudget(int? dailyTokenLimit, decimal? dailyCostLimitUsd);

    Task Pause();

    Task Resume();

    Task Archive();

    [AlwaysInterleave]
    Task<WorkspaceSnapshot?> GetSnapshot();

    /// <summary>Posts the once-a-day "budget reached" notice (called one-way from CheckBudget,
    /// which must not write state itself).</summary>
    [OneWay]
    Task PostBudgetNotice(string reason);
}
