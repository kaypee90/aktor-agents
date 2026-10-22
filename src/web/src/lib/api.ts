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
  /** The file a passage was added from, when it's one of several ("report.pdf (part 2 of 5)"). */
  file_name: string | null;
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

/** How much knowledge a scope holds (search results are capped, so counts come from here). */
export interface KnowledgeSummary {
  entries: number;
  files: number;
  facts: number;
  from_agents: number;
  file_names: string[];
  /** Keys of the facts people typed, newest first (for @knowledge mentions). */
  fact_keys?: string[];
}

export function getKnowledgeSummary(workspace?: string | null) {
  return apiFetch<KnowledgeSummary>(`/api/memory/summary${scopeQuery(workspace)}`);
}

/** Deletes one knowledge entry (Admin). */
export function deleteKnowledge(memoryId: string, workspace?: string | null) {
  return apiFetch<{ deleted: number }>(`/api/memory/${encodeURIComponent(memoryId)}${scopeQuery(workspace)}`, { method: "DELETE" });
}

/** Deletes every passage of a file added as knowledge (Admin). */
export function deleteKnowledgeFile(fileName: string, workspace?: string | null) {
  return apiFetch<{ deleted: number }>(`/api/memory/files?name=${encodeURIComponent(fileName)}${scopeQuery(workspace, false)}`, { method: "DELETE" });
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

// ---- Workspaces (long-running agents with triggers) ----

/** A workspace for `goal`; its pipeline is drafted from the goal by the pipeline editor. */
/** With `blank`, the workspace starts with an empty canvas and `goal` may be empty. */
export function createWorkspace(input: { name: string; goal: string; blank?: boolean; daily_token_limit?: number; daily_cost_limit_usd?: number; visibility?: import("./workspaceTypes").WorkspaceVisibility }) {
  return apiFetch<{ workspace_id: string; draft: string | null }>("/api/workspaces", { method: "POST", body: JSON.stringify(input) });
}

/** Replaces the workspace's variables (values every stage can use as {{ vars.name }}). */
export function setWorkspaceVariables(id: string, variables: Record<string, string>) {
  return apiFetch<{ variables: Record<string, string> }>(`/api/workspaces/${id}/variables`, { method: "PUT", body: JSON.stringify({ variables }) });
}

/** Only the workspace's owner may change who sees it. */
export function setWorkspaceVisibility(id: string, visibility: import("./workspaceTypes").WorkspaceVisibility) {
  return apiFetch<{ visibility: import("./workspaceTypes").WorkspaceVisibility }>(`/api/workspaces/${id}/visibility`, { method: "PUT", body: JSON.stringify({ visibility }) });
}

// ---- Pipelines and runs (docs/workspaces.md) ----

export function getPipelineHistory(id: string) {
  return apiFetch<import("./pipelineTypes").PipelineDefinition[]>(`/api/workspaces/${id}/pipeline/history`);
}

/** What the editor understood from a plain-language request; changes nothing until applied. */
export function proposePipelineChange(id: string, request: string) {
  return apiFetch<import("./pipelineTypes").PipelineProposal>(`/api/workspaces/${id}/pipeline/propose`, {
    method: "POST",
    body: JSON.stringify({ request }),
  });
}

/** Applies edits to the pipeline at `baseVersion`; fails with a conflict if it changed since. */
export function applyPipelineEdits(id: string, ops: import("./pipelineTypes").PipelineEditOp[], baseVersion: number, note?: string) {
  return apiFetch<import("./pipelineTypes").PipelineChangeResult>(`/api/workspaces/${id}/pipeline/edits`, {
    method: "POST",
    body: JSON.stringify({ ops, base_version: baseVersion, note }),
  });
}

export function savePipeline(id: string, pipeline: import("./pipelineTypes").PipelineDefinition, baseVersion: number, note?: string) {
  return apiFetch<import("./pipelineTypes").PipelineChangeResult>(`/api/workspaces/${id}/pipeline`, {
    method: "PUT",
    body: JSON.stringify({ pipeline, base_version: baseVersion, note }),
  });
}

/** Places stages on the canvas (no new version); an empty layout lays it out automatically. */
export function savePipelineLayout(id: string, layout: Record<string, import("./pipelineTypes").StagePosition>) {
  return apiFetch<import("./pipelineTypes").PipelineDefinition>(`/api/workspaces/${id}/pipeline/layout`, {
    method: "PUT",
    body: JSON.stringify({ layout }),
  });
}

export function restorePipelineVersion(id: string, version: number) {
  return apiFetch<import("./pipelineTypes").PipelineChangeResult>(`/api/workspaces/${id}/pipeline/restore`, {
    method: "POST",
    body: JSON.stringify({ version }),
  });
}

/** Starts a run. With `data` (any JSON), action and script stages see it as `input`. */
export function startPipelineRun(id: string, input: string, data?: unknown) {
  return apiFetch<import("./pipelineTypes").RunStartResult>(`/api/workspaces/${id}/runs`, {
    method: "POST",
    body: JSON.stringify(data === undefined ? { input } : { input, data }),
  });
}

export interface ScriptTestResult {
  ok: boolean;
  output: unknown;
  error: string | null;
  logs: string;
  duration_ms: number;
}

/** Runs a script stage's code in the sandbox with sample data. */
export function testScript(id: string, body: { language: string; code: string; data: unknown; timeout_seconds?: number | null }) {
  return apiFetch<ScriptTestResult>(`/api/workspaces/${id}/scripts/test`, { method: "POST", body: JSON.stringify(body) });
}

export function getForm(workspaceId: string, triggerId: string) {
  return apiFetch<import("./workspaceTypes").FormView>(`/api/workspaces/${workspaceId}/forms/${triggerId}`);
}

export function submitForm(workspaceId: string, triggerId: string, answers: Record<string, unknown>, submissionId: string) {
  return apiFetch<{ success: boolean; message: string; run_id: string | null; number: number }>(
    `/api/workspaces/${workspaceId}/forms/${triggerId}/submissions`,
    { method: "POST", body: JSON.stringify({ answers, submission_id: submissionId }) },
  );
}

/** Problems with particular fields, from a refused form submission. */
export function apiFieldErrors(err: unknown): Record<string, string> {
  const text = err instanceof Error ? err.message : String(err);
  try {
    return (JSON.parse(text.slice(text.indexOf("{"))) as { field_errors?: Record<string, string> }).field_errors ?? {};
  } catch {
    return {};
  }
}

export function getPipelineRun(id: string, runId: string) {
  return apiFetch<import("./pipelineTypes").PipelineRunView>(`/api/workspaces/${id}/runs/${runId}`);
}

export function controlPipelineRun(id: string, runId: string, action: "pause" | "resume" | "cancel") {
  return apiFetch<import("./pipelineTypes").PipelineRunView>(`/api/workspaces/${id}/runs/${runId}/${action}`, { method: "POST", body: "{}" });
}

export function listWorkspaceTemplates() {
  return apiFetch<import("./workspaceTypes").WorkspaceTemplate[]>("/api/workspace-templates");
}

/** A workspace from a template: its instructions, safety policy, webhooks and (with the demo
 * system) simulated connections. Returns the webhook paths, which hold a secret. */
export function createWorkspaceFromTemplate(template: string, name?: string, useDemoSystem = true, visibility?: import("./workspaceTypes").WorkspaceVisibility) {
  return apiFetch<{ workspace_id: string; webhooks?: { name: string; path: string | null }[]; sample_input: string | null }>("/api/workspaces/from-template", {
    method: "POST",
    body: JSON.stringify({ template, name, use_demo_system: useDemoSystem, visibility }),
  });
}

/** What a copy (or a workspace from one of your templates) got: each connection and trigger. */
export interface WorkspaceCopyResult {
  workspace_id: string;
  name?: string;
  connections: { name: string; plugin_id: string; ok: boolean; message: string }[];
  triggers: { name: string; kind: string; ok: boolean; message: string; webhook_path: string | null }[];
  connections_without_secrets?: string[];
  skills?: number;
  knowledge?: number;
}

/** A new workspace with this one's setup (pipeline, triggers, connections, skills, knowledge);
 * never its runs, files or chat. Connections are copied (with their secrets) for Admins only. */
export function cloneWorkspace(id: string, name: string, copySkillsAndKnowledge = true) {
  return apiFetch<WorkspaceCopyResult>(`/api/workspaces/${id}/clone`, {
    method: "POST", body: JSON.stringify({ name: name || null, copy_skills_and_knowledge: copySkillsAndKnowledge }),
  });
}

/** Saves the workspace as one of the organization's templates (no secrets, runs or files). */
export function exportWorkspaceTemplate(id: string, template: { name?: string; description?: string; category?: string }) {
  return apiFetch<{ template_id: string; name: string; category: string }>(`/api/workspaces/${id}/export-template`, { method: "POST", body: JSON.stringify(template) });
}

/** Deletes an archived workspace (Admin). Its runs' history and the audit log stay. */
export function deleteWorkspace(id: string) {
  return apiFetch<void>(`/api/workspaces/${id}`, { method: "DELETE" });
}

export function deleteWorkspaceTemplate(id: string) {
  return apiFetch<void>(`/api/workspace-templates/${encodeURIComponent(id)}`, { method: "DELETE" });
}

export function workspaceTemplateDownloadUrl(id: string) {
  return `${API_BASE}/api/workspace-templates/${encodeURIComponent(id)}/download`;
}

/** Adds a template from a downloaded template file's contents. */
export function importWorkspaceTemplate(json: string) {
  return apiFetch<import("./workspaceTypes").WorkspaceTemplate>("/api/workspace-templates/import", { method: "POST", body: json });
}

/** Sends the template's sample event through the workspace's own webhook (starting a run). */
export function simulateWorkspaceAlert(id: string) {
  return apiFetch<{ delivered: boolean }>(`/api/workspaces/${id}/simulate-alert`, { method: "POST", body: "{}" });
}

/** Which workspaces to list: "active" is everything not archived; solution is an install id, or
 * "none" for workspaces no solution made; q matches words in the name or goal. */
export type WorkspaceListFilter = { status?: "all" | "active" | "archived"; solution?: string; q?: string; limit?: number; offset?: number };

/** The organization's workspaces, newest first (the server's default page is 100). */
export function listWorkspaces(filter: WorkspaceListFilter = {}) {
  const params = new URLSearchParams();
  for (const [k, v] of Object.entries(filter)) if (v !== undefined && v !== "") params.set(k, String(v));
  const query = params.toString();
  return apiFetch<import("./workspaceTypes").WorkspaceListItem[]>(`/api/workspaces${query ? `?${query}` : ""}`);
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

/** A watch's rule: which items of a read-only tool's result to check, and when they match. */
export interface WatchInput {
  source_tool: string;
  /** The tool's arguments each check, e.g. {"path": "/todos?userId=1"}. */
  source_arguments?: Record<string, unknown>;
  items_path?: string;
  conditions: { field: string; op: string; value?: string }[];
  key_field?: string;
  display_fields?: string[];
  /** "notify": alert in the chat (and channels) with no model call; "run": run the pipeline with the matches. */
  mode: "notify" | "run";
  message?: string;
  urgency?: "info" | "warning" | "urgent";
}

/** Creates a trigger. A watch's response includes a dry run of its rule (items found, matching now). */
export function addWorkspaceTrigger(
  id: string,
  trigger: { kind: "schedule" | "webhook" | "watch" | "form"; name: string; instruction?: string; every_minutes?: number; cron?: string; form?: import("./workspaceTypes").FormDefinition } & Partial<WatchInput>,
) {
  return apiFetch<import("./workspaceTypes").TriggerView & { dry_run?: { items_found: number; matching_now: number } }>(`/api/workspaces/${id}/triggers`, {
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

export function getOrganizationPolicy() {
  return apiFetch<import("./workspaceTypes").OrganizationSafetyPolicy>("/api/organization/policy");
}

export function updateOrganizationPolicy(policy: import("./workspaceTypes").OrganizationSafetyPolicy) {
  return apiFetch<import("./workspaceTypes").OrganizationSafetyPolicy>("/api/organization/policy", { method: "PUT", body: JSON.stringify(policy) });
}

/** Changes to the organization's policy, newest first. */
export function getOrganizationPolicyAudit() {
  return apiFetch<import("./workspaceTypes").AuditEntry[]>("/api/organization/policy/audit");
}

/** An approval request waiting for a person, with its workspace. */
export type PendingApproval = {
  workspace_id: string;
  workspace_name: string;
  approval: import("./workspaceTypes").ApprovalRecord;
};

/** Approval requests waiting in any of the organization's workspaces, oldest first. */
export function listPendingApprovals() {
  return apiFetch<PendingApproval[]>("/api/approvals/pending");
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

export function addTaskConnection(taskId: string, body: { plugin_id: string; name: string; settings: Record<string, string>; secrets: Record<string, string>; global_connector?: string }) {
  return apiFetch<{ message: string; connection: import("./workspaceTypes").ConnectionView }>(`/api/tasks/${taskId}/connections`, {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export function updateTaskConnection(taskId: string, connectionId: string, body: ConnectionUpdateBody) {
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

/** A body for adding a connection: a plugin with its settings and secrets, or a link to a global connector. */
export interface AddConnectionBody {
  plugin_id: string; name: string; settings: Record<string, string>; secrets: Record<string, string>;
  notify_level?: string; allowed_senders?: string[];
  /** Link this global connector (id or name) instead: plugin, settings and secrets come from it. */
  global_connector?: string;
}

export function addConnection(workspaceId: string, body: AddConnectionBody) {
  return apiFetch<{ message: string; connection: import("./workspaceTypes").ConnectionView }>(`/api/workspaces/${workspaceId}/connections`, {
    method: "POST",
    body: JSON.stringify(body),
  });
}

/** A change to a connection: settings (e.g. an HTTP API's endpoints) re-check it; gateway is workspaces only. */
export interface ConnectionUpdateBody {
  notify_level?: string;
  enabled_tools?: string[];
  allowed_senders?: string[];
  settings?: Record<string, string>;
  gateway?: import("./workspaceTypes").McpGatewaySettings;
}

export function updateConnection(workspaceId: string, connectionId: string, body: ConnectionUpdateBody) {
  return apiFetch<import("./workspaceTypes").ConnectionView>(`/api/workspaces/${workspaceId}/connections/${connectionId}`, {
    method: "PATCH",
    body: JSON.stringify(body),
  });
}

/** Reads an OpenAPI document (JSON or YAML) into endpoints for an HTTP API connection; saves nothing. */
export function importOpenApi(spec: string) {
  return apiFetch<{ title: string | null; base_url: string | null; endpoints: import("./workspaceTypes").ApiEndpoint[]; warnings: string[] }>(
    "/api/integrations/openapi", { method: "POST", body: JSON.stringify({ spec }) });
}

export function refreshConnection(workspaceId: string, connectionId: string) {
  return apiFetch<import("./workspaceTypes").ConnectionView>(`/api/workspaces/${workspaceId}/connections/${connectionId}/refresh`, { method: "POST" });
}

export function removeConnection(workspaceId: string, connectionId: string) {
  return apiFetch<void>(`/api/workspaces/${workspaceId}/connections/${connectionId}`, { method: "DELETE" });
}

// ---------------------------------------------------------------- global connectors

/** An integration set up once for the organization and linked from workspaces, tasks and solutions. */
export interface GlobalConnector {
  connector_id: string;
  name: string;
  plugin_id: string;
  plugin_name: string;
  description?: string | null;
  settings: Record<string, string>;
  secret_keys: string[];
  usable_in_tasks: boolean;
  created_by?: string | null;
  created_at: string;
  updated_at: string;
  used_by: { owner_kind: "workspace" | "task"; owner_id: string; owner_name?: string | null; connection_id: string; connection_name: string }[];
}

export interface GlobalConnectorBody {
  name?: string;
  plugin_id?: string;
  description?: string;
  settings?: Record<string, string>;
  /** New secret values; ones left out keep what's stored. */
  secrets?: Record<string, string>;
  usable_in_tasks?: boolean;
}

export function listGlobalConnectors() {
  return apiFetch<GlobalConnector[]>("/api/connectors");
}

export function createGlobalConnector(body: GlobalConnectorBody) {
  return apiFetch<GlobalConnector>("/api/connectors", { method: "POST", body: JSON.stringify(body) });
}

export function updateGlobalConnector(id: string, body: GlobalConnectorBody) {
  return apiFetch<{ connector: GlobalConnector; refreshed_connections: number; problems: string[] }>(`/api/connectors/${encodeURIComponent(id)}`, {
    method: "PATCH", body: JSON.stringify(body),
  });
}

export function testGlobalConnector(id: string) {
  return apiFetch<{ ok: boolean; message: string }>(`/api/connectors/${encodeURIComponent(id)}/test`, { method: "POST" });
}

export function deleteGlobalConnector(id: string, force = false) {
  return apiFetch<{ deleted: boolean; removed_connections: number }>(`/api/connectors/${encodeURIComponent(id)}${force ? "?force=true" : ""}`, { method: "DELETE" });
}

/** Turns "POST /api/workspaces/... failed: 400 {"error":"..."}" into just the error text. */
export function apiErrorMessage(err: unknown): string {
  const text = err instanceof Error ? err.message : String(err);
  const json = text.slice(text.indexOf("{"));
  try {
    // Most errors are { error }; pipeline changes list every problem in { errors }.
    const body = JSON.parse(json) as { error?: string; errors?: string[] };
    return [body.error, ...(body.errors ?? [])].filter(Boolean).join(" ") || text;
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
  /** The models with a list price, newest first; empty for local providers. */
  models: ListedModel[];
  /** When the list prices were copied from the provider; null without any. */
  prices_as_of: string | null;
  /** Official standard API pricing source; null for local and demo providers. */
  pricing_url: string | null;
  get_key_url: string | null;
};

/** A model on the provider's price list, in USD per million tokens. */
export type ListedModel = {
  id: string;
  name: string;
  input_per_million_usd: number;
  output_per_million_usd: number;
  cached_input_per_million_usd: number | null;
  /** What the list price doesn't cover, e.g. a higher rate for long prompts. */
  note: string | null;
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
  /** custom: entered for it; list: the provider's list price; free: local; server: nobody priced it. */
  price_source: "custom" | "list" | "free" | "server";
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
  /** Who started the runs: a user id, "key:<id>" for an API key, or "unknown" (tasks only). */
  user?: string | null;
};

/** Spend and response time per model: which one is cheaper or faster for the same work. */
/** Model-call tokens by kind. Input is uncached input only: cache reads and writes are counted apart
 * (and priced apart), so the four add up to every token sent and received. */
export type AnalyticsTokenUsage = {
  calls: number;
  input_tokens: number;
  output_tokens: number;
  cache_read_tokens: number;
  cache_write_tokens: number;
};

export type AnalyticsModelRow = AnalyticsTokenUsage & {
  profile_id: string;
  profile_name: string;
  provider: string;
  model: string;
  label: string;
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
    usage: AnalyticsTokenUsage;
    avg_call_ms: number | null;
    p95_call_ms: number | null;
    triggers_fired: number;
    approvals_requested: number;
    approvals_approved: number;
    approvals_rejected: number;
    approvals_expired: number;
    tool_calls: number;
    tool_failures: number;
    /** The pipelines' runs that started in the range. */
    runs: number;
    runs_completed: number;
    runs_failed: number;
    runs_running: number;
    avg_run_cost_usd: number;
    p50_run_duration_s: number | null;
    p95_run_duration_s: number | null;
  };
  previous: { calls: number; tokens: number; cost_usd: number; runs: number };
  series: { t: string; calls: number; runs: number; tokens: number; cost_usd: number; avg_duration_s: number | null }[];
  by_workspace: { workspace_id: string; name: string; status: string; calls: number; tokens: number; cost_usd: number; triggers_fired: number; approvals_requested: number; runs: number; runs_failed: number }[];
  by_role: { role: string; calls: number; tokens: number; cost_usd: number; avg_tokens: number }[];
  /** Who started the pipelines' runs (a member, an API key, or triggers). */
  by_user: AnalyticsUserRow[];
  top_runs_by_cost: AnalyticsTaskRow[];
  slowest_runs: AnalyticsTaskRow[];
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
  started_by: string;
  started_by_name: string;
  /** For a pipeline's run: its workspace. */
  workspace_id?: string;
  workspace_name?: string;
};

/** Usage by whoever started the runs: a member, an API key, or "unknown" for older runs. */
export type AnalyticsUserRow = {
  user: string;
  name: string;
  kind: "user" | "api_key" | "trigger" | "unknown";
  runs: number;
  tokens: number;
  cost_usd: number;
  avg_cost_usd: number;
  failed: number;
  last_run_at: string;
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
    /** Median tokens per run. */
    p50_tokens: number;
    /** Days (in the viewer's timezone) on which at least one run started. */
    active_days: number;
    avg_cost_per_active_day_usd: number;
    usage: AnalyticsTokenUsage;
  };
  previous: { runs: number; tokens: number; cost_usd: number; avg_cost_usd: number; avg_duration_s: number | null };
  series: { t: string; runs: number; tokens: number; cost_usd: number; avg_duration_s: number | null }[];
  by_role: { role: string; agents: number; tokens: number; cost_usd: number; avg_tokens: number }[];
  by_source: { source: string; runs: number; tokens: number; cost_usd: number }[];
  by_user: AnalyticsUserRow[];
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

// ---- Solutions: packaged apps, their records and end users (docs/solutions.md) ----

export interface SolutionBundle {
  manifest: string;
  files: Record<string, string>;
}

export interface SolutionSummary {
  install_id: string;
  name: string;
  title: string;
  version: string;
  status: "Active" | "Installing" | "Uninstalled";
  installed_at: string;
  updated_at: string;
}

export interface SolutionFieldDef {
  type: string;
  required?: boolean;
  description?: string | null;
  values?: string[] | null;
  entity?: string | null;
}

export interface SolutionView {
  install_id: string;
  name: string;
  title: string;
  version: string;
  status: string;
  description?: string | null;
  installed_by?: string | null;
  installed_at: string;
  updated_at: string;
  inputs: Record<string, string>;
  workspaces: { key: string; workspace_id: string; name?: string | null }[];
  entities: { name: string; description?: string | null; records: number; title_field?: string | null; fields: Record<string, SolutionFieldDef> }[];
  connectors: { name: string; plugin: string; required: boolean; description?: string | null; global?: string | null }[];
  assistant: {
    name: string;
    greeting?: string | null;
    data: Record<string, string>;
    allowed_origins?: string[];
    monthly_cost_limit_usd_per_user?: number | null;
    max_messages_per_minute?: number;
  } | null;
  /** End users sign in with the builder's own identity provider (JWTs), not tokens from an app key. */
  own_sign_in?: boolean;
  evals: { name: string; workspace: string }[];
  webhooks: { url: string; events: string[] }[];
  webhook_secret?: string | null;
  history: { version: string; at: string; by?: string | null; action: string; from?: string }[];
}

export interface SolutionValidation {
  ok: boolean;
  errors: string[];
  warnings: string[];
  summary: {
    name: string; title: string; version: string;
    workspaces: { key: string; name: string; stages: number; triggers: string[] }[];
    entities: { name: string; fields: number }[];
    connectors: { name: string; plugin: string; required: boolean }[];
    inputs: { key: string; label: string; type: string; required: boolean; default?: string | null; description?: string | null }[];
    assistant?: string | null;
    evals: number;
  } | null;
  secrets: Record<string, { key: string; label: string; required: boolean; description?: string | null }[]> | null;
  /** For each connector, the organization's global connectors of the same plugin it could use instead. */
  globals?: Record<string, { connector_id: string; name: string; description?: string | null }[]> | null;
}

export interface SolutionRecord {
  id: string;
  version: number;
  created_at: string;
  updated_at: string;
  [field: string]: unknown;
}

export interface EvalReport {
  passed: number;
  failed: number;
  results: { name: string; passed: boolean; failures: string[]; run_id?: string; status?: string; summary?: string; duration_ms?: number }[];
}

/** Installed solutions; with <c>all</c>, uninstalled ones too. */
export function listSolutions(all = false) {
  return apiFetch<SolutionSummary[]>(`/api/v1/solutions${all ? "?all=true" : ""}`);
}

export function getSolution(idOrName: string) {
  return apiFetch<SolutionView>(`/api/v1/solutions/${encodeURIComponent(idOrName)}`);
}

export function validateSolution(bundle: SolutionBundle) {
  return apiFetch<SolutionValidation>("/api/v1/solutions/validate", { method: "POST", body: JSON.stringify(bundle) });
}

export function deploySolution(input: {
  installId?: string; bundle: SolutionBundle; packageRef?: string; inputs: Record<string, string>; secrets: Record<string, Record<string, string>>;
  /** Connector name → global connector name ("" to use its own secrets). */
  globals?: Record<string, string>;
  force?: boolean;
}) {
  // From the registry, the server takes the package by reference (and checks its signature).
  const common = { inputs: input.inputs, secrets: input.secrets, globals: input.globals, force: input.force ?? false };
  const body = JSON.stringify(input.packageRef ? { package: input.packageRef, ...common } : { bundle: input.bundle, ...common });
  return input.installId
    ? apiFetch<Record<string, unknown>>(`/api/v1/solutions/${encodeURIComponent(input.installId)}`, { method: "PUT", body })
    : apiFetch<Record<string, unknown>>("/api/v1/solutions", { method: "POST", body });
}

export function uninstallSolution(installId: string, purge: boolean) {
  return apiFetch<void>(`/api/v1/solutions/${encodeURIComponent(installId)}${purge ? "?purge=true" : ""}`, { method: "DELETE" });
}

export function listSolutionRecords(installId: string, entity: string, options: { limit?: number; offset?: number; filters?: string[] } = {}) {
  const q = new URLSearchParams();
  if (options.limit) q.set("limit", String(options.limit));
  if (options.offset) q.set("offset", String(options.offset));
  for (const f of options.filters ?? []) q.append("filter", f);
  return apiFetch<{ entity: string; total: number; items: SolutionRecord[] }>(
    `/api/v1/solutions/${encodeURIComponent(installId)}/records/${encodeURIComponent(entity)}?${q}`);
}

export function runSolutionEvals(installId: string) {
  return apiFetch<EvalReport>(`/api/v1/solutions/${encodeURIComponent(installId)}/evals`, { method: "POST", body: "{}" });
}

export function createEndUserToken(installId: string, userId: string, name?: string, ttlMinutes = 60) {
  return apiFetch<{ token: string; expires_at: string }>(`/api/v1/solutions/${encodeURIComponent(installId)}/end-user-tokens`, {
    method: "POST",
    body: JSON.stringify({ user_id: userId, name: name || undefined, ttl_minutes: ttlMinutes }),
  });
}

/** A solution's app key, as listed: never the key itself (docs/solutions.md#app-keys). */
export interface AppKey {
  key_id: string;
  name: string;
  prefix: string;
  created_by: string;
  created_at: string;
  last_used_at?: string | null;
  revoked_at?: string | null;
}

export function listAppKeys(installId: string) {
  return apiFetch<AppKey[]>(`/api/v1/solutions/${encodeURIComponent(installId)}/app-keys`);
}

/** A new app key; the full key is in this answer only. */
export function createAppKey(installId: string, name: string) {
  return apiFetch<{ key: string; key_id: string; name: string; prefix: string; created_at: string }>(`/api/v1/solutions/${encodeURIComponent(installId)}/app-keys`, {
    method: "POST",
    body: JSON.stringify({ name }),
  });
}

export function revokeAppKey(installId: string, keyId: string) {
  return apiFetch<void>(`/api/v1/solutions/${encodeURIComponent(installId)}/app-keys/${encodeURIComponent(keyId)}`, { method: "DELETE" });
}

/** A ready-made solution to start from in the builder. */
export interface SolutionTemplate {
  name: string;
  title: string;
  description?: string | null;
  version: string;
  entities: number;
  workflows: number;
  assistant: boolean;
  evals: number;
}

export function listSolutionTemplates() {
  return apiFetch<SolutionTemplate[]>("/api/v1/solutions/templates");
}

export function getSolutionTemplate(name: string) {
  return apiFetch<SolutionBundle>(`/api/v1/solutions/templates/${encodeURIComponent(name)}`);
}

/** A folder picked in the browser (input webkitdirectory) as a package: its aktor.yaml and text files. */
export async function bundleFromFolder(files: FileList | File[]): Promise<SolutionBundle> {
  const list = Array.from(files);
  const text = new Set(["yaml", "yml", "json", "md", "txt", "csv", "py", "js", "html", "sql"]);
  const skip = new Set(["node_modules", ".git", "bin", "obj", ".aktor"]);
  const rel = (f: File) => ((f as File & { webkitRelativePath?: string }).webkitRelativePath || f.name).split("/").slice(1).join("/") || f.name;
  const manifestFile = list.find((f) => ["aktor.yaml", "aktor.yml", "aktor.json"].includes(rel(f)));
  if (!manifestFile) throw new Error("That folder has no aktor.yaml at its top level.");
  const out: Record<string, string> = {};
  for (const f of list) {
    const path = rel(f);
    if (f === manifestFile || path.split("/").some((p) => p.startsWith(".") || skip.has(p))) continue;
    if (!text.has(path.split(".").pop()?.toLowerCase() ?? "")) continue;
    out[path] = await f.text();
  }
  return { manifest: await manifestFile.text(), files: out };
}

export function createSolutionRecord(installId: string, entity: string, fields: Record<string, unknown>) {
  return apiFetch<SolutionRecord>(`/api/v1/solutions/${encodeURIComponent(installId)}/records/${encodeURIComponent(entity)}`, {
    method: "POST",
    body: JSON.stringify(fields),
  });
}

/** Only the fields given change (null clears one); a record changed since `version` is refused (409). */
export function updateSolutionRecord(installId: string, entity: string, id: string, fields: Record<string, unknown>, version: number) {
  return apiFetch<SolutionRecord>(`/api/v1/solutions/${encodeURIComponent(installId)}/records/${encodeURIComponent(entity)}/${encodeURIComponent(id)}`, {
    method: "PATCH",
    body: JSON.stringify(fields),
    headers: { "If-Match": `"${version}"` },
  });
}

export function deleteSolutionRecord(installId: string, entity: string, id: string) {
  return apiFetch<void>(`/api/v1/solutions/${encodeURIComponent(installId)}/records/${encodeURIComponent(entity)}/${encodeURIComponent(id)}`, { method: "DELETE" });
}

export interface RegistryPackage {
  package: string;
  name: string;
  version: string;
  title: string;
  description: string;
  publisher: string;
  visibility: "public" | "organization";
  sha256: string;
  yanked: boolean;
  installs: number;
  published_at: string;
}

export interface SolutionUsage {
  from: string;
  to: string;
  by: "end_user" | "workspace" | "day";
  tokens: number;
  cost_usd: number;
  conversations: number;
  runs: number;
  lines: { key: string; label?: string | null; conversations: number; runs: number; tokens: number; cost_usd: number }[];
}

export function searchRegistry(query?: string) {
  return apiFetch<RegistryPackage[]>(`/api/v1/registry/packages${query ? `?q=${encodeURIComponent(query)}` : ""}`);
}

export function getRegistryPackage(name: string, version = "latest") {
  return apiFetch<{ package: string; version: string; sha256: string; bundle: SolutionBundle }>(
    `/api/v1/registry/packages/${encodeURIComponent(name)}/${encodeURIComponent(version)}`);
}

export function publishInstalledSolution(installId: string, visibility: "organization" | "public") {
  return apiFetch<RegistryPackage>(`/api/v1/solutions/${encodeURIComponent(installId)}/publish`, { method: "POST", body: JSON.stringify({ visibility }) });
}

export function getSolutionUsage(installId: string, by: SolutionUsage["by"], from?: string, to?: string) {
  const q = new URLSearchParams({ by });
  if (from) q.set("from", from);
  if (to) q.set("to", to);
  return apiFetch<SolutionUsage>(`/api/v1/solutions/${encodeURIComponent(installId)}/usage?${q}`);
}

export function solutionUsageCsvUrl(installId: string, by: SolutionUsage["by"]) {
  return `${API_BASE}/api/v1/solutions/${encodeURIComponent(installId)}/usage?by=${by}&format=csv`;
}

/** A package as structured JSON, for the solution builder (the server reads the YAML). */
/** One turn of the builder's design conversation. */
export interface DesignTurn { role: "user" | "assistant"; content: string }

/** The designer's answer: a reply, and the draft its changes would make (validated, not saved). */
export interface SolutionDesignProposal {
  reply: string;
  summary: string;
  changes: string[];
  errors: string[];
  manifest: { [key: string]: import("./solutionPackage").Json } | null;
  skills: { name: string; description: string; instructions: string; workspace?: string | null }[] | null;
  problems: string[];
  warnings: string[];
  can_apply: boolean;
}

/** Asks the solution designer for changes to a draft, in plain language. Nothing is saved. */
export function designSolution(body: {
  manifest: { [key: string]: import("./solutionPackage").Json };
  skills: import("./solutionPackage").BuilderSkill[];
  history: DesignTurn[];
  request: string;
}) {
  return apiFetch<SolutionDesignProposal>("/api/v1/solutions/design", { method: "POST", body: JSON.stringify(body) });
}

export function parseSolution(bundle: SolutionBundle) {
  return apiFetch<{ manifest: { [key: string]: import("./solutionPackage").Json } | null; files: Record<string, string>; errors: string[] }>(
    "/api/v1/solutions/parse", { method: "POST", body: JSON.stringify(bundle) });
}

/** The package an install was deployed from. */
export function getSolutionPackage(idOrName: string) {
  return apiFetch<SolutionBundle>(`/api/v1/solutions/${encodeURIComponent(idOrName)}/package`);
}
