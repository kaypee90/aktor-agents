/** A workspace's pipeline (docs/workspaces.md), as the API returns it. */

export type StageFailurePolicy = "FailRun" | "Continue";
export type PipelineRunStatus = "Queued" | "Running" | "Completed" | "Failed" | "Cancelled" | "TimedOut";
export type StageRunStatus = "Pending" | "Running" | "Completed" | "Failed" | "Skipped";

export interface PipelineStage {
  stage_id: string;
  name: string;
  role: string;
  instructions: string;
  /** Stages whose results this one needs. */
  inputs: string[];
  capabilities: string[];
  model_profile_id: string | null;
  max_helpers: number;
  may_message_stages: boolean;
  retries: number;
  on_failure: StageFailurePolicy;
  max_cost_usd: number | null;
}

/** Where a stage sits on the canvas. */
export type StagePosition = { x: number; y: number };

export interface PipelineDefinition {
  version: number;
  stages: PipelineStage[];
  updated_at: string;
  updated_by: string;
  note: string;
  max_run_minutes: number;
  max_concurrent_runs: number;
  /** How a finished run's result is posted, and so which channels forward it. */
  result_urgency: "info" | "warning" | "urgent";
  /** Stages placed by hand, by stage id; the rest are laid out automatically. */
  layout?: Record<string, StagePosition>;
}

/** Fields of a stage to set; anything left out stays as it is. */
export type StagePatch = Partial<Omit<PipelineStage, "stage_id">> & { stage_id?: string };

export type PipelineEditOp =
  | { op: "add_stage"; stage: StagePatch & { name: string }; after?: string; before?: string; position?: StagePosition }
  | { op: "update_stage"; stage_id: string; stage: StagePatch }
  | { op: "remove_stage"; stage_id: string }
  | { op: "connect"; from: string; to: string }
  | { op: "disconnect"; from: string; to: string };

export interface PipelineChangeResult {
  success: boolean;
  pipeline: PipelineDefinition | null;
  errors: string[];
  changes: string[];
  conflict: boolean;
}

export interface PipelineProposal {
  base_version: number;
  summary: string;
  ops: PipelineEditOp[];
  changes: string[];
  errors: string[];
  preview: PipelineDefinition | null;
  valid: boolean;
}

export interface WorkspaceRunSummary {
  run_id: string;
  number: number;
  status: PipelineRunStatus;
  source: string;
  trigger_name: string | null;
  input: string;
  started_by: string | null;
  pipeline_version: number;
  created_at: string;
  completed_at: string | null;
  summary: string | null;
}

export interface StageRunView {
  stage_id: string;
  name: string;
  status: StageRunStatus;
  agent_id: string | null;
  attempts: number;
  outcome: string | null;
  summary: string | null;
  artifacts: string[];
  error: string | null;
  started_at: string | null;
  completed_at: string | null;
  inputs: string[];
}

export interface PipelineRunView {
  run_id: string;
  workspace_id: string;
  number: number;
  status: PipelineRunStatus;
  paused: boolean;
  input: string;
  source: string;
  trigger_name: string | null;
  started_by: string | null;
  pipeline_version: number;
  created_at: string;
  started_at: string | null;
  completed_at: string | null;
  summary: string | null;
  stages: StageRunView[];
}

export interface RunStartResult {
  success: boolean;
  run_id: string | null;
  number: number;
  message: string;
}

/** Capability words that grant a stage tools (AgentToolCatalog), with what each gives. */
export const CAPABILITIES: { id: string; label: string }[] = [
  { id: "research", label: "Research (web search, knowledge)" },
  { id: "web-search", label: "Web search" },
  { id: "http", label: "HTTP requests" },
  { id: "filesystem", label: "Files (always on)" },
  { id: "postgresql", label: "Database queries" },
  { id: "database-design", label: "Database design" },
  { id: "shell", label: "Shell (sandboxed)" },
  { id: "docker", label: "Docker (sandboxed shell)" },
  { id: "security-scanner", label: "Security scanning" },
];

export const RUN_ACTIVE: PipelineRunStatus[] = ["Queued", "Running"];
