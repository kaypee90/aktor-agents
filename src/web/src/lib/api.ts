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

async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    ...init,
    headers: { "Content-Type": "application/json", ...init?.headers },
    cache: "no-store",
  });

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

  const source = new EventSource(url.toString());
  source.onmessage = (e) => {
    try {
      onEvent(JSON.parse(e.data));
    } catch {
      // ignore malformed frames
    }
  };
  return source;
}
