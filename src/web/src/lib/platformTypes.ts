export type Role = "Viewer" | "Member" | "Admin" | "Owner";

export const ROLES: Role[] = ["Viewer", "Member", "Admin", "Owner"];

export const ROLE_HELP: Record<Role, string> = {
  Viewer: "sees everything, changes nothing",
  Member: "creates workspaces, instructs agents, decides approvals",
  Admin: "also manages connections, safety policy, members and API keys",
  Owner: "also manages billing and owners",
};

export function atLeast(role: string | undefined, minimum: Role) {
  return ROLES.indexOf((role ?? "Viewer") as Role) >= ROLES.indexOf(minimum);
}

export interface Me {
  authenticated: boolean;
  server: { auth_mode: "accounts" | "disabled"; signup_allowed: boolean; billing_enabled: boolean };
  user?: { user_id: string | null; email: string | null; platform_admin: boolean };
  via?: "session" | "api_key" | "disabled";
  tenant_id?: string;
  role?: Role;
  organizations?: { tenant_id: string; name: string; role: Role }[];
}

export interface Plan {
  id: string;
  name: string;
  description: string;
  monthly_token_limit: number;
  monthly_cost_limit_usd: number;
  max_workspaces: number;
  max_active_agents: number;
  max_members: number;
  price_monthly_usd: number;
  purchasable?: boolean;
}

export interface Organization {
  tenant_id: string;
  name: string;
  created_at: string | null;
  your_role: Role;
  plan: Plan;
  members: number;
}

export interface Member {
  user_id: string;
  email: string;
  name: string;
  role: Role;
  joined_at: string;
}

export interface Invitation {
  invitation_id: string;
  email: string;
  role: Role;
  created_at?: string;
  expires_at: string;
  accepted_at?: string | null;
  revoked_at?: string | null;
}

export interface ApiKey {
  key_id: string;
  name: string;
  display: string;
  role: Role;
  created_at: string;
  last_used_at: string | null;
  expires_at: string | null;
  revoked_at: string | null;
}

export interface UsagePeriod {
  period: string;
  tokens: number;
  cost_usd: number;
  llm_calls: number;
  tool_calls: number;
  agents_created: number;
}

export interface BillingView {
  billing_enabled: boolean;
  plan: Plan;
  subscription: { status: string; current_period_end: string | null; has_billing_account: boolean };
  usage: UsagePeriod;
  history: UsagePeriod[];
  quota: { allowed: boolean; reason: string | null };
  paused_agents: number;
  plans: Plan[];
}
