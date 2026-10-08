# Roadmap

Priorities follow from where Aktor differs from CrewAI, n8n and OpenClaw.

- **CrewAI and n8n:** a human designs the team. Agents only delegate to agents defined in advance.
- **OpenClaw:** does spawn sub-agents recursively and lets agents message each other. It's built
  for a single trusted operator, though: one trust domain per Gateway, no billing plane, and
  limits on time and concurrency rather than on spend.

Aktor's position is **governed agent teams for organizations, on a shared server**:

- token and dollar budgets enforced by the runtime, with each child's budget carved from its
  parent's remaining budget;
- child permissions that are always a subset of the parent's;
- specialist roles that agents can create at runtime;
- tenant isolation, quotas and billing inside one runtime.

The plan is to:

- make that position reliable and easy to prove;
- let other tools call Aktor instead of competing with them;
- close the gaps that would cost deals, with memory first.

Non-goals:

- a visual workflow builder (n8n);
- hand-written crew definitions (CrewAI);
- personal-assistant features, consumer chat channels and device apps (OpenClaw);
- competing on integration count.

Open question: are we targeting developers embedding a runtime, or teams wanting a finished
product? If it's the latter, move P9 (flagship use case) ahead of P2.

Change log:

- 2026-10-08: **Studies** replace the Simulation page: research on a question with the study's own
  datasets, documents and connections, statistics in a sandboxed Python container, reviewed models,
  a sealed holdout, simulated experiments built from the data, and reports whose findings cite
  evidence ([studies.md](studies.md)). Also: built-in model prices with a model dropdown,
  token-kind and usage analytics, switching a finished task's model, `@knowledge` mentions,
  deleting knowledge, and cloning, templating and deleting workspaces.

- 2026-10-03: direction set: a finished **productivity product** where people do their work through
  agent teams, used like ChatGPT, Claude or Gemini (this answers the open question above). Built:
  tasks as conversations with follow-ups that reopen a finished task, continuing a partial result
  with more budget, attachments of any type, `create_document` (Word, PDF, Excel, PowerPoint, CSV,
  Markdown), in-place previews, and shared memory from files. See [tasks.md](tasks.md).

- 2026-10-01: P1–P10 implemented (see each item's doc: integrations.md, preview.md, safety.md#team-shape,
  memory.md, replay.md, evals.md, guarantees.md, incident-response.md; Gemini in ProviderContractTests).
  Verified in-repo against protocol-level clients: the MCP C# SDK client, the official A2A Python SDK (1.2.1),
  and an ACP JSON-RPC client over the WebSocket. Still to verify by hand with the real products: an n8n
  workflow, Claude Code, a CrewAI crew and OpenClaw/acpx.

- 2026-10-01: reprioritized after comparing with OpenClaw.
  - Budget and team governance (P2, P3) and vector memory (P4) moved up.
  - The A2A endpoint now also covers ACP (P5).

---

## P1. Expose Aktor as an MCP server

**Why:** lets n8n (MCP Client, "Message an Agent"), CrewAI (MCP), OpenClaw (MCP) and coding agents
hand open-ended goals to Aktor. This puts Aktor inside their ecosystems as the governed runtime
underneath. It's the quickest way to reach more users.

**Scope**
- MCP server endpoint (streamable HTTP) on the API, authenticated with existing `ak_…` API keys.
- Tools: `run_goal` (goal, budget, workspace?) → task id; `get_task_status`; `get_task_result`;
  `cancel_task`; `list_agents` (for a task).
- Tasks can run for minutes. The design must cover how callers find out a task has finished:
  start-then-poll, a completion webhook, and MCP progress notifications where the client
  supports them.
- Budgets and the safety policy apply exactly as they do for `POST /api/tasks`. MCP callers get no
  bypass.
- Every result includes the correlation ID and a link to the task's dashboard view, so failures
  can be traced across both systems.

**Done when:**
- an n8n workflow and Claude Code can both start a task, wait for it and read the final result;
- an integration test covers the tool round-trip and checks that tenant isolation holds.

**Touches:** `AgentRuntime.Api/Controllers/TasksController.cs`, `AgentRuntime.Api/Platform`,
`sdk/typescript` (docs only).

## P2. Cost and team preview before a run

**Why:** budget enforcement is one of Aktor's clearest advantages: neither OpenClaw nor CrewAI
enforces spend limits that pass down to children. Emergent teams also make buyers nervous ("will it
spawn 40 agents and spend $50?"). Showing the expected cost up front addresses both.

**Scope**
- `POST /api/tasks/preview`: one cheap planning call returns:
  - the planned team shape (roles, depth);
  - an estimated token and dollar range;
  - expected duration.
- The dashboard shows the preview, with a confirm step when the estimate is above a configurable
  threshold.
- During the run, the dashboard shows spend against budget for each branch of the agent tree.
- After the run, record estimate vs. actual so later estimates can be calibrated.

**Done when:** the preview appears for every new task, and estimate vs. actual is visible in the
task's metrics.

**Touches:** `AgentRuntime/Resources/BudgetGuard.cs`, `AgentRuntime/LLM/AgentPromptBuilder.cs`,
`web/`.

## P3. Team-shape policies

**Why:** today, team size is shaped by prompting (see the recent right-sizing commits). Like every
other limit, it should be enforced by the runtime. OpenClaw's spawn controls are global config
(allowlists, depth, concurrency). Policies tied to each task and workspace, and derived from the
parent, are part of Aktor's governance story.

**Scope**
- Policy per workspace or per task:
  - max agents by goal type;
  - which roles or capabilities may spawn;
  - max fan-out per level;
  - a stop rule for duplicate roles (no two live agents with equivalent role and goal).
- Enforced in spawn validation alongside `MAX_AGENT_DEPTH` and the other limits. A rejection is
  returned to the agent as a structured tool error.

**Done when:** unit tests cover each rule, and an integration test shows a duplicate-role spawn
being rejected.

**Touches:** `AgentRegistryGrain` spawn validation, `AgentRuntime/Safety/SafetyContracts.cs`,
`AgentRuntime/Tools/GovernanceTools.cs`.

## P4. Vector memory (pgvector)

**Why:** memory is now the clearest gap against both competitors that have strong memory:
- CrewAI: embeddings, scoped, deduplicated;
- OpenClaw: hybrid keyword and vector search, active recall, background consolidation.

pgvector fits the existing Postgres setup.

**Scope**
- Add embeddings behind `IMemoryStore` through a pluggable embedding provider (Ollama, OpenAI).
- Hybrid search: keyword plus vector similarity, ranked with recency.
- Tenant and agent scoping preserved.
- Follow-up, out of scope for now: consolidation (deduplicating and merging similar entries).

**Done when:**
- shared-knowledge recall finds semantically related entries in an integration test;
- the keyword path still works when no embedding provider is configured;
- tenant isolation is covered by a test.

**Touches:** `AgentRuntime/Memory/IMemoryStore.cs`,
`AgentRuntime.Infrastructure/Memory/PostgresMemoryStore.cs`, `docker-compose.yml` (pgvector image).

## P5. Expose Aktor as an A2A agent and an ACP endpoint

**Why:** the same idea as P1, using agent-to-agent protocols:
- A2A reaches CrewAI and other agent frameworks.
- ACP reaches OpenClaw, which can run external agents over ACP. An OpenClaw user could then hand
  governed, multi-tenant team work to Aktor.

**Scope:**
- An A2A agent card plus A2A task endpoints, mapped onto the same task service as P1.
- An ACP endpoint on the same task service.
- Results stream back from the event stream.

**Done when:**
- a CrewAI crew delegates to Aktor through A2A and receives the final `TaskResult`;
- OpenClaw runs an Aktor task over ACP.

## P6. Replay a run from the step journal

**Why:** the journal already stores every LLM decision and tool result. Replaying a run with those
recorded responses gives debugging and audit at the level of individual agent steps.

**Scope**
- An `ILLMProvider` that serves recorded responses from a past task's journal, keyed by agent and
  step.
- Recorded tool results are served instead of re-executing tools, so replay never causes external
  side effects.
- Two modes:
  - full replay (should be identical to the original run);
  - replay up to step N, then continue live ("fork").
- Dashboard: step-through view and a diff between two runs.

**Done when:** replaying a finished demo task produces the same agent tree, messages and result.
The fork mode works from any step.

**Touches:** `AgentRuntime.Infrastructure/Llm`, `AgentRuntime/Durability`, `web/`.

## P7. Eval harness

**Why:** turns "emergent" from a risk into something we can measure, and catches regressions when
prompts or models change.

**Scope:** a CLI or test project that runs a goal N times (live or through P6 replay) and reports:
- team size and depth;
- cost and duration;
- completion rate;
- output quality (an LLM-judged rubric).

It also compares results across models and prompt versions.

**Done when:** a baseline report exists for both demo scenarios and runs in CI against the mock
provider.

**Depends on:** P6 (for deterministic runs).

## P8. Guarantees page

**Why:** an exact list of what the runtime guarantees persuades engineering buyers. It also makes
the difference from OpenClaw concrete, since OpenClaw has one trust domain per Gateway, while
Aktor isolates tenants inside one runtime.

**Scope:** `docs/guarantees.md`, a single table covering:
- durability: link to `durability.md`;
- side effects: the idempotent vs. non-idempotent handling;
- budget inheritance;
- intersection of permissions between parent and child;
- tenant isolation (messaging, discovery, memory, SQL schema);
- audit-chain integrity;
- spawn limits;
- sandboxed execution by default.

For each guarantee, state how it's enforced and which test proves it.

**Done when:** every row links to an enforcing class and a test. The README links to the page.

## P9. Flagship use case: ops and incident investigation

**Why:** this needs open-ended work, long-running monitors and approval-gated fixes all at once,
which is exactly where emergent teams, durability and governance meet. It builds on workspaces,
approvals and the audit log, which already exist. It's also an organizational, multi-user use
case, which plays to Aktor's strengths rather than OpenClaw's.

**Scope**
- A workspace template: monitors wake on a webhook or watch, spawn investigators (logs, metrics,
  recent deploys) and propose a fix behind a `SemiAutonomous` approval.
- Write an incident report artifact.
- A scripted mock scenario for demos and a live-LLM version.

**Done when:** an end-to-end demo runs from the dashboard, and a matching e2e test runs on the
mock provider.

## P10. Gemini provider: finish or remove

**Why:** the README currently advertises a stub (`AgentRuntime.Infrastructure/Llm/GeminiProvider.cs`).

**Scope:** implement tool calling plus usage accounting to match the Anthropic and OpenAI
providers, or remove the provider and its README mention.

**Done when:** provider contract tests pass, or the stub is gone.
