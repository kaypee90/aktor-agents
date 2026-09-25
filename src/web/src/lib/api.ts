import type {
  AgentListItem,
  AgentSnapshot,
  ArtifactListItem,
  EventRecordDto,
  MessageRecord,
  ResourceBudget,
  TaskResultResponse,
  TaskSummary,
  ToolCallRecord,
} from "./types";

export const API_BASE =
  process.env.NEXT_PUBLIC_API_BASE_URL ?? "http://localhost:5080";

/** Fired when the API says the session is gone; the auth gate sends the user to sign in. */
export const UNAUTHORIZED_EVENT = "aktor:unauthorized";

async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    ...init,
    headers: { "Content-Type": "application/json", ...init?.headers },
    cache: "no-store",
    // The session is an HttpOnly cookie on the API's origin.
    credentials: "include",
  });

  if (res.status === 401 && typeof window !== "undefined" && !path.startsWith("/api/auth/")) {
    window.dispatchEvent(new Event(UNAUTHORIZED_EVENT));
  }

  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new Error(`${init?.method ?? "GET"} ${path} failed: ${res.status} ${text}`);
  }

  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export function createTask(goal: string, budget?: Partial<ResourceBudget>) {
  return apiFetch<{ task_id: string; root_agent_id: string }>("/api/tasks", {
    method: "POST",
    body: JSON.stringify({ goal, budget }),
  });
}

export function listTasks() {
  return apiFetch<TaskSummary[]>("/api/tasks");
}

export function getTask(taskId: string) {
  return apiFetch<TaskSummary>(`/api/tasks/${taskId}`);
}

export function pauseTask(taskId: string) {
  return apiFetch<void>(`/api/tasks/${taskId}/pause`, { method: "POST" });
}

export function resumeTask(taskId: string) {
  return apiFetch<void>(`/api/tasks/${taskId}/resume`, { method: "POST" });
}

export function cancelTask(taskId: string) {
  return apiFetch<void>(`/api/tasks/${taskId}/cancel`, { method: "POST" });
}

export function getTaskEvents(taskId: string, limit = 200) {
  return apiFetch<EventRecordDto[]>(`/api/tasks/${taskId}/events?limit=${limit}`);
}

/** The aggregated final result — the root's summary plus every agent's findings and any
 * artifacts produced. `ready: false` until the root agent has completed. */
export function getTaskResult(taskId: string) {
  return apiFetch<TaskResultResponse>(`/api/tasks/${taskId}/result`);
}

export function getTaskArtifacts(taskId: string) {
  return apiFetch<ArtifactListItem[]>(`/api/tasks/${taskId}/artifacts`);
}

export function artifactDownloadUrl(taskId: string, artifactId: string) {
  return `${API_BASE}/api/tasks/${taskId}/artifacts/${artifactId}/content`;
}

/** One zip of every artifact file the task produced, keeping their folder layout. */
export function artifactsZipUrl(taskId: string) {
  return `${API_BASE}/api/tasks/${taskId}/artifacts.zip`;
}

/** Wipes all tasks/agents/messages/events/artifacts and the live agent registry so the next
 * submitted goal starts from a clean slate. */
export function resetAll() {
  return apiFetch<void>("/api/admin/reset", { method: "POST" });
}

export function listAgents() {
  return apiFetch<AgentListItem[]>("/api/agents");
}

export function getAgent(agentId: string) {
  return apiFetch<AgentSnapshot>(`/api/agents/${agentId}`);
}

export function getAgentChildren(agentId: string) {
  return apiFetch<string[]>(`/api/agents/${agentId}/children`);
}

export function getAgentMessages(agentId: string) {
  return apiFetch<MessageRecord[]>(`/api/agents/${agentId}/messages`);
}

export function getAgentToolCalls(agentId: string) {
  return apiFetch<ToolCallRecord[]>(`/api/agents/${agentId}/tool-calls`);
}

export function pauseAgent(agentId: string) {
  return apiFetch<void>(`/api/agents/${agentId}/pause`, { method: "POST" });
}

export function resumeAgent(agentId: string) {
  return apiFetch<void>(`/api/agents/${agentId}/resume`, { method: "POST" });
}

export function terminateAgent(agentId: string) {
  return apiFetch<void>(`/api/agents/${agentId}/terminate`, { method: "POST" });
}

/** Opens the live SSE event feed. Caller owns the returned EventSource's lifecycle. */
export function subscribeToEvents(
  onEvent: (evt: import("./types").RuntimeEvent) => void,
  taskId?: string,
): EventSource {
  const url = new URL(`${API_BASE}/ws/events`);
  if (taskId) url.searchParams.set("taskId", taskId);

  const source = new EventSource(url.toString(), { withCredentials: true });
  source.onmessage = (e) => {
    try {
      onEvent(JSON.parse(e.data));
    } catch {
      // ignore malformed frames
    }
  };
  return source;
}

// ---- Simulation (living worlds) ----

export function createWorld(input: import("./worldTypes").CreateWorldInput) {
  return apiFetch<{ world_id: string }>("/api/worlds", { method: "POST", body: JSON.stringify(input) });
}

export function getWorldSettings() {
  return apiFetch<import("./worldTypes").WorldFormSettings>("/api/worlds/settings");
}

export function listWorlds() {
  return apiFetch<import("./worldTypes").WorldListItem[]>("/api/worlds");
}

export function getWorld(worldId: string) {
  return apiFetch<import("./worldTypes").WorldSnapshot>(`/api/worlds/${worldId}`);
}

export function pauseWorld(worldId: string) {
  return apiFetch<void>(`/api/worlds/${worldId}/pause`, { method: "POST" });
}

export function resumeWorld(worldId: string) {
  return apiFetch<void>(`/api/worlds/${worldId}/resume`, { method: "POST" });
}

export function endWorld(worldId: string) {
  return apiFetch<void>(`/api/worlds/${worldId}/end`, { method: "POST" });
}

// ---- Workspaces (long-running agents with triggers) ----

export function createWorkspace(input: { name: string; goal: string; daily_token_limit?: number; daily_cost_limit_usd?: number }) {
  return apiFetch<{ workspace_id: string }>("/api/workspaces", { method: "POST", body: JSON.stringify(input) });
}

export function listWorkspaces() {
  return apiFetch<import("./workspaceTypes").WorkspaceListItem[]>("/api/workspaces");
}

export function getWorkspace(id: string) {
  return apiFetch<import("./workspaceTypes").WorkspaceSnapshot>(`/api/workspaces/${id}`);
}

export function postWorkspaceMessage(id: string, text: string, clientMessageId: string, toAgentId?: string) {
  return apiFetch<import("./workspaceTypes").ChatEntry>(`/api/workspaces/${id}/messages`, {
    method: "POST",
    body: JSON.stringify({ text, client_message_id: clientMessageId, to_agent_id: toAgentId }),
  });
}

export function addWorkspaceTrigger(
  id: string,
  trigger: { kind: "schedule" | "webhook"; name: string; instruction?: string; target_agent_id?: string; every_minutes?: number; cron?: string },
) {
  return apiFetch<import("./workspaceTypes").TriggerView>(`/api/workspaces/${id}/triggers`, {
    method: "POST",
    body: JSON.stringify(trigger),
  });
}

export function deleteWorkspaceTrigger(id: string, triggerId: string) {
  return apiFetch<void>(`/api/workspaces/${id}/triggers/${triggerId}`, { method: "DELETE" });
}

export function updateWorkspaceBudget(id: string, budget: { daily_token_limit?: number; daily_cost_limit_usd?: number }) {
  return apiFetch<void>(`/api/workspaces/${id}/budget`, { method: "PUT", body: JSON.stringify(budget) });
}

export function workspaceAction(id: string, action: "pause" | "resume" | "archive") {
  return apiFetch<void>(`/api/workspaces/${id}/${action}`, { method: "POST" });
}


// ---- Safety ----

export function updateSafetyPolicy(workspaceId: string, policy: import("./workspaceTypes").SafetyPolicy) {
  return apiFetch<import("./workspaceTypes").SafetyPolicy>(`/api/workspaces/${workspaceId}/policy`, { method: "PUT", body: JSON.stringify(policy) });
}

export function decideApproval(workspaceId: string, approvalId: string, approve: boolean, reason?: string) {
  return apiFetch<{ message: string }>(`/api/workspaces/${workspaceId}/approvals/${approvalId}/decision`, {
    method: "POST",
    body: JSON.stringify({ approve, reason: reason || undefined }),
  });
}

export function listAudit(workspaceId: string, filters: { actor?: string; action?: string; q?: string; before?: number; limit?: number } = {}) {
  const params = new URLSearchParams();
  for (const [k, v] of Object.entries(filters)) if (v !== undefined && v !== "") params.set(k, String(v));
  const qs = params.toString();
  return apiFetch<import("./workspaceTypes").AuditEntry[]>(`/api/workspaces/${workspaceId}/audit${qs ? `?${qs}` : ""}`);
}

export function verifyAudit(workspaceId: string) {
  return apiFetch<import("./workspaceTypes").AuditVerification>(`/api/workspaces/${workspaceId}/audit/verify`);
}

// ---- Integrations ----

export function listPlugins() {
  return apiFetch<import("./workspaceTypes").PluginInfo[]>("/api/plugins");
}

export function listConnections(workspaceId: string) {
  return apiFetch<import("./workspaceTypes").ConnectionView[]>(`/api/workspaces/${workspaceId}/connections`);
}

export function addConnection(workspaceId: string, body: {
  plugin_id: string; name: string; settings: Record<string, string>; secrets: Record<string, string>;
  notify_level?: string; allowed_senders?: string[];
}) {
  return apiFetch<{ message: string; connection: import("./workspaceTypes").ConnectionView }>(`/api/workspaces/${workspaceId}/connections`, {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export function updateConnection(workspaceId: string, connectionId: string, body: { notify_level?: string; enabled_tools?: string[]; allowed_senders?: string[] }) {
  return apiFetch<import("./workspaceTypes").ConnectionView>(`/api/workspaces/${workspaceId}/connections/${connectionId}`, {
    method: "PATCH",
    body: JSON.stringify(body),
  });
}

export function refreshConnection(workspaceId: string, connectionId: string) {
  return apiFetch<import("./workspaceTypes").ConnectionView>(`/api/workspaces/${workspaceId}/connections/${connectionId}/refresh`, { method: "POST" });
}

export function removeConnection(workspaceId: string, connectionId: string) {
  return apiFetch<void>(`/api/workspaces/${workspaceId}/connections/${connectionId}`, { method: "DELETE" });
}

/** Turns "POST /api/workspaces/... failed: 400 {"error":"..."}" into just the error text. */
export function apiErrorMessage(err: unknown): string {
  const text = err instanceof Error ? err.message : String(err);
  const json = text.slice(text.indexOf("{"));
  try {
    return (JSON.parse(json) as { error?: string }).error ?? text;
  } catch {
    return text;
  }
}

// ---- Platform: accounts, organization, API keys, billing (docs/platform.md) ----


export function getMe() {
  return apiFetch<import("./platformTypes").Me>("/api/auth/me");
}

export function signIn(email: string, password: string) {
  return apiFetch<{ user_id: string; tenant_id: string }>("/api/auth/login", { method: "POST", body: JSON.stringify({ email, password }) });
}

export function signUp(input: { email: string; password: string; name?: string; organization?: string; invitation?: string }) {
  return apiFetch<{ user_id: string; tenant_id: string }>("/api/auth/signup", { method: "POST", body: JSON.stringify(input) });
}

export function signOut() {
  return apiFetch<void>("/api/auth/logout", { method: "POST" });
}

export function switchOrganization(tenantId: string) {
  return apiFetch<void>("/api/auth/switch", { method: "POST", body: JSON.stringify({ tenant_id: tenantId }) });
}

export function changePassword(currentPassword: string, newPassword: string) {
  return apiFetch<void>("/api/auth/password", { method: "POST", body: JSON.stringify({ current_password: currentPassword, new_password: newPassword }) });
}

export function peekInvitation(token: string) {
  return apiFetch<{ organization: string; email: string; role: string }>(`/api/auth/invitations/${encodeURIComponent(token)}`);
}

export function acceptInvitation(token: string) {
  return apiFetch<{ tenant_id: string }>("/api/auth/invitations/accept", { method: "POST", body: JSON.stringify({ invitation: token }) });
}

export function getOrganization() {
  return apiFetch<import("./platformTypes").Organization>("/api/organization");
}

export function renameOrganization(name: string) {
  return apiFetch<void>("/api/organization", { method: "PATCH", body: JSON.stringify({ name }) });
}

export function listMembers() {
  return apiFetch<import("./platformTypes").Member[]>("/api/organization/members");
}

export function setMemberRole(userId: string, role: import("./platformTypes").Role) {
  return apiFetch<void>(`/api/organization/members/${userId}/role`, { method: "PUT", body: JSON.stringify({ role }) });
}

export function removeMember(userId: string) {
  return apiFetch<void>(`/api/organization/members/${userId}`, { method: "DELETE" });
}

export function listInvitations() {
  return apiFetch<import("./platformTypes").Invitation[]>("/api/organization/invitations");
}

export function inviteMember(email: string, role: import("./platformTypes").Role) {
  return apiFetch<import("./platformTypes").Invitation & { token: string }>("/api/organization/invitations", {
    method: "POST",
    body: JSON.stringify({ email, role }),
  });
}

export function revokeInvitation(invitationId: string) {
  return apiFetch<void>(`/api/organization/invitations/${invitationId}`, { method: "DELETE" });
}

export function listApiKeys() {
  return apiFetch<import("./platformTypes").ApiKey[]>("/api/api-keys");
}

export function createApiKey(name: string, role: import("./platformTypes").Role, expiresInDays?: number) {
  return apiFetch<{ key_id: string; name: string; role: string; key: string }>("/api/api-keys", {
    method: "POST",
    body: JSON.stringify({ name, role, expires_in_days: expiresInDays }),
  });
}

export function revokeApiKey(keyId: string) {
  return apiFetch<void>(`/api/api-keys/${keyId}`, { method: "DELETE" });
}

export function getBilling() {
  return apiFetch<import("./platformTypes").BillingView>("/api/billing");
}

export function startCheckout(planId: string) {
  return apiFetch<{ url: string }>("/api/billing/checkout", { method: "POST", body: JSON.stringify({ plan_id: planId }) });
}

export function openBillingPortal() {
  return apiFetch<{ url: string }>("/api/billing/portal", { method: "POST" });
}

