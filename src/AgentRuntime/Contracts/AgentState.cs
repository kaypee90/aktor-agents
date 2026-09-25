namespace AgentRuntime.Contracts;

/// <summary>
/// The durable state owned exclusively by a single agent grain. Other agents never mutate this
/// directly; they communicate through <see cref="Messaging.AgentMessage"/> (CLAUDE.md section 6).
/// </summary>
[GenerateSerializer]
public sealed class AgentState
{
    [Id(0)] public string AgentId { get; set; } = string.Empty;
    [Id(1)] public string? ParentAgentId { get; set; }
    [Id(2)] public string RootAgentId { get; set; } = string.Empty;

    [Id(3)] public string Name { get; set; } = string.Empty;
    [Id(4)] public string Role { get; set; } = string.Empty;

    [Id(5)] public string Goal { get; set; } = string.Empty;
    [Id(6)] public AgentStatus Status { get; set; } = AgentStatus.Created;

    [Id(7)] public List<string> Capabilities { get; set; } = [];
    [Id(8)] public List<string> AllowedTools { get; set; } = [];
    [Id(9)] public ToolPermission GrantedPermissions { get; set; } = ToolPermission.None;

    [Id(10)] public DateTimeOffset CreatedAt { get; set; }
    [Id(11)] public DateTimeOffset? StartedAt { get; set; }
    [Id(12)] public DateTimeOffset? CompletedAt { get; set; }

    [Id(13)] public string? CurrentTask { get; set; }
    [Id(14)] public List<string> CompletedWork { get; set; } = [];
    [Id(15)] public List<string> PendingWork { get; set; } = [];
    [Id(16)] public List<string> BlockedWork { get; set; } = [];

    [Id(17)] public int Depth { get; set; }
    [Id(18)] public List<string> Children { get; set; } = [];

    [Id(19)] public ResourceBudget Budget { get; set; } = new();
    [Id(20)] public ResourceUsage Usage { get; set; } = new();

    [Id(21)] public DateTimeOffset? StartedExecutionAt { get; set; }

    [Id(22)] public Dictionary<string, string> Metadata { get; set; } = [];

    [Id(23)] public int RetryCount { get; set; }
    [Id(24)] public string? FailureReason { get; set; }

    [Id(25)] public int ConversationTurns { get; set; }

    [Id(26)] public List<AgentTranscriptEntry> Transcript { get; set; } = [];

    [Id(27)] public string TaskId { get; set; } = string.Empty;

    /// <summary>Set when this agent is a resident of a simulated world rather than a task worker.
    /// Residents run short per-tick turns and act through world tools instead of completing a goal.</summary>
    [Id(28)] public string? WorldId { get; set; }

    public bool IsResident => WorldId is not null;

    // ---- Durable execution journal (docs/durability.md) ----
    // The transcript is the step journal: an assistant entry records the LLM's decision and each
    // tool entry records a completed call. These fields hold what the transcript can't: where a
    // turn is, and which side-effecting calls started but haven't recorded a result.

    /// <summary>Highest mailbox sequence number already moved into the transcript.</summary>
    [Id(29)] public long LastConsumedMailSeq { get; set; }

    /// <summary>A reasoning turn is underway. If the agent is re-activated with this set (after a
    /// crash), the runtime resumes the turn from the journal instead of waiting for new input.</summary>
    [Id(30)] public bool TurnInProgress { get; set; }

    [Id(31)] public int TurnIteration { get; set; }
    [Id(32)] public bool TurnNudged { get; set; }
    [Id(33)] public List<string> TurnFingerprints { get; set; } = [];

    /// <summary>Non-idempotent tool calls recorded as started but not yet finished. One still here
    /// on recovery may or may not have taken effect, so it is never blindly re-run.</summary>
    [Id(34)] public List<string> InFlightToolCallIds { get; set; } = [];

    /// <summary>Messages the runtime owes other agents (e.g. "your child completed"), saved in the
    /// same write as the state change that caused them and removed once delivered.</summary>
    [Id(35)] public List<Messaging.AgentMessage> Outbox { get; set; } = [];

    /// <summary>A resume was requested; the next wake runs a turn even without new input.</summary>
    [Id(36)] public bool ResumeRequested { get; set; }

    [Id(37)] public bool Paused { get; set; }

    // ---- Workspaces ----

    /// <summary>The workspace this agent belongs to, if any (its task id is the same id).</summary>
    [Id(38)] public string? WorkspaceId { get; set; }

    /// <summary>A standing agent never "finishes": it waits for messages, schedules and webhooks
    /// indefinitely. It sees only a sliding window of recent history and its budget renews.</summary>
    [Id(39)] public bool Standing { get; set; }

    /// <summary>Recent transcript entries sent to the LLM; 0 means the whole conversation.</summary>
    [Id(40)] public int ContextWindow { get; set; }

    /// <summary>Start of the current budget period, for budgets that renew (ResourceBudget.PeriodHours).</summary>
    [Id(41)] public DateTimeOffset? BudgetPeriodStartedAt { get; set; }

    public bool InWorkspace => WorkspaceId is not null;

    /// <summary>Summary of older history that was compacted out of the transcript (see
    /// ContextCompactor). Shown to the LLM so long-lived agents keep continuity cheaply.</summary>
    [Id(42)] public string? ContextSummary { get; set; }

    /// <summary>Routine event handling that can use the fast model tier.</summary>
    public bool UsesFastTier => IsResident || (Standing && Role != "Coordinator");

    public bool IsTerminal => Status is AgentStatus.Completed or AgentStatus.Failed or AgentStatus.Terminated or AgentStatus.TimedOut;

    public static readonly IReadOnlyDictionary<AgentStatus, AgentStatus[]> ValidTransitions =
        new Dictionary<AgentStatus, AgentStatus[]>
        {
            [AgentStatus.Created] = [AgentStatus.Initializing, AgentStatus.Terminated],
            [AgentStatus.Initializing] = [AgentStatus.Idle, AgentStatus.Failed, AgentStatus.Terminated],
            [AgentStatus.Idle] = [AgentStatus.Thinking, AgentStatus.Waiting, AgentStatus.Completed, AgentStatus.Terminated, AgentStatus.TimedOut],
            [AgentStatus.Thinking] = [AgentStatus.Executing, AgentStatus.Spawning, AgentStatus.Waiting, AgentStatus.Idle, AgentStatus.Completed, AgentStatus.Failed, AgentStatus.TimedOut, AgentStatus.Terminated],
            [AgentStatus.Executing] = [AgentStatus.Waiting, AgentStatus.Idle, AgentStatus.Thinking, AgentStatus.Failed, AgentStatus.Completed, AgentStatus.TimedOut, AgentStatus.Terminated],
            [AgentStatus.Spawning] = [AgentStatus.Thinking, AgentStatus.Idle, AgentStatus.Failed, AgentStatus.Terminated],
            [AgentStatus.Waiting] = [AgentStatus.Thinking, AgentStatus.Idle, AgentStatus.Failed, AgentStatus.TimedOut, AgentStatus.Terminated],
            [AgentStatus.Failed] = [AgentStatus.Executing, AgentStatus.Terminated, AgentStatus.Thinking],
            [AgentStatus.Completed] = [],
            [AgentStatus.Terminated] = [],
            [AgentStatus.TimedOut] = [AgentStatus.Terminated]
        };

    public bool CanTransitionTo(AgentStatus next) =>
        Status == next || (ValidTransitions.TryGetValue(Status, out var allowed) && allowed.Contains(next));

    public void TransitionTo(AgentStatus next)
    {
        if (!CanTransitionTo(next))
        {
            throw new InvalidOperationException(
                $"Agent '{AgentId}' cannot transition from {Status} to {next}.");
        }

        Status = next;
    }

    /// <summary>
    /// Bypasses the normal transition graph. Reserved for operator-triggered overrides (Stop) and
    /// terminal failure paths where the state machine would otherwise deadlock — never called in
    /// response to an LLM decision (CLAUDE.md section 50).
    /// </summary>
    public void ForceStatus(AgentStatus next) => Status = next;
}
