export type AgentStatus =
  | "Created"
  | "Initializing"
  | "Idle"
  | "Thinking"
  | "Executing"
  | "Waiting"
  | "Spawning"
  | "Completed"
  | "Failed"
  | "Terminated"
  | "TimedOut";

export interface ResourceBudget {
  max_tokens: number;
  max_duration_seconds: number;
  max_children: number;
  max_tool_calls: number;
  max_cost_usd: number;
}

export interface ResourceUsage {
  tokens_used: number;
  tool_calls_used: number;
  children_spawned: number;
  cost_usd: number;
  elapsed_seconds: number;
}

export interface AgentListItem {
  agent_id: string;
  role: string;
  goal: string;
  status: AgentStatus;
  capabilities: string[];
  parent_agent_id: string | null;
  root_agent_id: string;
  depth: number;
}

export interface AgentSnapshot {
  agent_id: string;
  parent_agent_id: string | null;
  root_agent_id: string;
  name: string;
  role: string;
  goal: string;
  status: AgentStatus;
  capabilities: string[];
  allowed_tools: string[];
  granted_permissions: string;
  created_at: string;
  started_at: string | null;
  completed_at: string | null;
  current_task: string | null;
  children: string[];
  budget: ResourceBudget;
  usage: ResourceUsage;
  depth: number;
  failure_reason: string | null;
  task_id: string;
}

export type RuntimeEventType =
  | "AgentCreated"
  | "AgentStarted"
  | "AgentThinking"
  | "AgentToolCalled"
  | "AgentToolCompleted"
  | "AgentMessageSent"
  | "AgentMessageReceived"
  | "AgentSpawnRequested"
  | "AgentSpawned"
  | "AgentCompleted"
  | "AgentFailed"
  | "AgentRestarted"
  | "AgentTerminated"
  | "AgentStatusChanged"
  | "TaskCreated"
  | "TaskCompleted"
  | "ArtifactCreated"
  | "EnvironmentChanged"
  | "WorldCreated"
  | "WorldTick"
  | "WorldActivity"
  | "WorldEnded"
  | "WorkspaceCreated"
  | "WorkspaceMessage"
  | "TriggerFired"
  | "WorkspaceChanged";

export interface RuntimeEvent {
  event_id: string;
  type: RuntimeEventType;
  timestamp: string;
  agent_id: string | null;
  parent_agent_id: string | null;
  target_agent_id: string | null;
  task_id: string | null;
  correlation_id: string | null;
  summary: string;
  data: Record<string, string>;
}

/** Shape of a persisted event row from GET /api/events or /api/tasks/{id}/events (historical). */
export interface EventRecordDto {
  id: number;
  event_id: string;
  type: RuntimeEventType;
  timestamp: string;
  agent_id: string | null;
  parent_agent_id: string | null;
  target_agent_id: string | null;
  task_id: string | null;
  correlation_id: string | null;
  summary: string;
  data_json: string;
}

/** Aggregated final result (CLAUDE.md section 52) from GET /api/tasks/{id}/result. */
export interface TaskResult {
  status: string;
  summary: string;
  findings: string[];
  artifacts: string[];
  participating_agents: number;
  unresolved_items: string[];
  metrics: Record<string, string>;
}

export interface TaskResultResponse {
  ready: boolean;
  status?: string;
  result?: TaskResult;
}

export interface ArtifactListItem {
  artifact_id: string;
  type: string;
  file_name: string;
  created_by_agent: string;
  created_at: string;
}

export interface TaskSummary {
  task_id: string;
  goal: string;
  status: string;
  root_agent_id: string | null;
  created_at: string;
  completed_at: string | null;
  result_summary: string | null;
}

export interface MessageRecord {
  message_id: string;
  from_agent_id: string;
  to_agent_id: string;
  conversation_id: string;
  correlation_id: string | null;
  message_type: string;
  priority: string;
  timestamp: string;
  payload: string;
  task_id: string;
}

export interface ToolCallRecord {
  id: number;
  agent_id: string;
  task_id: string;
  tool_name: string;
  arguments_json: string;
  result_json: string | null;
  success: boolean;
  timestamp: string;
}
