/**
 * @aktor/sdk: a typed client for the Aktor Agents platform API (docs/platform.md).
 *
 *   const aktor = new Aktor({ apiKey: process.env.AKTOR_API_KEY!, baseUrl: "https://agents.example.com" });
 *   const { workspace_id } = await aktor.workspaces.create({ name: "Support", goal: "Triage new tickets..." });
 *   for await (const event of aktor.events.stream({ signal })) console.log(event.summary);
 *
 * No dependencies: it uses the platform's fetch (Node 18+, Deno, Bun, browsers).
 */

export type Role = "Viewer" | "Member" | "Admin" | "Owner";
export type AutonomyLevel = "Autonomous" | "SemiAutonomous" | "Supervised";
export type ApprovalStatus = "Pending" | "Approved" | "Rejected" | "Expired";

export interface ChatEntry {
  seq: number;
  at: string;
  author_kind: "User" | "Agent" | "System";
  author_id: string;
  author_name: string;
  text: string;
  urgency: string;
}

export interface WorkspaceAgent {
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

export interface Trigger {
  trigger_id: string;
  kind: "Schedule" | "Webhook" | "Watch";
  name: string;
  instruction: string;
  target_agent_id: string | null;
  interval_seconds: number | null;
  cron: string | null;
  enabled: boolean;
  [key: string]: unknown;
}

export interface Approval {
  approval_id: string;
  code: string;
  agent_id: string;
  agent_name: string;
  tool_name: string;
  side_effects: string;
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

export interface SafetyRule {
  id?: string;
  name?: string;
  /** Tool-name glob, e.g. "billing__*" or "*__send_sms". */
  tool_pattern: string;
  applies: "Any" | "Writes" | "Unsafe";
  decision: "Allow" | "Deny" | "RequireApproval";
}

export interface SafetyPolicy {
  autonomy: AutonomyLevel;
  rules: SafetyRule[];
  approval_timeout_hours: number;
}

export interface Workspace {
  workspace_id: string;
  tenant_id: string;
  name: string;
  goal: string;
  status: "Active" | "Paused" | "Archived";
  created_at: string;
  updated_at: string;
  coordinator_agent_id: string;
  conversation: ChatEntry[];
  triggers: Trigger[];
  agents: WorkspaceAgent[];
  daily_token_limit: number;
  daily_cost_limit_usd: number;
  tokens_today: number;
  cost_today: number;
  total_tokens: number;
  total_cost_usd: number;
  safety_policy?: SafetyPolicy;
  approvals?: Approval[];
  [key: string]: unknown;
}

export interface WorkspaceSummary {
  workspace_id: string;
  name: string;
  goal: string;
  status: string;
  agents: number;
  triggers: number;
  total_tokens: number;
  total_cost_usd: number;
  created_at: string;
  updated_at: string;
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
  previous_hash: string;
  hash: string;
}

export interface RuntimeEvent {
  event_id: string;
  type: string;
  timestamp: string;
  agent_id: string | null;
  parent_agent_id: string | null;
  target_agent_id: string | null;
  task_id: string | null;
  tenant_id: string | null;
  summary: string;
  data: Record<string, string>;
}

export interface Plan {
  id: string;
  name: string;
  monthly_token_limit: number;
  monthly_cost_limit_usd: number;
  max_workspaces: number;
  max_active_agents: number;
  max_members: number;
  price_monthly_usd: number;
}

export interface Usage {
  period: string;
  tokens: number;
  cost_usd: number;
  llm_calls: number;
  tool_calls: number;
  agents_created: number;
}

export interface Billing {
  billing_enabled: boolean;
  plan: Plan;
  usage: Usage;
  history: Usage[];
  quota: { allowed: boolean; reason: string | null };
  paused_agents: number;
  plans: Plan[];
}

export interface Me {
  authenticated: boolean;
  tenant_id?: string;
  role?: Role;
  via?: "session" | "api_key" | "disabled";
}

export interface ScheduleTrigger {
  kind: "schedule";
  name: string;
  instruction: string;
  target_agent_id?: string;
  every_minutes?: number;
  /** 5-field cron, UTC. */
  cron?: string;
}

export interface WebhookTrigger {
  kind: "webhook";
  name: string;
  instruction: string;
  target_agent_id?: string;
}

export interface WatchTrigger {
  kind: "watch";
  name: string;
  /** A read-only connection tool, e.g. "billing__get". */
  source_tool: string;
  source_arguments?: Record<string, unknown>;
  items_path?: string;
  conditions: { field: string; op: string; value?: string }[];
  key_field?: string;
  display_fields?: string[];
  every_minutes?: number;
  mode?: "notify" | "wake_agent";
  urgency?: "info" | "warning" | "urgent";
  message?: string;
  instruction?: string;
  target_agent_id?: string;
}

export class AktorError extends Error {
  constructor(message: string, readonly status: number, readonly body?: unknown) {
    super(message);
    this.name = "AktorError";
  }
}

export interface AktorOptions {
  /** An API key from Settings → API keys ("ak_…"). Omit on a server running with auth disabled. */
  apiKey?: string;
  /** The API server, e.g. "http://localhost:5080". */
  baseUrl?: string;
  /** Retries for rate limits, 5xx and network errors on requests that are safe to repeat. Default 2. */
  maxRetries?: number;
  fetch?: typeof fetch;
}

type Query = Record<string, string | number | boolean | undefined | null>;

export class Aktor {
  readonly baseUrl: string;
  private readonly apiKey?: string;
  private readonly maxRetries: number;
  private readonly fetchImpl: typeof fetch;

  constructor(options: AktorOptions = {}) {
    this.baseUrl = (options.baseUrl ?? "http://localhost:5080").replace(/\/+$/, "");
    this.apiKey = options.apiKey;
    this.maxRetries = options.maxRetries ?? 2;
    this.fetchImpl = options.fetch ?? globalThis.fetch.bind(globalThis);
  }

  /** Low-level request. `idempotent` requests are retried on 429/5xx/network errors. */
  async request<T>(method: string, path: string, options: { body?: unknown; query?: Query; idempotent?: boolean; signal?: AbortSignal } = {}): Promise<T> {
    const url = new URL(this.baseUrl + path);
    for (const [k, v] of Object.entries(options.query ?? {})) if (v !== undefined && v !== null && v !== "") url.searchParams.set(k, String(v));
    const idempotent = options.idempotent ?? (method === "GET" || method === "PUT" || method === "DELETE");

    for (let attempt = 0; ; attempt++) {
      let response: Response;
      try {
        response = await this.fetchImpl(url, {
          method,
          headers: this.headers(options.body !== undefined),
          body: options.body === undefined ? undefined : JSON.stringify(options.body),
          signal: options.signal,
        });
      } catch (err) {
        if (idempotent && attempt < this.maxRetries && !options.signal?.aborted) {
          await sleep(backoff(attempt));
          continue;
        }
        throw err;
      }

      if ((response.status === 429 || response.status >= 500) && idempotent && attempt < this.maxRetries) {
        await sleep(retryAfter(response) ?? backoff(attempt));
        continue;
      }

      if (response.status === 204) return undefined as T;
      const text = await response.text();
      const parsed = text ? safeJson(text) : undefined;
      if (!response.ok) {
        const message = (parsed as { error?: string } | undefined)?.error ?? `${method} ${path} failed with HTTP ${response.status}`;
        throw new AktorError(message, response.status, parsed ?? text);
      }

      return parsed as T;
    }
  }

  private headers(json: boolean): Record<string, string> {
    const h: Record<string, string> = { Accept: "application/json" };
    if (json) h["Content-Type"] = "application/json";
    if (this.apiKey) h.Authorization = `Bearer ${this.apiKey}`;
    return h;
  }

  /** Who the key belongs to. */
  me() {
    return this.request<Me>("GET", "/api/auth/me");
  }

  readonly workspaces = {
    list: () => this.request<WorkspaceSummary[]>("GET", "/api/workspaces"),

    get: (workspaceId: string) => this.request<Workspace>("GET", `/api/workspaces/${enc(workspaceId)}`),

    /** Creates a workspace; its coordinator starts on the goal immediately. */
    create: (input: { name: string; goal: string; daily_token_limit?: number; daily_cost_limit_usd?: number }) =>
      this.request<{ workspace_id: string }>("POST", "/api/workspaces", { body: input }),

    /**
     * Gives the workspace's agents an instruction (to the coordinator, or to `toAgentId`).
     * Safe to retry: the message is delivered once per `clientMessageId` (one is generated).
     */
    send: (workspaceId: string, text: string, options: { toAgentId?: string; clientMessageId?: string } = {}) =>
      this.request<ChatEntry>("POST", `/api/workspaces/${enc(workspaceId)}/messages`, {
        body: { text, to_agent_id: options.toAgentId, client_message_id: options.clientMessageId ?? randomId() },
        idempotent: true,
      }),

    pause: (workspaceId: string) => this.request<void>("POST", `/api/workspaces/${enc(workspaceId)}/pause`, { idempotent: true }),
    resume: (workspaceId: string) => this.request<void>("POST", `/api/workspaces/${enc(workspaceId)}/resume`, { idempotent: true }),
    archive: (workspaceId: string) => this.request<void>("POST", `/api/workspaces/${enc(workspaceId)}/archive`, { idempotent: true }),

    setBudget: (workspaceId: string, budget: { daily_token_limit?: number; daily_cost_limit_usd?: number }) =>
      this.request<void>("PUT", `/api/workspaces/${enc(workspaceId)}/budget`, { body: budget }),

    triggers: {
      list: (workspaceId: string) => this.request<Trigger[]>("GET", `/api/workspaces/${enc(workspaceId)}/triggers`),
      /** For a webhook, the response includes its secret path: the only time the API returns it. */
      add: (workspaceId: string, trigger: ScheduleTrigger | WebhookTrigger | WatchTrigger) =>
        this.request<Record<string, unknown>>("POST", `/api/workspaces/${enc(workspaceId)}/triggers`, { body: trigger }),
      remove: (workspaceId: string, triggerId: string) =>
        this.request<void>("DELETE", `/api/workspaces/${enc(workspaceId)}/triggers/${enc(triggerId)}`),
    },

    policy: {
      get: (workspaceId: string) => this.request<SafetyPolicy>("GET", `/api/workspaces/${enc(workspaceId)}/policy`),
      update: (workspaceId: string, policy: SafetyPolicy) =>
        this.request<SafetyPolicy>("PUT", `/api/workspaces/${enc(workspaceId)}/policy`, { body: policy }),
    },

    approvals: {
      list: (workspaceId: string, status?: ApprovalStatus) =>
        this.request<Approval[]>("GET", `/api/workspaces/${enc(workspaceId)}/approvals`, { query: { status } }),
      /** Approve or reject by id or code ("A3"). A decision is final. */
      decide: (workspaceId: string, approvalIdOrCode: string, approve: boolean, reason?: string) =>
        this.request<{ message: string }>("POST", `/api/workspaces/${enc(workspaceId)}/approvals/${enc(approvalIdOrCode)}/decision`, {
          body: { approve, reason },
          idempotent: true, // deciding twice is refused, never applied twice
        }),
    },

    audit: {
      list: (workspaceId: string, filter: { actor?: string; action?: string; q?: string; since?: string; before?: number; limit?: number } = {}) =>
        this.request<AuditEntry[]>("GET", `/api/workspaces/${enc(workspaceId)}/audit`, { query: filter }),
      verify: (workspaceId: string) =>
        this.request<{ valid: boolean; records: number; first_broken_seq: number | null; message: string }>(
          "GET", `/api/workspaces/${enc(workspaceId)}/audit/verify`),
    },

    /** Polls until `predicate` holds (e.g. an agent replied), or throws after `timeoutMs`. */
    waitFor: async (workspaceId: string, predicate: (w: Workspace) => boolean, options: { timeoutMs?: number; intervalMs?: number } = {}) => {
      const deadline = Date.now() + (options.timeoutMs ?? 60_000);
      for (;;) {
        const w = await this.workspaces.get(workspaceId);
        if (predicate(w)) return w;
        if (Date.now() > deadline) throw new AktorError("Timed out waiting for the workspace", 408);
        await sleep(options.intervalMs ?? 1000);
      }
    },
  };

  readonly tasks = {
    create: (goal: string) => this.request<{ task_id: string; root_agent_id: string }>("POST", "/api/tasks", { body: { goal } }),
    list: () => this.request<{ task_id: string; goal: string; status: string; created_at: string }[]>("GET", "/api/tasks"),
    get: (taskId: string) => this.request<Record<string, unknown>>("GET", `/api/tasks/${enc(taskId)}`),
    result: (taskId: string) => this.request<{ ready: boolean; result?: Record<string, unknown> }>("GET", `/api/tasks/${enc(taskId)}/result`),
    cancel: (taskId: string) => this.request<void>("POST", `/api/tasks/${enc(taskId)}/cancel`, { idempotent: true }),
  };

  readonly agents = {
    list: () => this.request<{ agent_id: string; role: string; goal: string; status: string; root_agent_id: string }[]>("GET", "/api/agents"),
    get: (agentId: string) => this.request<Record<string, unknown>>("GET", `/api/agents/${enc(agentId)}`),
  };

  readonly organization = {
    get: () => this.request<{ tenant_id: string; name: string; plan: Plan; members: number }>("GET", "/api/organization"),
    members: () => this.request<{ user_id: string; email: string; name: string; role: Role }[]>("GET", "/api/organization/members"),
  };

  readonly billing = {
    /** The plan, this month's metered usage and the quota. */
    get: () => this.request<Billing>("GET", "/api/billing"),
  };

  readonly events = {
    /**
     * Live events for the key's organization (optionally one task/workspace/world), as an async
     * iterator. Reconnects on dropped connections until `signal` aborts.
     */
    stream: (options: { taskId?: string; signal?: AbortSignal; reconnect?: boolean } = {}): AsyncIterable<RuntimeEvent> => {
      const self = this;
      return {
        async *[Symbol.asyncIterator]() {
          const url = new URL(self.baseUrl + "/ws/events");
          if (options.taskId) url.searchParams.set("taskId", options.taskId);
          for (let attempt = 0; !options.signal?.aborted; attempt++) {
            let response: Response;
            try {
              response = await self.fetchImpl(url, { headers: { ...self.headers(false), Accept: "text/event-stream" }, signal: options.signal });
            } catch (err) {
              if (options.signal?.aborted) return;
              if (options.reconnect === false) throw err;
              await sleep(backoff(attempt));
              continue;
            }

            if (!response.ok || !response.body) {
              const text = await response.text().catch(() => "");
              throw new AktorError((safeJson(text) as { error?: string } | undefined)?.error ?? `event stream failed with HTTP ${response.status}`, response.status);
            }

            attempt = 0;
            try {
              for await (const data of sseData(response.body)) {
                const evt = safeJson(data);
                if (evt) yield evt as RuntimeEvent;
              }
            } catch (err) {
              if (options.signal?.aborted) return;
              if (options.reconnect === false) throw err;
            }

            if (options.reconnect === false) return;
            await sleep(backoff(0));
          }
        },
      };
    },
  };
}

async function* sseData(body: ReadableStream<Uint8Array>): AsyncGenerator<string> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) return;
      buffer += decoder.decode(value, { stream: true });
      let boundary: number;
      while ((boundary = buffer.search(/\r?\n\r?\n/)) >= 0) {
        const frame = buffer.slice(0, boundary);
        buffer = buffer.slice(boundary).replace(/^\r?\n\r?\n/, "");
        const data = frame.split(/\r?\n/).filter((l) => l.startsWith("data:")).map((l) => l.slice(5).trimStart()).join("\n");
        if (data) yield data;
      }
    }
  } finally {
    reader.releaseLock();
  }
}

const enc = encodeURIComponent;
const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));
const backoff = (attempt: number) => Math.min(8000, 250 * 2 ** attempt) + Math.floor(Math.random() * 100);

function retryAfter(response: Response): number | undefined {
  const value = response.headers.get("retry-after");
  const seconds = value ? Number(value) : NaN;
  return Number.isFinite(seconds) ? Math.min(30_000, seconds * 1000) : undefined;
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return undefined;
  }
}

function randomId() {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

export default Aktor;
