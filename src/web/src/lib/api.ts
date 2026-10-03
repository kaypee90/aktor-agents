import type {
  AgentListItem,
  AgentSpend,
  JournalStep,
  RunDiff,
  TaskPreview,
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

/** Team-shape rules for one task (docs/safety.md#team-shape); they can only tighten the server's. */
export interface TeamPolicyInput {
  max_agents?: number | null;
  max_fan_out_by_depth?: number[];
  spawner_roles?: string[];
  prevent_duplicate_roles?: boolean;
  goal_type?: string | null;
}

export interface CreateTaskInput {
  goal: string;
  budget?: Partial<ResourceBudget>;
  preview_id?: string;
  callback_url?: string;
  callback_secret?: string;
  correlation_id?: string;
  team_policy?: TeamPolicyInput;
  /** A model profile id (or "server"); the organization's default when left out. */
  model?: string | null;
  /** Upload ids from uploadFiles: the task starts with these files. */
  attachments?: string[];
  /** Tool connections (MCP servers, APIs) for the task's agents, connected before it starts. */
  connections?: { plugin_id: string; name: string; settings: Record<string, string>; secrets: Record<string, string> }[];
}

export function createTask(input: CreateTaskInput) {
  return apiFetch<{ task_id: string; root_agent_id: string; correlation_id: string; dashboard_url: string }>("/api/tasks", {
    method: "POST",
    body: JSON.stringify(input),
  });
}

/** One cheap planning call: the team the root would likely build, and its cost range. */
export function previewTask(goal: string, budget?: Partial<ResourceBudget>, model?: string | null) {
  return apiFetch<TaskPreview>("/api/tasks/preview", {
    method: "POST",
    body: JSON.stringify({ goal, budget, model: model || null }),
  });
}

/** Spend against budget for every agent and branch of a task's tree. */
export function getTaskSpend(taskId: string) {
  return apiFetch<AgentSpend[]>(`/api/tasks/${taskId}/spend`);
}

export interface TaskListItem {
  task_id: string;
  goal: string;
  status: string;
  created_at: string;
  completed_at: string | null;
  source: string;
  correlation_id: string | null;
  replay_of_task_id: string | null;
  replay_mode: string | null;
  result_summary: string | null;
  agents: number;
  tokens_used: number;
  cost_usd: number;
}

export function listTasks(limit = 100) {
  return apiFetch<TaskListItem[]>(`/api/tasks?limit=${limit}`);
}

// ---- Shared memory (docs/memory.md) ----

export interface KnowledgeEntry {
  memory_id: string;
  key: string;
  value: string;
  agent_id: string;
  created_at: string;
  score: number | null;
}

export function getMemoryStatus() {
  return apiFetch<{ semantic: boolean; embedding_model: string | null; mode: string }>("/api/memory/status");
}

/** `?workspace=` for one workspace's own skills or knowledge; nothing for the organization's. */
const scopeQuery = (workspace?: string | null, first = true) =>
  workspace ? `${first ? "?" : "&"}workspace=${encodeURIComponent(workspace)}` : "";

export function searchKnowledge(q: string, limit = 50, workspace?: string | null) {
  return apiFetch<KnowledgeEntry[]>(`/api/memory?q=${encodeURIComponent(q)}&limit=${limit}${scopeQuery(workspace, false)}`);
}

export function addKnowledge(key: string, value: string, workspace?: string | null) {
  return apiFetch<void>(`/api/memory${scopeQuery(workspace)}`, { method: "POST", body: JSON.stringify({ key, value }) });
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

/** The task's conversation: goal, follow-ups and the root agent's report for each round. */
export function getTaskChat(taskId: string) {
  return apiFetch<import("./types").TaskChatEntry[]>(`/api/tasks/${taskId}/chat`);
}

/** A follow-up instruction: a finished task picks up again with all its context. `attachments`
 * are ids returned by uploadTaskAttachments. */
export function followUpTask(taskId: string, text: string, attachments: string[] = []) {
  return apiFetch<import("./types").TaskChatEntry>(`/api/tasks/${taskId}/messages`, {
    method: "POST",
    body: JSON.stringify({ text, attachments }),
  });
}

/** Picks a task that stopped partway back up, with a budget for the rest (on top of what it spent). */
export function continueTask(taskId: string, budget: Partial<ResourceBudget>, note?: string) {
  return apiFetch<import("./types").TaskChatEntry>(`/api/tasks/${taskId}/continue`, {
    method: "POST",
    body: JSON.stringify({ budget, note: note || undefined }),
  });
}

/** Uploads files to a task (any type) for a follow-up to reference. */
export function uploadTaskAttachments(taskId: string, files: File[]) {
  return postFiles<import("./types").TaskChatFile[]>(`/api/tasks/${taskId}/attachments`, files);
}

/** Posts files as multipart "files" (not apiFetch: the browser sets the multipart boundary itself). */
async function postFiles<T>(path: string, files: File[]): Promise<T> {
  const form = new FormData();
  for (const f of files) form.append("files", f, f.name);
  const res = await fetch(`${API_BASE}${path}`, { method: "POST", body: form, credentials: "include" });
  if (res.status === 401) window.dispatchEvent(new Event(UNAUTHORIZED_EVENT));
  if (!res.ok) throw new Error(`POST ${path} failed: ${res.status} ${await res.text().catch(() => "")}`);
  return (await res.json()) as T;
}

/** Files attached to a task that doesn't exist yet; pass the ids as the new task's attachments. */
export function uploadFiles(files: File[]) {
  return postFiles<{ upload_id: string; file_name: string; size_bytes: number }[]>("/api/uploads", files);
}

/** Adds files to shared memory as knowledge (their text, in searchable passages). */
export function addKnowledgeFiles(files: File[], workspace?: string | null) {
  return postFiles<{ file_name: string; entries: number; characters: number; truncated: boolean; error: string | null }[]>(
    `/api/memory/files${scopeQuery(workspace)}`, files);
}

/** Where a file's preview and contents come from: a task's or a workspace's. */
export interface FileSource {
  previewUrl: string;
  contentUrl: string;
}

export function taskFileSource(taskId: string, artifactId: string): FileSource {
  return { previewUrl: `/api/tasks/${taskId}/artifacts/${artifactId}/preview`, contentUrl: artifactDownloadUrl(taskId, artifactId) };
}

export function workspaceFileSource(workspaceId: string, artifactId: string): FileSource {
  return { previewUrl: `/api/workspaces/${workspaceId}/files/${artifactId}/preview`, contentUrl: workspaceFileUrl(workspaceId, artifactId) };
}

export function getFilePreview(source: FileSource) {
  return apiFetch<import("./types").FilePreview>(source.previewUrl);
}

/** A file's bytes as a typed blob, for showing PDFs and images (the session cookie goes along). */
export async function fetchFileBlob(source: FileSource, contentType: string) {
  const res = await fetch(source.contentUrl, { credentials: "include" });
  if (!res.ok) throw new Error(`The file couldn't be loaded (${res.status}).`);
  return new Blob([await res.arrayBuffer()], { type: contentType });
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

export function listWorkspaceTemplates() {
  return apiFetch<import("./workspaceTypes").WorkspaceTemplate[]>("/api/workspace-templates");
}

/** A workspace from a template: its instructions, safety policy, webhooks and (with the demo
 * system) simulated connections. Returns the webhook paths, which hold a secret. */
export function createWorkspaceFromTemplate(template: string, name?: string, useDemoSystem = true) {
  return apiFetch<{ workspace_id: string; webhooks: { name: string; path: string | null }[] }>("/api/workspaces/from-template", {
    method: "POST",
    body: JSON.stringify({ template, name, use_demo_system: useDemoSystem }),
  });
}

/** Sends the template's sample alert through the workspace's own webhook. */
export function simulateWorkspaceAlert(id: string) {
  return apiFetch<{ delivered: boolean }>(`/api/workspaces/${id}/simulate-alert`, { method: "POST", body: "{}" });
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

/** A workspace's recent events from the database, oldest first and shaped like live events, so
 * the team view shows recent activity straight after a reload. */
export async function getWorkspaceHistory(id: string, limit = 300): Promise<import("./types").RuntimeEvent[]> {
  const rows = await apiFetch<EventRecordDto[]>(`/api/events?taskId=${encodeURIComponent(id)}&limit=${limit}`);
  return rows.map(({ data_json, ...row }) => {
    let data: Record<string, string> = {};
    try {
      data = JSON.parse(data_json) as Record<string, string>;
    } catch {
      // Unreadable data: keep the event without it.
    }
    return {
      event_id: row.event_id,
      type: row.type,
      timestamp: row.timestamp,
      agent_id: row.agent_id,
      parent_agent_id: row.parent_agent_id,
      target_agent_id: row.target_agent_id,
      task_id: row.task_id,
      correlation_id: row.correlation_id,
      summary: row.summary,
      data,
    };
  });
}

/** Files the workspace's agents saved, one entry per file, newest first. */
export function getWorkspaceFiles(id: string) {
  return apiFetch<import("./workspaceTypes").WorkspaceFile[]>(`/api/workspaces/${id}/files`);
}

export function workspaceFileUrl(id: string, artifactId: string) {
  return `${API_BASE}/api/workspaces/${id}/files/${artifactId}/content`;
}

export function workspaceFilesZipUrl(id: string) {
  return `${API_BASE}/api/workspaces/${id}/files.zip`;
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

/** A task's own tool connections (MCP servers, APIs) for its agents. */
export function listTaskConnections(taskId: string) {
  return apiFetch<import("./workspaceTypes").ConnectionView[]>(`/api/tasks/${taskId}/connections`);
}

export function addTaskConnection(taskId: string, body: { plugin_id: string; name: string; settings: Record<string, string>; secrets: Record<string, string> }) {
  return apiFetch<{ message: string; connection: import("./workspaceTypes").ConnectionView }>(`/api/tasks/${taskId}/connections`, {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export function updateTaskConnection(taskId: string, connectionId: string, body: { enabled_tools?: string[] }) {
  return apiFetch<import("./workspaceTypes").ConnectionView>(`/api/tasks/${taskId}/connections/${connectionId}`, { method: "PATCH", body: JSON.stringify(body) });
}

export function refreshTaskConnection(taskId: string, connectionId: string) {
  return apiFetch<import("./workspaceTypes").ConnectionView>(`/api/tasks/${taskId}/connections/${connectionId}/refresh`, { method: "POST" });
}

export function removeTaskConnection(taskId: string, connectionId: string) {
  return apiFetch<void>(`/api/tasks/${taskId}/connections/${connectionId}`, { method: "DELETE" });
}

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


/** Every recorded step of a task: each LLM decision and tool result, in order. */
export function getTaskJournal(taskId: string) {
  return apiFetch<JournalStep[]>(`/api/tasks/${taskId}/journal`);
}

/** Replays a task from its journal: "full" (no model or external calls) or a "fork" that runs live
 * after the given step. Returns the new task. */
/** model: for a fork, the model the live part runs on (the original's when left out). */
export function replayTask(taskId: string, mode: "full" | "fork", forkAfterStep?: number, model?: string | null) {
  return apiFetch<{ task_id: string; root_agent_id: string | null }>(`/api/tasks/${taskId}/replay`, {
    method: "POST",
    body: JSON.stringify({ mode, fork_after_step: forkAfterStep, model: model || null }),
  });
}

export function diffTasks(a: string, b: string) {
  return apiFetch<RunDiff>(`/api/tasks/${a}/diff/${b}`);
}

// ---- Skills (docs/skills.md) ----

export interface SkillSummary {
  name: string;
  description: string;
  version: number;
  enabled: boolean;
  updated_at: string;
  file_count: number;
}

export interface SkillFile {
  path: string;
  content: string;
}

export interface Skill {
  name: string;
  description: string;
  instructions: string;
  files: SkillFile[];
  version: number;
  enabled: boolean;
  updated_at: string;
  updated_by: string | null;
}

// Every skill call takes an optional workspace: that workspace's own skills, which only its agents use.

export function listSkills(workspace?: string | null) {
  return apiFetch<SkillSummary[]>(`/api/skills${scopeQuery(workspace)}`);
}

export function getSkill(name: string, workspace?: string | null) {
  return apiFetch<Skill>(`/api/skills/${encodeURIComponent(name)}${scopeQuery(workspace)}`);
}

/** Writes a new skill in the editor. */
export function createSkill(skill: { name: string; description: string; instructions: string; files?: SkillFile[] }, workspace?: string | null) {
  return apiFetch<Skill>(`/api/skills${scopeQuery(workspace)}`, { method: "POST", body: JSON.stringify(skill) });
}

/** Edits a skill (saved as a new version; the name stays). */
export function updateSkill(name: string, skill: { description: string; instructions: string; files?: SkillFile[] }, workspace?: string | null) {
  return apiFetch<Skill>(`/api/skills/${encodeURIComponent(name)}${scopeQuery(workspace)}`, { method: "PUT", body: JSON.stringify(skill) });
}

/** Uploads a SKILL.md or a .zip (SKILL.md plus resource files). */
export async function uploadSkill(file: File, workspace?: string | null) {
  const form = new FormData();
  form.append("file", file);
  // Not apiFetch: the browser must set the multipart Content-Type (with its boundary) itself.
  const res = await fetch(`${API_BASE}/api/skills/upload${scopeQuery(workspace)}`, { method: "POST", body: form, credentials: "include" });
  if (!res.ok) throw new Error(`POST /api/skills/upload failed: ${res.status} ${await res.text().catch(() => "")}`);
  return (await res.json()) as Skill;
}

export function setSkillEnabled(name: string, enabled: boolean, workspace?: string | null) {
  return apiFetch<void>(`/api/skills/${encodeURIComponent(name)}${scopeQuery(workspace)}`, { method: "PATCH", body: JSON.stringify({ enabled }) });
}

export function deleteSkill(name: string, workspace?: string | null) {
  return apiFetch<void>(`/api/skills/${encodeURIComponent(name)}${scopeQuery(workspace)}`, { method: "DELETE" });
}

export function skillDownloadUrl(name: string, workspace?: string | null) {
  return `${API_BASE}/api/skills/${encodeURIComponent(name)}/download${scopeQuery(workspace)}`;
}

// --- Models (docs/llm-settings.md) ---

export type LlmProviderInfo = {
  id: string;
  label: string;
  needs_api_key: boolean;
  free: boolean;
  default_base_url: string | null;
  suggested_models: string[];
  get_key_url: string | null;
};

export type ModelProfile = {
  id: string;
  name: string;
  description: string | null;
  provider: string;
  model: string;
  fast_model: string | null;
  base_url: string | null;
  price_per_input_token_usd: number | null;
  price_per_output_token_usd: number | null;
  fast_price_per_input_token_usd: number | null;
  fast_price_per_output_token_usd: number | null;
  price_per_million_input_usd: number;
  price_per_million_output_usd: number;
  api_key_set: boolean;
  uses_server_key: boolean;
  is_default: boolean;
  updated_at: string;
  updated_by: string | null;
};

export type LlmSettingsView = {
  allow_organization_settings: boolean;
  /** Agents may pick one of these models for the agents they spawn. */
  agents_may_choose: boolean;
  default_profile_id: string;
  server: {
    id: "server";
    name: string;
    provider: string;
    model: string;
    fast_model: string | null;
    price_per_million_input_usd: number;
    price_per_million_output_usd: number;
    is_default: boolean;
  };
  profiles: ModelProfile[];
  effective: {
    profile_id: string;
    name: string;
    provider: string;
    model: string;
    fast_model: string | null;
    price_per_million_input_usd: number;
    price_per_million_output_usd: number;
  };
};

export type ModelProfileInput = {
  id?: string | null;
  name?: string | null;
  description?: string | null;
  provider: string;
  model?: string | null;
  fast_model?: string | null;
  base_url?: string | null;
  api_key?: string | null;
  price_per_input_token_usd?: number | null;
  price_per_output_token_usd?: number | null;
  fast_price_per_input_token_usd?: number | null;
  fast_price_per_output_token_usd?: number | null;
  make_default?: boolean;
};

/** A model a task can run on: the server's default or one of the organization's profiles. */
export type ModelChoice = { id: string; name: string; provider: string; model: string; in_per_million: number; out_per_million: number; is_default: boolean };

export function modelChoices(view: LlmSettingsView): ModelChoice[] {
  return [
    { id: "server", name: "Server default", provider: view.server.provider, model: view.server.model,
      in_per_million: view.server.price_per_million_input_usd, out_per_million: view.server.price_per_million_output_usd, is_default: view.server.is_default },
    ...view.profiles.map((p) => ({ id: p.id, name: p.name, provider: p.provider, model: p.model,
      in_per_million: p.price_per_million_input_usd, out_per_million: p.price_per_million_output_usd, is_default: p.is_default })),
  ];
}

export function listLlmProviders() {
  return apiFetch<LlmProviderInfo[]>("/api/llm/providers");
}

export function getLlmSettings() {
  return apiFetch<LlmSettingsView>("/api/llm/settings");
}

export function createModelProfile(input: ModelProfileInput) {
  return apiFetch<LlmSettingsView>("/api/llm/profiles", { method: "POST", body: JSON.stringify(input) });
}

export function updateModelProfile(id: string, input: ModelProfileInput) {
  return apiFetch<LlmSettingsView>(`/api/llm/profiles/${encodeURIComponent(id)}`, { method: "PUT", body: JSON.stringify(input) });
}

export function deleteModelProfile(id: string) {
  return apiFetch<LlmSettingsView>(`/api/llm/profiles/${encodeURIComponent(id)}`, { method: "DELETE" });
}

export function setDefaultModel(profileId: string) {
  return apiFetch<LlmSettingsView>("/api/llm/default", { method: "PUT", body: JSON.stringify({ profile_id: profileId }) });
}

export function setAgentsMayChoose(enabled: boolean) {
  return apiFetch<LlmSettingsView>("/api/llm/agent-choice", { method: "PUT", body: JSON.stringify({ enabled }) });
}

export function resetLlmSettings() {
  return apiFetch<LlmSettingsView>("/api/llm/settings", { method: "DELETE" });
}

export function testLlmSettings(input: ModelProfileInput) {
  return apiFetch<{ ok: boolean; message: string; latency_ms: number; input_tokens: number | null; output_tokens: number | null }>(
    "/api/llm/test", { method: "POST", body: JSON.stringify(input) });
}

export function listLlmModels(provider: string, baseUrl?: string | null, apiKey?: string | null, profileId?: string | null) {
  return apiFetch<{ models: string[] }>("/api/llm/models", {
    method: "POST",
    body: JSON.stringify({ provider, base_url: baseUrl || null, api_key: apiKey || null, profile_id: profileId || null }),
  });
}

export type TaskModel = { profile_id: string; name: string; provider: string; model: string; chosen: boolean };

/** Moves a running task to another model; every agent uses it from its next step. */
export function switchTaskModel(taskId: string, model: string) {
  return apiFetch<{ model: TaskModel }>(`/api/tasks/${taskId}/model`, { method: "POST", body: JSON.stringify({ model }) });
}

// --- Analytics (docs/analytics.md) ---

export type AnalyticsFilter = {
  /** A preset window ending now (24h, 7d, 30d, 90d); otherwise from/to. */
  range?: string | null;
  from?: string | null;
  to?: string | null;
  source?: string | null;
  status?: "running" | "completed" | "failed" | null;
  q?: string | null;
  /** "tasks" (default) or "workspaces". */
  scope?: "tasks" | "workspaces" | null;
  workspace?: string | null;
  /** A model profile id: only what ran on it. */
  model?: string | null;
};

/** Spend and response time per model: which one is cheaper or faster for the same work. */
export type AnalyticsModelRow = {
  profile_id: string;
  profile_name: string;
  provider: string;
  model: string;
  label: string;
  calls: number;
  tokens: number;
  cost_usd: number;
  avg_cost_per_call_usd: number;
  avg_duration_ms: number;
  p95_duration_ms: number | null;
};

export type AnalyticsToolRow = { tool: string; calls: number; failures: number; avg_duration_ms: number | null; p95_duration_ms: number | null; total_duration_ms: number };

export type WorkspaceAnalytics = {
  scope: "workspaces";
  range: { from: string; to: string; bucket: "hour" | "day" };
  totals: {
    workspaces: number;
    active_workspaces: number;
    calls: number;
    tokens: number;
    cost_usd: number;
    avg_cost_per_day_usd: number;
    avg_call_ms: number | null;
    p95_call_ms: number | null;
    triggers_fired: number;
    approvals_requested: number;
    approvals_approved: number;
    approvals_rejected: number;
    approvals_expired: number;
    tool_calls: number;
    tool_failures: number;
  };
  previous: { calls: number; tokens: number; cost_usd: number };
  series: { t: string; calls: number; tokens: number; cost_usd: number; avg_duration_s: number | null }[];
  by_workspace: { workspace_id: string; name: string; status: string; calls: number; tokens: number; cost_usd: number; triggers_fired: number; approvals_requested: number }[];
  by_role: { role: string; calls: number; tokens: number; cost_usd: number; avg_tokens: number }[];
  by_model: AnalyticsModelRow[];
  by_tool: AnalyticsToolRow[];
};

export type AnalyticsTaskRow = {
  task_id: string;
  goal: string;
  status: string;
  source: string;
  created_at: string;
  duration_s: number | null;
  tokens: number;
  cost_usd: number;
  agents: number;
};

export type Analytics = {
  scope: "tasks";
  range: { from: string; to: string; bucket: "hour" | "day" };
  by_model: AnalyticsModelRow[];
  totals: {
    runs: number;
    completed: number;
    failed: number;
    running: number;
    tokens: number;
    cost_usd: number;
    avg_cost_usd: number;
    avg_tokens: number;
    avg_duration_s: number | null;
    p50_duration_s: number | null;
    p95_duration_s: number | null;
    agents: number;
    tool_calls: number;
    tool_failures: number;
  };
  previous: { runs: number; tokens: number; cost_usd: number; avg_cost_usd: number; avg_duration_s: number | null };
  series: { t: string; runs: number; tokens: number; cost_usd: number; avg_duration_s: number | null }[];
  by_role: { role: string; agents: number; tokens: number; cost_usd: number; avg_tokens: number }[];
  by_source: { source: string; runs: number; tokens: number; cost_usd: number }[];
  by_status: { status: string; runs: number }[];
  by_tool: { tool: string; calls: number; failures: number; avg_duration_ms: number | null; p95_duration_ms: number | null; total_duration_ms: number }[];
  duration_histogram: { label: string; runs: number }[];
  top_by_cost: AnalyticsTaskRow[];
  slowest: AnalyticsTaskRow[];
};

export function getAnalytics(filter: AnalyticsFilter) {
  const params = new URLSearchParams();
  for (const [k, v] of Object.entries(filter)) if (v) params.set(k, v);
  // Days are counted in the viewer's timezone.
  params.set("tz_offset_minutes", String(new Date().getTimezoneOffset()));
  return apiFetch<Analytics | WorkspaceAnalytics>(`/api/analytics?${params.toString()}`);
}
