# Calling Aktor from other tools

Aktor doesn't try to replace n8n, CrewAI, OpenClaw or a coding agent. It's the **governed runtime
underneath** them: they hand an open-ended goal to Aktor, an agent team works on it under budgets
the server enforces, and the result comes back. There are four ways in, and all of them use the
same task service as `POST /api/tasks`:

| Way in | Endpoint | For |
|---|---|---|
| REST | `POST /api/tasks` | Scripts, the [TypeScript SDK](../sdk/typescript/README.md), n8n's HTTP Request node |
| MCP | `/mcp` (streamable HTTP) | Claude Code, n8n's MCP Client node, CrewAI and OpenClaw MCP tools, any MCP client |
| A2A | `/.well-known/agent-card.json`, `/a2a` | CrewAI and other agent frameworks that delegate over A2A |
| ACP | `/acp` (WebSocket) and the `aktor-acp` stdio bridge | OpenClaw and editors that run external agents over the Agent Client Protocol |

**No bypass.** Every way in authenticates with the same `ak_…` API keys (Settings → API keys), runs
as that key's organization, and goes through `TaskService`. A caller can't see another
organization's tasks, can't exceed the server's **task budget ceiling** (`TaskBudgetCeiling` in
`appsettings.json`: by default 5M tokens and $50 per task, whatever is asked for), and is subject to
the same plan quotas, spawn limits and team-shape policies as anyone else. Starting a task needs a
Member key; Viewer keys can read status and results.

**Traceability.** Every result carries:
- `correlation_id`: yours if you sent one (`correlation_id`, or the `X-Correlation-Id` header on
  REST), otherwise generated. It is stamped on every event of the run and its agents, so
  `GET /api/tasks/{id}/events` and the logs can be matched to the other system's run.
- `dashboard_url`: a link straight to the task's live graph (`Dashboard:BaseUrl`, from
  `APP_BASE_URL`).

## Finding out that a task finished

Tasks usually take minutes. Pick whichever fits the caller:

1. **Start, then poll.** Start the task, then call `get_task_status` (MCP) or
   `GET /api/tasks/{id}/wait?timeout_seconds=60` (REST) in a loop. Both are long polls: they answer
   as soon as the task finishes, or at the timeout with `done: false`.
2. **Completion webhook.** Pass `callback_url` (and optionally `callback_secret`). When the task
   finishes, Aktor POSTs:

   ```json
   { "event": "task.completed", "task_id": "…", "status": "Completed", "correlation_id": "…",
     "dashboard_url": "…", "completed_at": "…", "summary": "…", "result": { …TaskResult… } }
   ```

   - Headers: `X-Aktor-Event: task.completed`, `Idempotency-Key: task-completed-{id}`,
     `X-Correlation-Id`, and with a secret `X-Aktor-Signature: sha256=<hex HMAC-SHA256(secret, body)>`.
   - Delivery is durable. Failed deliveries are retried with backoff (up to
     `Tasks:Callbacks:MaxAttempts`, 6 by default), and a webhook missed while the server was down is
     sent after it restarts. Use the idempotency key to drop duplicates.
   - The secret is stored encrypted in the vault, never in the task table.
   - Webhooks to private addresses (localhost, the Docker network) are refused unless
     `CALLBACKS_ALLOW_PRIVATE_NETWORKS=true`, since a caller could otherwise probe the server's own
     network.
3. **Block with progress (MCP).** `run_goal` with `wait: true`, or `get_task_status` with
   `wait_seconds`, holds the call open and sends **MCP progress notifications** (agents finished out
   of agents started, plus what just happened) when the client supplied a progress token. The wait is
   capped by `Mcp:MaxWaitSeconds` (600); after that the call returns the current status and you
   poll again.

## MCP server

Endpoint: `POST {API}/mcp`, streamable HTTP, stateless (no session affinity, so it load-balances),
with header `Authorization: Bearer ak_…`.

| Tool | Arguments | Returns |
|---|---|---|
| `run_goal` | `goal`, `budget?` `{max_tokens, max_cost_usd, max_duration_seconds, max_children, max_tool_calls}`, `workspace?`, `wait?`, `callback_url?`, `callback_secret?`, `correlation_id?`, `team_policy?` | The task (`task_id`, `state`, `correlation_id`, `dashboard_url`, `budget`…) |
| `get_task_status` | `task_id`, `wait_seconds?` | The task: `state` is `working`, `completed`, `failed` or `canceled`; `done` is true once it's finished |
| `get_task_result` | `task_id` | `ready`, and the aggregated `result`: summary, findings, artifacts, unresolved items, metrics |
| `cancel_task` | `task_id` | The task, after every agent is stopped |
| `list_agents` | `task_id` | The team: id, role, status, parent and depth of each agent |

**Workspaces.** With `workspace: "ws-…"`, `run_goal` starts a run of that workspace's pipeline with
the goal as its input, instead of a new task. The run uses the workspace's connections, safety
policy (approvals included) and daily budget. A run is a task: the returned id (`run-…`) works with
`get_task_status`, `get_task_result`, `list_agents` and `cancel_task`, and its result is the
pipeline's output. A run beyond the pipeline's runs-at-once limit waits in a queue first.

### Claude Code

```bash
claude mcp add --transport http aktor http://localhost:5080/mcp \
  --header "Authorization: Bearer ak_your_key"
```

Then ask, for example: *"Use aktor's run_goal to research the property management SaaS market with
a $3 budget, wait for it, and summarize the result."*

### n8n

- **MCP Client Tool node** (in an AI Agent workflow): set the SSE/HTTP endpoint to
  `http://api:8080/mcp` from inside the Compose network, or your public URL, choose *Header Auth* with
  `Authorization: Bearer ak_…`, and expose `run_goal`, `get_task_status` and `get_task_result` to
  the agent.
- **Without an AI agent:** use an HTTP Request node to `POST /api/tasks` with a `callback_url`
  pointing at a Webhook node in a second workflow. The webhook receives the result when the task
  finishes. Check `X-Aktor-Signature` with a Crypto node (HMAC-SHA256, hex).

### CrewAI and OpenClaw over MCP

Both can use MCP servers as tools. Point them at `/mcp` with the same header, then let the crew or
agent call `run_goal` with `wait: true` (or poll with `get_task_status`).

## A2A agent

Aktor is an [A2A](https://a2a-protocol.org) agent, so agent frameworks that delegate over A2A (CrewAI
and anything built on the A2A SDKs) can hand it a goal.

- **Agent card:** `GET /.well-known/agent-card.json` (also `/.well-known/agent.json`) is public.
  It describes one skill, `run-goal`, streaming support, and bearer auth with an API key.
- **Endpoint:** `POST /a2a`, JSON-RPC 2.0, `Authorization: Bearer ak_…`.
- **Versions.** Both protocol versions in use are spoken, chosen by the client's `A2A-Version`
  header (or query parameter):
  - **1.0:** `SendMessage`, `SendStreamingMessage`, `GetTask`, `CancelTask`, `SubscribeToTask`,
    `TASK_STATE_*` states, `ROLE_*` roles, parts like `{"text": …}` / `{"data": …}`, and stream
    events `{"statusUpdate": …}` / `{"artifactUpdate": …}`.
  - **0.3 (no header):** `message/send`, `message/stream`, `tasks/get`, `tasks/cancel`,
    `tasks/resubscribe`, lowercase states, and `"kind"` discriminators.
  - Method names from either version work under either.

| A2A | Aktor |
|---|---|
| A message's text parts (data parts as JSON) | The task's goal |
| `message.metadata.aktor` (or `metadata`): `budget`, `workspace`, `correlation_id`, `team_policy` | The same options as MCP `run_goal` |
| `contextId` | The correlation id; follow-up tasks in one context share it |
| A message with the `taskId` of a finished task | A new task that builds on that task's result |
| Blocking send (0.3 `blocking`, default true; 1.0 unless `returnImmediately`) | Waits up to `A2a:MaxWaitSeconds` (600), then returns the task still working |
| Task `status.message` | The final summary |
| Artifact `task_result` | A text part (summary, findings, dashboard link) and a data part (the full `TaskResult` JSON) |
| `metadata.aktor` | `dashboard_url`, `correlation_id`, agents, tokens, cost |

Streaming sends:
1. the task;
2. a status update for each step of the team's progress;
3. the result artifact;
4. a final status (`final: true` in 0.3).

Errors use the A2A codes: `-32001` task not found (including another organization's), `-32002`
not cancelable, `-32004` unsupported.

**From a CrewAI crew:** add Aktor as a remote A2A agent with the agent card URL
`https://your-aktor/.well-known/agent-card.json` and the API key as the bearer token. Then delegate
goals to it; the crew receives the `TaskResult` as the delegated task's artifact.

## ACP agent

OpenClaw (through `acpx`), Zed and other editors run external agents with the
[Agent Client Protocol](https://agentclientprotocol.com) (protocol version 1). They launch the agent
as a local process and talk JSON-RPC over its stdin and stdout. Aktor ships that process:

```bash
# Node 22+. Relays stdio to the server's /acp WebSocket, authenticated with the key.
AKTOR_URL=https://your-aktor AKTOR_API_KEY=ak_… npx aktor-acp      # sdk/typescript/bin/aktor-acp.mjs
acpx --agent "npx aktor-acp" "Research the market for AI property management software"
```

In OpenClaw, register a custom ACP agent whose command is `npx aktor-acp`, with `AKTOR_URL` and
`AKTOR_API_KEY` in its environment.

The server side is `GET /acp`, a WebSocket that takes `Authorization: Bearer ak_…`:

| ACP | Aktor |
|---|---|
| `initialize` | Protocol 1; text and embedded-context prompts; no session loading |
| `session/new` | A session; its id is the correlation id of its tasks. MCP servers the client offers are ignored: Aktor's agents use their own governed tools |
| `session/prompt` | Starts a task from the prompt's text, and embedded resources. A later prompt in the session builds on the previous result |
| `session/update` | `agent_message_chunk` when the task starts (with its dashboard link) and at the end (summary, findings, cost); a `tool_call` per agent spawned, updated when it finishes; a `plan` with one entry per agent |
| Prompt result | `stopReason: end_turn`, or `cancelled` |
| `session/cancel` | Stops every agent of the running task |

## Tests

`McpServerTests` runs the real API against a throwaway Postgres container:
- an MCP client starts a task, long-polls it with progress, and reads the result and the team;
- `run_goal` with `wait` blocks until the task is done, and reports progress;
- another organization's key gets "No task" from every tool, and 404 over REST;
- MCP without a key gets 401, and a Viewer key can't start tasks;
- a budget above the ceiling is cut down to it;
- the completion webhook arrives with a valid signature.

`A2aAcpTests`, against the same kind of host:
- the agent card is public and versioned;
- a blocking 0.3 `message/send` returns the completed task with its result artifact;
- a 1.0 `SendMessage` returns at once and `GetTask` polls it to `TASK_STATE_COMPLETED`, and a
  finished task can't be cancelled;
- `message/stream` sends the task, progress, the artifact and a final status;
- another organization gets "task not found", and calls without a key get 401;
- an ACP session runs a prompt as a task, streams the team, and handles a follow-up prompt;
- `session/cancel` ends a prompt as `cancelled`;
- ACP without a key is refused.
