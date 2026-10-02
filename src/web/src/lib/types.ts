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
  /** A model given to this agent when it was spawned; absent when it follows the task's model. */
  model_profile_id?: string | null;
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
  | "WorkspaceChanged"
  | "LlmCallCompleted"
  | "TaskModelChanged";

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
  correlation_id?: string | null;
  replay_of_task_id?: string | null;
  replay_mode?: string | null;
  /** The model the task runs on now (docs/llm-settings.md). */
  model?: { profile_id: string; name: string; provider: string; model: string; chosen: boolean };
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

// ---- Cost and team preview (roadmap P2) ----

export interface PlannedTeamMember {
  role: string;
  purpose: string;
  depth: number;
  parent_role: string | null;
}

export interface TaskEstimate {
  tokens_low: number;
  tokens_expected: number;
  tokens_high: number;
  cost_usd_low: number;
  cost_usd_expected: number;
  cost_usd_high: number;
  duration_seconds_low: number;
  duration_seconds_expected: number;
  duration_seconds_high: number;
  team_size: number;
}

export interface TaskPreview {
  preview_id: string;
  goal: string;
  goal_type: string | null;
  team: PlannedTeamMember[];
  team_size: number;
  max_depth: number;
  rationale: string | null;
  estimate: TaskEstimate;
  budget: ResourceBudget;
  capped_by_budget: boolean;
  requires_confirmation: boolean;
  confirm_above_usd: number;
  calibration: { samples: number; tokens_per_agent: number; team_size_factor: number; source: string };
  planning: { tokens: number; cost_usd: number };
}

/** One agent's spend, and its whole branch's (it plus everything below it), against its budget. */
export interface AgentSpend {
  agent_id: string;
  parent_agent_id: string | null;
  role: string;
  tokens_used: number;
  cost_usd: number;
  budget_max_tokens: number;
  budget_max_cost_usd: number;
  branch_tokens: number;
  branch_cost_usd: number;
}

// ---- Step journal, replay and diff (roadmap P6) ----

export interface JournalStep {
  seq: number;
  agent_id: string;
  agent_path: string;
  role: string | null;
  kind: "llm" | "tool";
  key: string;
  step: number;
  tool_name: string | null;
  inputs_received: number;
  at: string;
  summary: string;
  payload: unknown;
}

export type StepDiffStatus = "Same" | "Different" | "OnlyInA" | "OnlyInB";

export interface StepDiff {
  agent_path: string;
  kind: string;
  key: string;
  step: number;
  tool_name: string | null;
  status: StepDiffStatus;
  seq_a: number | null;
  seq_b: number | null;
  summary_a: string | null;
  summary_b: string | null;
}

export interface RunDiff {
  a: string;
  b: string;
  identical: boolean;
  same: number;
  different: number;
  only_in_a: number;
  only_in_b: number;
  agents_only_in_a: string[];
  agents_only_in_b: string[];
  steps: StepDiff[];
}
