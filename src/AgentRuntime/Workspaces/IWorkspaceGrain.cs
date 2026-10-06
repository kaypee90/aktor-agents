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
/// A reusable agent pipeline (docs/workspaces.md): a versioned graph of stages, each an agent,
/// configured in plain language or on the canvas; triggers (schedules, webhooks, watches) and
/// people start runs of it with an input; plus a conversation, connections, a safety policy and
/// one daily budget for all of it. State is durable; schedules are Orleans reminders.
///
/// Deadlock rule: agents call into this grain from their own turns (budget, policy, tools), so
/// every call it makes into an agent is interleaved or one-way. Runs tell it they finished
/// one-way, so its calls into a run (start, pause, cancel) can't deadlock with them.
/// </summary>
public interface IWorkspaceGrain : IGrainWithStringKey
{
    Task Create(WorkspaceCreationRequest request);

    /// <summary>A message from the user: the input of a new run, or guidance for an agent of a run
    /// in progress (<paramref name="toAgentId"/>). "approve A3" decides an approval.</summary>
    Task<ChatEntry> PostUserMessage(string text, string? toAgentId, string? clientMessageId, string? startedBy = null);

    // ---- Pipeline ----

    Task<Pipelines.PipelineDefinition?> GetPipeline();

    /// <summary>Earlier versions, newest first.</summary>
    Task<List<Pipelines.PipelineDefinition>> GetPipelineHistory();

    /// <summary>Replaces the pipeline (the canvas's save). Refused with a conflict if it changed
    /// since <paramref name="baseVersion"/>, or with the reasons if it can't run.</summary>
    Task<PipelineChangeResult> SetPipeline(Pipelines.PipelineDefinition pipeline, int baseVersion, string changedBy, string note);

    /// <summary>Applies edits (from the natural-language editor) to the pipeline at <paramref name="baseVersion"/>.</summary>
    Task<PipelineChangeResult> ApplyPipelineEdits(List<Pipelines.PipelineEditOp> ops, int baseVersion, string changedBy, string? note);

    /// <summary>Places stages on the canvas: replaces the layout of the current version in place
    /// (no new version, since runs don't depend on it). An empty layout lays it out automatically.</summary>
    Task<Pipelines.PipelineDefinition?> SetPipelineLayout(Dictionary<string, Pipelines.StagePosition> layout);

    /// <summary>Makes an earlier version current again (as a new version).</summary>
    Task<PipelineChangeResult> RestorePipelineVersion(int version, string changedBy);

    /// <summary>Starts a run with <paramref name="input"/>, or queues it if the pipeline's
    /// runs-at-once limit is reached or the workspace is paused.</summary>
    Task<RunStartResult> StartRun(string input, string startedBy);

    /// <summary>A run finished: recorded, reported in the chat, and the next queued run started.
    /// One-way, so a run never waits on its workspace.</summary>
    [OneWay]
    Task OnRunFinished(string runId, Pipelines.PipelineRunStatus status, string summary);


    /// <summary>Creates a schedule or webhook. The webhook's secret URL goes to the user's
    /// conversation (and to the API caller when <paramref name="revealSecret"/>), never to an agent.</summary>
    Task<WorkspaceActionResult> AddTrigger(TriggerSpec spec, string createdBy, string idempotencyKey, bool revealSecret);

    Task<WorkspaceActionResult> RemoveTrigger(string triggerId, string requestedBy);

    [AlwaysInterleave]
    Task<IReadOnlyList<TriggerView>> ListTriggers();

    Task<WebhookOutcome> DeliverWebhook(WebhookDelivery delivery);

    /// <summary>Delivers a payload to one of the workspace's webhooks on its owner's behalf (the API
    /// has already checked the caller may act on the workspace), e.g. to simulate an alert. Goes
    /// through the same path as a real delivery: rate limits, payload limits and the audit log.</summary>
    Task<WebhookOutcome> DeliverWebhookAsOwner(string triggerId, string body, string deliveryId);

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

    /// <summary>Delivers due notifications. One-way so the workspace never waits on an SMS provider.</summary>
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
    /// <summary>Approval requests waiting for a person, oldest first. Read-only, so it interleaves:
    /// the dashboard asks every workspace for these often.</summary>
    [AlwaysInterleave]
    Task<List<Safety.ApprovalRecord>> GetPendingApprovals();

    Task<WorkspaceActionResult> DecideApproval(string approvalIdOrCode, bool approve, string? reason, string decidedBy, string channel);

    [AlwaysInterleave]
    Task<Safety.WorkspaceSafetyPolicy> GetSafetyPolicy();

    Task UpdateSafetyPolicy(Safety.WorkspaceSafetyPolicy policy, string changedBy);

    /// <summary>Posts the once-a-day "budget reached" notice (called one-way from CheckBudget,
    /// which must not write state itself).</summary>
    [OneWay]
    Task PostBudgetNotice(string reason);

}
