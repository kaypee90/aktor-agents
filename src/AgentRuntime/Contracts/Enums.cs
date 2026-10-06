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

/// <summary>An agent finishing up because its budget is nearly spent (see Resources/BudgetGuard).</summary>
public enum WrapUpStage
{
    None,
    /// <summary>Told to start finishing.</summary>
    Warned,
    /// <summary>Its next LLM call is the last one, and only complete_task may run.</summary>
    FinalStep,
    /// <summary>The last call was made; if it didn't report, the runtime reports for it.</summary>
    FinalStepTaken
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
    WorkspaceChanged,

    // Models (docs/llm-settings.md, docs/analytics.md).
    /// <summary>One model call finished: model, tokens, cost and duration, for analytics.</summary>
    LlmCallCompleted,
    /// <summary>A running task was switched to another model.</summary>
    TaskModelChanged,

    // Task chat (follow-up instructions on a task).
    /// <summary>The task's owner sent a follow-up instruction (Data["text"]).</summary>
    TaskFollowUp,
    /// <summary>A finished task's root agent took up a follow-up: the task is running again.</summary>
    TaskReopened,

    // Pipelines (docs/workspaces.md). Stage events are published with TaskId = run id; changes
    // to the pipeline and run progress also with TaskId = workspace id, for the workspace screen.
    /// <summary>A new version of a workspace's pipeline was applied (Data["version"], ["note"]).</summary>
    PipelineChanged,
    /// <summary>A run was queued, started, or finished (Data["run_id"], ["status"]).</summary>
    PipelineRunUpdated,
    /// <summary>A stage's agent started (Data["stage_id"], ["attempt"]).</summary>
    PipelineStageStarted,
    /// <summary>A stage finished, failed, or was skipped (Data["stage_id"], ["status"]).</summary>
    PipelineStageFinished
}
