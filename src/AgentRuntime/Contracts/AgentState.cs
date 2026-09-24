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
