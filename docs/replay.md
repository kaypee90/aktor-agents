# Replay and the step journal

Every task run is recorded step by step in the **step journal** (`JournalSteps` in Postgres):
- **LLM steps:** each decision the model made, including its tool calls, text and token usage;
- **tool steps:** each tool call's result.

Steps are keyed by the agent's **position in the tree**, not its id. The root is `r`, and each
spawned agent is `{parent path}/{spawn call id}`, so the same agent can be found in a re-run where
every id is new. Recording is best-effort: if the journal can't be written, the run continues but
can't be replayed.

## Replaying a run

`POST /api/tasks/{id}/replay` starts a new task with the same goal and budget, whose agents take
their decisions from the original's journal.

| Mode | Model calls | External tools | Use |
|---|---|---|---|
| `full` | none | none, recorded results are served | Debugging and audit: reproduce exactly what happened |
| `fork` + `fork_after_step` | live after the step | live after the step | "What if?": replay up to step N, then let the model continue |

**What runs again and what doesn't.** Runtime actions are re-run, so the replay rebuilds the same
agent tree, messages and files:
- spawning, messaging, discovery and completing;
- reading memory;
- the task's own sandboxed files.

Everything that reaches outside the platform is answered from the journal instead: web search,
shell, HTTP, databases, connections, notifications, and writes to shared memory. **A full replay
never causes an external side effect**, and a tool with no recorded result is reported as "not run
during replay" rather than executed.

**Causality.** An agent replays a recorded decision only once it has received as many messages and
events as it had when it originally made it. For example, a root's final summary isn't replayed
before its children have reported. If inputs arrive together that originally came one at a time,
the agent carries on through its recorded steps without waiting for wake-ups that won't come.

**Usage.** A replayed step keeps its recorded token usage in the agent's own counters, so budgets
and wrap-up behave as they did. No provider is called, so nothing is metered or billed to the
organization.

**Forks.** Each agent switches to the live model at its first step past the fork point, and an
agent with nothing recorded (one spawned only in the fork) runs live from the start. The event
stream shows when each agent goes live.

Workspaces are long-lived and driven by outside events, so replay applies to tasks.

## Inspecting runs

- `GET /api/tasks/{id}/journal`: every step in order, with agent, role, path, a one-line summary
  and the full payload.
- `GET /api/tasks/{a}/diff/{b}`: two runs aligned step by step by agent path. Each step is `Same`,
  `Different`, `OnlyInA` or `OnlyInB`, and agents present in only one run are listed.
  - Agent ids, message ids and wall-clock time budgets differ between runs by design, so they're
    normalized before comparing.
  - Token counts describe cost, not behavior, so they're ignored.

In the dashboard, **Journal & replay** on a task opens `/replay?task={id}`:
- a step-through view with a slider over every decision and tool result;
- buttons to replay in full or fork after the selected step;
- a diff against another run. A replay is compared with its original by default.

## Tests

`ReplayTests`:
- A full replay of a finished run makes no model calls and doesn't charge again (a non-idempotent
  test tool). It produces the same tree, the same summary, and a step-for-step identical journal.
- A fork after the root's first decision replays that decision exactly, then continues live with a
  different outcome.
- A fork works from every recorded step: everything up to the fork point is identical.

`RunDiffTests` (unit) cover id normalization and alignment.
