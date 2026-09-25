export type WorkspaceStatus = "Active" | "Paused" | "Archived";
export type TriggerKind = "Schedule" | "Webhook";
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

export interface TriggerView {
  trigger_id: string;
  kind: TriggerKind;
  name: string;
  target_agent_id: string;
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
}

export interface WorkspaceAgentView {
  agent_id: string;
  role: string;
  goal: string;
  status: string;
  parent_agent_id: string | null;
  standing: boolean;
  tokens_used: number;
  cost_usd: number;
  current_task: string | null;
}

export interface WorkspaceSnapshot {
  workspace_id: string;
  name: string;
  goal: string;
  status: WorkspaceStatus;
  created_at: string;
  updated_at: string;
  coordinator_agent_id: string;
  conversation: ChatEntry[];
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
}
