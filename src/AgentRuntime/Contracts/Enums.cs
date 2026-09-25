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

/// <summary>
/// Runtime-enforced permissions. An LLM cannot grant these to itself (CLAUDE.md section 18/44).
/// </summary>
[Flags]
public enum ToolPermission
{
    None = 0,
    ReadFilesystem = 1 << 0,
    WriteFilesystem = 1 << 1,
    ExecuteShell = 1 << 2,
    NetworkAccess = 1 << 3,
    GitRead = 1 << 4,
    GitWrite = 1 << 5,
    DatabaseRead = 1 << 6,
    DatabaseWrite = 1 << 7,
    SpawnAgents = 1 << 8,
    SendMessages = 1 << 9,

    /// <summary>Acting inside a simulated world (move, speak, vote, ...). Only residents get this.</summary>
    WorldActions = 1 << 10,

    /// <summary>Acting inside a workspace: notifying the user, creating schedules and webhooks.</summary>
    WorkspaceActions = 1 << 11
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
