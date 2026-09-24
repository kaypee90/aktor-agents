export type WorldStatus = "Created" | "Running" | "Paused" | "Ended";
export type ResidentState = "Active" | "Dormant" | "Removed" | "Left";
export type ProposalOutcome = "Open" | "Passed" | "Rejected";

export interface LocationInfo {
  name: string;
  description: string;
}

export interface Resident {
  agent_id: string;
  name: string;
  role: string;
  persona: string;
  drives: string;
  location: string;
  energy: number;
  state: ResidentState;
  agent_status: string | null;
  parent_agent_id: string | null;
  joined_tick: number;
  notes: string[];
  last_plan: string | null;
  tokens_used: number;
  cost_usd: number;
}

export interface BoardPost {
  tick: number;
  author_id: string;
  author_name: string;
  text: string;
  timestamp: string;
}

export interface Proposal {
  proposal_id: string;
  target_id: string;
  proposer_id: string;
  reason: string;
  opened_tick: number;
  deadline_tick: number;
  eligible_voters: number;
  votes: Record<string, boolean>;
  outcome: ProposalOutcome;
}

export type ActivityKind =
  | "said" | "talked" | "moved" | "posted" | "gave" | "proposed" | "voted" | "removed"
  | "rejected" | "dormant" | "revived" | "joined" | "left" | "plan" | "note" | "world";

export interface WorldActivity {
  seq: number;
  tick: number;
  kind: ActivityKind;
  actor_id: string | null;
  target_id: string | null;
  location: string | null;
  from_location: string | null;
  text: string;
  timestamp: string;
  global: boolean;
  private: boolean;
  quote: string | null;
}

export interface WorldSnapshot {
  world_id: string;
  name: string;
  description: string;
  seed: string;
  status: WorldStatus;
  tick: number;
  max_ticks: number;
  tick_interval_seconds: number;
  created_at: string;
  started_at: string | null;
  ends_at: string | null;
  ended_at: string | null;
  end_reason: string | null;
  locations: LocationInfo[];
  residents: Resident[];
  board: BoardPost[];
  proposals: Proposal[];
  activity: WorldActivity[];
  totals: { tokens_used: number; cost_usd: number; active_residents: number; total_residents: number };
  energy_costs: string;
  max_energy: number;
}

export interface WorldListItem {
  world_id: string;
  name: string;
  seed: string;
  status: WorldStatus;
  tick: number;
  max_ticks: number;
  residents: number;
  cost_usd: number;
  created_at: string;
  ended_at: string | null;
}

export interface CreateWorldInput {
  seed: string;
  population: number;
  tick_interval_seconds: number;
  max_ticks: number;
  max_duration_minutes: number;
}

/** GET /api/worlds/settings: form defaults and limits for the configured LLM provider. */
export interface WorldFormSettings {
  provider: string;
  model: string;
  local_model: boolean;
  defaults: { population: number; tick_interval_seconds: number; max_ticks: number; max_duration_minutes: number };
  limits: { max_population: number; min_tick_interval_seconds: number; max_ticks: number; max_duration_minutes: number };
}
