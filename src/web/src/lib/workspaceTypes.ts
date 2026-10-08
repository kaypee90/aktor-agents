export type WorkspaceStatus = "Active" | "Paused" | "Archived";
export type TriggerKind = "Schedule" | "Webhook" | "Watch";
export type ChatAuthorKind = "User" | "Agent" | "System";

export interface ChatEntry {
  seq: number;
  at: string;
  author_kind: ChatAuthorKind;
  author_id: string;
  author_name: string;
  text: string;
  urgency: "info" | "warning" | "urgent";
}

/** A file an agent saved with filesystem_write; `versions` counts how many times it was written. */
export interface WorkspaceFile {
  artifact_id: string;
  path: string;
  file_name: string;
  size_bytes: number;
  versions: number;
  created_by_agent: string;
  updated_at: string;
}

export interface TriggerView {
  trigger_id: string;
  kind: TriggerKind;
  name: string;
  instruction: string;
  interval_seconds: number | null;
  cron: string | null;
  /** Only present in the response that created a webhook. */
  webhook_path: string | null;
  enabled: boolean;
  last_fired_at: string | null;
  fire_count: number;
  next_due_at: string | null;
  created_by: string;
  dropped_count: number;
  /** Watches: "shop__get: qty < 10 → notify". */
  watch_summary: string | null;
  checks: number;
  alerts: number;
  last_match_count: number;
  last_error: string | null;
}

export interface WorkspaceAgentView {
  agent_id: string;
  role: string;
  goal: string;
  status: string;
  parent_agent_id: string | null;
  tokens_used: number;
  cost_usd: number;
  current_task: string | null;
  cached_input_tokens: number;
  created_at: string | null;
  completed_at: string | null;
  /** Why the runtime is holding the agent back (a budget or plan limit), and until when if known. */
  pause_reason: string | null;
  paused_until: string | null;
}

export interface WorkspaceSnapshot {
  workspace_id: string;
  /** The template the workspace was made from (e.g. "incident-response"), if any. */
  template_id?: string | null;
  name: string;
  goal: string;
  status: WorkspaceStatus;
  created_at: string;
  updated_at: string;
  conversation: ChatEntry[];
  pipeline: import("./pipelineTypes").PipelineDefinition | null;
  /** Recent runs, newest first. */
  runs: import("./pipelineTypes").WorkspaceRunSummary[];
  queued_runs: number;
  triggers: TriggerView[];
  agents: WorkspaceAgentView[];
  daily_token_limit: number;
  daily_cost_limit_usd: number;
  tokens_today: number;
  cost_today: number;
  total_tokens: number;
  total_cost_usd: number;
  connections?: ConnectionView[];
  pending_notifications?: number;
  /** Checks watches ran without an LLM call. */
  llm_calls_avoided?: number;
  safety_policy?: SafetyPolicy;
  approvals?: ApprovalRecord[];
}

export interface WorkspaceListItem {
  workspace_id: string;
  name: string;
  goal: string;
  status: WorkspaceStatus;
  agents: number;
  triggers: number;
  total_tokens: number;
  total_cost_usd: number;
  created_at: string;
  updated_at: string;
}

// ---- Integrations (phase 3) ----

export type NotifyLevel = "Off" | "Urgent" | "Warning" | "All";
export type SideEffects = "ReadOnly" | "Idempotent" | "NonIdempotent";

export interface PluginSetting {
  key: string;
  label: string;
  description: string | null;
  secret: boolean;
  required: boolean;
  placeholder: string | null;
  default_value: string | null;
  options: string[] | null;
}

export interface PluginInfo {
  id: string;
  name: string;
  description: string;
  version: string;
  category: string;
  setup_help: string | null;
  provides_tools: boolean;
  supports_notifications: boolean;
  supports_inbound: boolean;
  settings: PluginSetting[];
}

export interface ConnectionView {
  connection_id: string;
  plugin_id: string;
  name: string;
  settings: Record<string, string>;
  secret_keys: string[];
  notify_level: NotifyLevel;
  tools: { name: string; description: string; side_effects: SideEffects; enabled: boolean }[];
  created_at: string;
  last_error: string | null;
  allowed_senders: string[];
  inbound_path: string | null;
  supports_tools: boolean;
  supports_notifications: boolean;
  supports_inbound: boolean;
  gateway?: McpGatewaySettings | null;
  /** Where the connection is served as an MCP server (workspace connections with tools). */
  gateway_path?: string | null;
}

export interface McpGatewaySettings {
  enabled: boolean;
  /** The connection's own tool names (without the "name__" prefix agents see). */
  tools: string[];
}

/** One operation of an HTTP API connection (docs/plugins.md, "Endpoints"). */
export interface ApiEndpointParam {
  name: string;
  in: "path" | "query";
  type: "string" | "integer" | "number" | "boolean";
  required?: boolean;
  description?: string | null;
}

export interface ApiEndpoint {
  name: string;
  method: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  path: string;
  description?: string | null;
  params: ApiEndpointParam[];
  body_schema?: unknown;
}

// ---- Safety (docs/safety.md) ----

export type AutonomyLevel = "Autonomous" | "SemiAutonomous" | "Supervised";
export type PolicyDecision = "Allow" | "Deny" | "RequireApproval";
export type SideEffectScope = "Any" | "Writes" | "Unsafe";
export type ApprovalStatus = "Pending" | "Approved" | "Rejected" | "Expired";

export interface ApprovalRule {
  id: string;
  name: string;
  tool_pattern: string;
  applies: SideEffectScope;
  decision: PolicyDecision;
}

/** Team-shape rules (docs/safety.md#team-shape), enforced at every spawn. */
export interface TeamPolicy {
  max_agents: number | null;
  goal_types?: { goal_type: string; keywords: string[]; max_agents: number }[];
  goal_type?: string | null;
  spawner_roles: string[];
  spawner_capabilities?: string[];
  max_fan_out_by_depth: number[];
  prevent_duplicate_roles: boolean;
  duplicate_goal_similarity?: number;
  count_finished_agents?: boolean;
}

export interface SafetyPolicy {
  autonomy: AutonomyLevel;
  rules: ApprovalRule[];
  approval_timeout_hours: number;
  team?: TeamPolicy | null;
}

/** The organization's safety policy: applies to every workspace and task, on top of their own. */
export interface OrganizationSafetyPolicy {
  /** The least oversight anywhere in the organization. */
  minimum_autonomy: AutonomyLevel;
  rules: ApprovalRule[];
  team?: TeamPolicy | null;
  updated_at?: string | null;
  updated_by?: string | null;
}

export interface ApprovalRecord {
  approval_id: string;
  code: string;
  agent_id: string;
  agent_name: string;
  tool_name: string;
  side_effects: SideEffects;
  arguments_json: string;
  agent_note: string | null;
  policy_reason: string;
  status: ApprovalStatus;
  requested_at: string;
  expires_at: string;
  decided_at: string | null;
  decided_by: string | null;
  decision_reason: string | null;
}

export interface AuditEntry {
  seq: number;
  at: string;
  actor_type: string;
  actor_id: string;
  actor_name: string;
  action: string;
  target: string;
  side_effects: string | null;
  outcome: string;
  summary: string;
  detail_json: string;
  hash: string;
}

export interface AuditVerification {
  valid: boolean;
  records: number;
  first_broken_seq: number | null;
  message: string;
}

export interface WorkspaceTemplate {
  id: string;
  name: string;
  /** Operations, Support, Research, Engineering, Sales, Marketing, Legal & finance, People… */
  category: string;
  description: string;
  goal: string;
  autonomy: string;
  /** What to try it with first (a webhook template's sample payload). */
  sample_input: string | null;
  stages: { stage_id: string; name: string; inputs: string[] }[];
  connections: { plugin_id: string; name: string; demo_only: boolean }[];
  webhooks: { name: string; sample_payload: string | null }[];
  schedules: { name: string; cron: string }[];
  /** One of the organization's own templates, made from a workspace (not built in). */
  custom: boolean;
  created_by?: string | null;
  created_at?: string;
}

/** A pipeline's shape in one line: stages that run together joined by ∥, levels by →. */
export function pipelineShape(stages: { stage_id: string; name: string; inputs: string[] }[]): string {
  const byId = new Map(stages.map((s) => [s.stage_id, s]));
  const depth = new Map<string, number>();
  const depthOf = (id: string, seen = new Set<string>()): number => {
    if (depth.has(id)) return depth.get(id)!;
    if (seen.has(id)) return 0;
    seen.add(id);
    const inputs = byId.get(id)?.inputs.filter((i) => byId.has(i)) ?? [];
    const d = inputs.length === 0 ? 0 : Math.max(...inputs.map((i) => depthOf(i, seen))) + 1;
    depth.set(id, d);
    return d;
  };
  const levels: string[][] = [];
  for (const s of stages) (levels[depthOf(s.stage_id)] ??= []).push(s.name);
  return levels.filter(Boolean).map((l) => l.join(" ∥ ")).join(" → ");
}
