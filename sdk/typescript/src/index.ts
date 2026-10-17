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

// ---- Solutions (docs/solutions.md) ----

/** A solution package: the manifest's text (YAML or JSON) and the files it refers to, by relative path. */
export interface SolutionBundle {
  manifest: string;
  files?: Record<string, string>;
}

export interface SolutionDeploy {
  bundle: SolutionBundle;
  /** Input values (see the manifest's inputs). */
  inputs?: Record<string, string>;
  /** Connector name → secret field → value; only needed the first time a connector is added. */
  secrets?: Record<string, Record<string, string>>;
  /** Upgrades: allow the same or an older version. */
  force?: boolean;
  /** Upgrades: report what would change without changing anything. */
  dry_run?: boolean;
}

export interface SolutionValidation {
  ok: boolean;
  errors: string[];
  warnings: string[];
  summary: Record<string, unknown> | null;
  secrets: Record<string, { key: string; label: string; required: boolean; description?: string }[]> | null;
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

export interface SolutionRecord {
  id: string;
  version: number;
  created_at: string;
  updated_at: string;
  [field: string]: unknown;
}

export interface RecordList {
  entity: string;
  total: number;
  items: SolutionRecord[];
}

export interface EvalReport {
  install_id: string;
  version: string;
  passed: number;
  failed: number;
  results: { name: string; passed: boolean; failures: string[]; run_id?: string; status?: string; summary?: string; duration_ms?: number }[];
}

export interface AppMessage {
  id: string;
  role: "user" | "assistant";
  text: string;
  at: string;
  status?: string;
}

export interface AppConversation {
  conversation_id: string;
  title: string;
  state: string;
  answering: boolean;
  messages: AppMessage[];
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

  /** A request with extra headers (idempotency keys, If-Match). */
  async requestWithHeaders<T>(method: string, path: string, body: unknown, headers: Record<string, string>, idempotent: boolean): Promise<T> {
    for (let attempt = 0; ; attempt++) {
      let response: Response;
      try {
        response = await this.fetchImpl(this.baseUrl + path, { method, headers: { ...this.headers(true), ...headers }, body: JSON.stringify(body) });
      } catch (err) {
        if (idempotent && attempt < this.maxRetries) { await sleep(backoff(attempt)); continue; }
        throw err;
      }
      if ((response.status === 429 || response.status >= 500) && idempotent && attempt < this.maxRetries) {
        await sleep(retryAfter(response) ?? backoff(attempt));
        continue;
      }
      const text = await response.text();
      const parsed = text ? safeJson(text) : undefined;
      if (!response.ok) {
        throw new AktorError((parsed as { error?: string } | undefined)?.error ?? `${method} ${path} failed with HTTP ${response.status}`, response.status, parsed ?? text);
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

  /**
   * Solutions: business apps packaged as one unit (docs/solutions.md). Install and upgrade need an
   * Admin key; the `aktor` CLI wraps these calls.
   */
  readonly solutions = {
    validate: (bundle: SolutionBundle) => this.request<SolutionValidation>("POST", "/api/v1/solutions/validate", { body: bundle, idempotent: true }),
    install: (deploy: SolutionDeploy) => this.request<Record<string, unknown>>("POST", "/api/v1/solutions", { body: deploy }),
    list: (options: { all?: boolean } = {}) => this.request<SolutionSummary[]>("GET", "/api/v1/solutions", { query: { all: options.all } }),
    /** By install id or by name. */
    get: (idOrName: string) => this.request<Record<string, unknown>>("GET", `/api/v1/solutions/${enc(idOrName)}`),
    upgrade: (idOrName: string, deploy: SolutionDeploy) => this.request<Record<string, unknown>>("PUT", `/api/v1/solutions/${enc(idOrName)}`, { body: deploy }),
    uninstall: (idOrName: string, options: { purge?: boolean } = {}) =>
      this.request<void>("DELETE", `/api/v1/solutions/${enc(idOrName)}`, { query: { purge: options.purge } }),
    /** Runs the evals for real; resolves when they've all finished. */
    runEvals: (idOrName: string, names?: string[]) =>
      this.request<EvalReport>("POST", `/api/v1/solutions/${enc(idOrName)}/evals`, { body: { names } }),
    /** A token for one of your end users (call from your backend, after your own sign-in). */
    endUserToken: (idOrName: string, userId: string, options: { name?: string; ttlMinutes?: number } = {}) =>
      this.request<{ token: string; install_id: string; user_id: string; expires_at: string }>(
        "POST", `/api/v1/solutions/${enc(idOrName)}/end-user-tokens`, { body: { user_id: userId, name: options.name, ttl_minutes: options.ttlMinutes } }),

    /** What the solution cost, by end user, workspace or day (this calendar month by default). */
    usage: (idOrName: string, options: { by?: "end_user" | "workspace" | "day"; from?: string; to?: string } = {}) =>
      this.request<{ from: string; to: string; by: string; tokens: number; cost_usd: number; conversations: number; runs: number;
        lines: { key: string; conversations: number; runs: number; tokens: number; cost_usd: number }[] }>(
        "GET", `/api/v1/solutions/${enc(idOrName)}/usage`, { query: { by: options.by, from: options.from, to: options.to } }),

    /** Installs a registry package ("name", "name@1.2.0"); see `registry`. */
    installPackage: (reference: string, options: { inputs?: Record<string, string>; secrets?: Record<string, Record<string, string>> } = {}) =>
      this.request<Record<string, unknown>>("POST", "/api/v1/solutions", { body: { package: reference, inputs: options.inputs, secrets: options.secrets } }),

    /** The solution's records, from your backend. */
    records: {
      /** `where` matches fields exactly; `filters` are like "ends_on<=today+30d", "rent>=1000", "name~smith". */
      list: (idOrName: string, entity: string, options: { where?: Record<string, string>; filters?: string[]; orderBy?: string; desc?: boolean; limit?: number; offset?: number; endUser?: string } = {}) => {
        const params = new URLSearchParams();
        for (const [k, v] of Object.entries(options.where ?? {})) params.append(`where.${k}`, v);
        for (const f of options.filters ?? []) params.append("filter", f);
        if (options.orderBy) params.set("order_by", options.orderBy);
        if (options.desc !== undefined) params.set("desc", String(options.desc));
        if (options.limit !== undefined) params.set("limit", String(options.limit));
        if (options.offset !== undefined) params.set("offset", String(options.offset));
        if (options.endUser) params.set("end_user", options.endUser);
        const qs = params.toString();
        return this.request<RecordList>("GET", `/api/v1/solutions/${enc(idOrName)}/records/${enc(entity)}${qs ? "?" + qs : ""}`);
      },
      get: (idOrName: string, entity: string, id: string) =>
        this.request<SolutionRecord>("GET", `/api/v1/solutions/${enc(idOrName)}/records/${enc(entity)}/${enc(id)}`),
      /** Pass an idempotencyKey to make retries safe (the same key returns the same record). */
      create: (idOrName: string, entity: string, fields: Record<string, unknown>, options: { idempotencyKey?: string } = {}) =>
        this.requestWithHeaders<SolutionRecord>("POST", `/api/v1/solutions/${enc(idOrName)}/records/${enc(entity)}`, fields,
          options.idempotencyKey ? { "Idempotency-Key": options.idempotencyKey } : {}, !!options.idempotencyKey),
      /** Only the fields given change (null clears one). With expectedVersion, a stale write is refused (409). */
      update: (idOrName: string, entity: string, id: string, fields: Record<string, unknown>, options: { expectedVersion?: number } = {}) =>
        this.requestWithHeaders<SolutionRecord>("PATCH", `/api/v1/solutions/${enc(idOrName)}/records/${enc(entity)}/${enc(id)}`, fields,
          options.expectedVersion !== undefined ? { "If-Match": `"${options.expectedVersion}"` } : {}, true),
      delete: (idOrName: string, entity: string, id: string) =>
        this.request<void>("DELETE", `/api/v1/solutions/${enc(idOrName)}/records/${enc(entity)}/${enc(id)}`),
    },
  };

  /** The solution registry: publish your packages, find others' public ones (docs/solutions.md#the-registry). */
  readonly registry = {
    publish: (bundle: SolutionBundle, visibility: "organization" | "public" = "organization") =>
      this.request<Record<string, unknown>>("POST", "/api/v1/registry/packages", { body: { bundle, visibility } }),
    search: (query?: string) => this.request<{ package: string; name: string; version: string; title: string; description: string; publisher: string; visibility: string; installs: number }[]>(
      "GET", "/api/v1/registry/packages", { query: { q: query } }),
    versions: (name: string) => this.request<Record<string, unknown>[]>("GET", `/api/v1/registry/packages/${enc(name)}`),
    get: (name: string, version = "latest") => this.request<{ package: string; sha256: string; bundle: SolutionBundle }>("GET", `/api/v1/registry/packages/${enc(name)}/${enc(version)}`),
    yank: (name: string, version: string, options: { undo?: boolean; reason?: string } = {}) =>
      this.request<void>("POST", `/api/v1/registry/packages/${enc(name)}/${enc(version)}/yank`, { body: { yank: !options.undo, reason: options.reason }, idempotent: true }),
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

/**
 * The end-user side of a solution (docs/solutions.md#end-users): what your own customers' app
 * calls with an end-user token your backend got from `aktor.solutions.endUserToken`. It reaches
 * only that user's conversations and records.
 */
export class AktorApp {
  readonly baseUrl: string;
  private token: string;
  private readonly fetchImpl: typeof fetch;

  constructor(options: { installId: string; token: string; baseUrl?: string; fetch?: typeof fetch }) {
    this.installId = options.installId;
    this.token = options.token;
    this.baseUrl = (options.baseUrl ?? "http://localhost:5080").replace(/\/+$/, "");
    this.fetchImpl = options.fetch ?? globalThis.fetch.bind(globalThis);
  }

  readonly installId: string;

  /** Swap in a fresh token (they expire; your backend issues new ones). */
  setToken(token: string) {
    this.token = token;
  }

  private async call<T>(method: string, path: string, body?: unknown): Promise<T> {
    const response = await this.fetchImpl(`${this.baseUrl}/api/v1/apps/${enc(this.installId)}${path}`, {
      method,
      headers: { Accept: "application/json", Authorization: `Bearer ${this.token}`, ...(body !== undefined ? { "Content-Type": "application/json" } : {}) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await response.text();
    const parsed = text ? safeJson(text) : undefined;
    if (!response.ok) throw new AktorError((parsed as { error?: string } | undefined)?.error ?? `HTTP ${response.status}`, response.status, parsed ?? text);
    return parsed as T;
  }

  describe() {
    return this.call<{ app: string; assistant: string | null; greeting: string | null; user: { id: string; name: string | null }; expires_at: string }>("GET", "");
  }

  conversations = {
    list: () => this.call<{ conversation_id: string; title: string; created_at: string; updated_at: string }[]>("GET", "/conversations"),
    start: (message: string) => this.call<{ conversation_id: string; state: string }>("POST", "/conversations", { message }),
    get: (conversationId: string) => this.call<AppConversation>("GET", `/conversations/${enc(conversationId)}`),
    send: (conversationId: string, message: string) => this.call<{ conversation_id: string; state: string }>("POST", `/conversations/${enc(conversationId)}/messages`, { message }),
    /** Polls until the assistant has answered, then returns the conversation. */
    waitForReply: async (conversationId: string, options: { timeoutMs?: number; intervalMs?: number } = {}) => {
      const deadline = Date.now() + (options.timeoutMs ?? 120_000);
      for (;;) {
        const c = await this.conversations.get(conversationId);
        if (!c.answering) return c;
        if (Date.now() > deadline) throw new AktorError("Timed out waiting for the reply", 408);
        await sleep(options.intervalMs ?? 1000);
      }
    },
  };

  /** The user's own records of an entity that belongs to people. */
  records(entity: string, options: { limit?: number; offset?: number } = {}) {
    const q = new URLSearchParams();
    if (options.limit !== undefined) q.set("limit", String(options.limit));
    if (options.offset !== undefined) q.set("offset", String(options.offset));
    return this.call<RecordList>("GET", `/records/${enc(entity)}${q.toString() ? "?" + q : ""}`);
  }
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
