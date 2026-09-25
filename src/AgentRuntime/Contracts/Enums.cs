namespace AgentRuntime.Contracts;

/// <summary>
/// Explicit lifecycle states for an agent. Transitions are enforced by <see cref="AgentState"/>;
/// arbitrary status mutation is not permitted (see CLAUDE.md section 6).
/// </summary>
public enum AgentStatus
{
    Created,
    Initializing,
    Idle,
    Thinking,
    Executing,
    Waiting,
    Spawning,
    Completed,
    Failed,
    Terminated,
    TimedOut
}

public enum MessageType
{
    TaskRequest,
    TaskResponse,
    InformationRequest,
    InformationResponse,
    StatusUpdate,
    DelegationRequest,
    DelegationResponse,
    SpawnNotification,
    CompletionNotification,
    FailureNotification,
    Cancellation,

    /// <summary>Free-form conversation between simulation residents (talk_to).</summary>
    Conversation
}

public enum MessagePriority
{
    Low,
    Normal,
    High,
    Urgent
}

public enum AutonomyLevel
{
    Supervised,
    SemiAutonomous,
    Autonomous
}

public enum SupervisionAction
{
    Retry,
    Restart,
    Replace,
    Terminate,
    Escalate
}

public enum ArtifactType
{
    Document,
    Code,
    Report,
    Image,
    Data,
    Repository
}

public enum MemoryKind
{
    Working,
    Episodic,
    Shared
}

public enum RuntimeEventType
{
    AgentCreated,
    AgentStarted,
    AgentThinking,
    AgentToolCalled,
    AgentToolCompleted,
    AgentMessageSent,
    AgentMessageReceived,
    AgentSpawnRequested,
    AgentSpawned,
    AgentCompleted,
    AgentFailed,
    AgentRestarted,
    AgentTerminated,
    AgentStatusChanged,
    TaskCreated,
    TaskCompleted,
    ArtifactCreated,
    EnvironmentChanged,

    // Simulation (living world) events. The world grain publishes these with TaskId = world id,
    // so the existing per-task SSE filter and event persistence work unchanged.
    WorldCreated,
    WorldTick,
    WorldActivity,
    WorldEnded,

    // Workspaces (long-running environments). Published with TaskId = workspace id.
    WorkspaceCreated,
    WorkspaceMessage,
    TriggerFired,
    WorkspaceChanged
}
