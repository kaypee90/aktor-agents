# Durable execution

Agents in this runtime can run for minutes or for years. They must survive process crashes,
restarts, deploys and outages, and continue exactly where they stopped. This document describes
what is guaranteed, how it works, and where the limits are.

## Guarantees

| If the process dies… | What happens on recovery |
|---|---|
| between two steps of an agent's turn | The turn continues from the last saved step. No step is lost and none is repeated. |
| during an LLM call | The call is made again: it's the one step that is re-run, and it has no external effect beyond its cost. |
| during a **read-only** or **idempotent** tool call | The call is re-run with the **same idempotency key**, so the effect happens once. |
| during a **non-idempotent** tool call | The call is **not** repeated. The agent receives an *outcome unknown* result and a `AgentToolCompleted` event with `outcome=unknown` is published for review. |
| after a message was accepted for an agent | The message is delivered exactly once. |
| after a pause, stop or retire request was accepted | The request is applied. |
| after an agent completed or failed, before its parent was told | The parent is still told, exactly once. |
| while a world simulation was running | The world's clock restarts from the last saved tick; perceptions for a replayed tick are deduplicated. |

"Recovery" needs no human and no incoming request. A durable reminder re-activates any agent or
world with interrupted work on a live silo, within about one reminder period
(`Durability:RecoveryReminderPeriod`, default one minute) of a silo being available.

### What is not guaranteed

- **Exactly-once external effects in general.** If a process dies after an external system
  accepted a request but before the runtime recorded the result, the runtime can't know whether
  it took effect. It therefore classifies tools:
  - Tools that can be made idempotent must be. They pass `ToolExecutionRequest.IdempotencyKey`
    to the external API, as a message id, an `Idempotency-Key` header, or an upsert key.
  - Tools that can't are marked `NonIdempotent`, and the ambiguous case is surfaced to the agent
    and to operators instead of being guessed.
- **Telemetry events.** The live event stream and the `Events` history table are written
  asynchronously and can lose the last moment of activity before a crash. The authoritative record
  of what an agent did is its own durable state (transcript and tool results), not the event log.
- **In-progress LLM output.** A response being generated when the process died is lost and
  requested again.

## How it works

### Durable state

Orleans grain state (agents, mailboxes, the agent registry, worlds) is stored in PostgreSQL through
Orleans' ADO.NET provider (`Silo:Storage=AdoNet`, the default), using Orleans' binary serializer.
That format is version tolerant, keyed by each field's `[Id]`, so new fields can be added to
agent state and state saved years earlier still loads.

The Orleans schema (official scripts from dotnet/orleans v10.3.1, embedded in
`AgentRuntime.Infrastructure/Persistence/OrleansSql`) is installed automatically on first startup.

### The step journal

An agent's transcript is its journal:

1. **Decision.** The LLM's response, including any tool calls, is appended and saved before any
   tool runs.
2. **Intent.** For a `NonIdempotent` tool only, the call's id is saved to `InFlightToolCallIds`
   *before* the call runs.
3. **Result.** Each tool result is appended and saved before the next call runs, together with
   its bookkeeping (for example, a spawned child's budget reservation).

On recovery, the latest decision's calls that have no result are resumed:
- a call still in `InFlightToolCallIds` started and never finished, so its outcome is unknown and
  it is not repeated;
- every other call hadn't finished (or hadn't started), so it is run with its original
  idempotency key.

### Idempotency keys

Every tool call gets the key `{agentId}:{toolCallId}`, stable across replays. The runtime's own
side effects are keyed by it:

| Effect | How the key is used |
|---|---|
| `spawn_agent` | Child id = hash of the key; the registry and `Initialize` accept a repeat of the same id. |
| `send_message` | Message id = hash of the key; the recipient's mailbox drops a repeated id. |
| World actions | The world records each action's result by key and returns it on replay. |
| `write_memory` | An upsert by key. |
| Parent notifications | Fixed id per child and outcome. |

### Durable mailbox

Every input to an agent goes into its `AgentMailboxGrain`, which is persisted before the sender
gets an acknowledgement: messages, environment events, and operator control (pause, resume,
stop). The agent consumes items only at safe points, when every tool call of the last decision
has its result, so an input can never land between a tool call and its result.

An item is removed only after the agent has saved the highest sequence number it consumed, so it
is never lost and never processed twice. The mailbox keeps a reminder while it holds items, so a
wake-up lost in a crash is repeated.

### Outbox

Messages the runtime owes other agents, such as "your child completed", are saved in the agent's
`Outbox` in the same write as the status change that caused them. They are delivered afterwards
and removed on success.

### Reminders

Grain timers live in memory; reminders are stored in PostgreSQL (`orleansreminderstable`) and fire
on whichever silo is alive.
- An agent holds a reminder while a turn is in progress or its outbox is non-empty, and drops it
  when idle, so idle agents cost nothing.
- A running world holds a reminder that restarts its tick timer.
- An agent or world re-activated for any reason also checks for interrupted work in
  `OnActivateAsync`.

## Configuration

| Setting | Default | Meaning |
|---|---|---|
| `Silo:Storage` | `AdoNet` | `AdoNet` for durable state in PostgreSQL; `Memory` for nothing durable (tests and demos). |
| `Silo:Clustering` | `Localhost` | `AdoNet` for PostgreSQL membership, so several silos can run and take over each other's agents. |
| `Durability:RecoveryReminderPeriod` | `00:01:00` | How quickly interrupted work is picked up. |

## Writing a tool

Declare `ToolDefinition.SideEffects` honestly:
- `ReadOnly`: no external effect.
- `Idempotent`: safe to repeat with the same `IdempotencyKey`. Pass the key to the external API.
- `NonIdempotent`: the default for unknown tools.

A tool that sends email, SMS or payments should use the provider's idempotency support and
declare itself `Idempotent`. If the provider has none, leave it `NonIdempotent` so a crash
produces "outcome unknown" instead of a duplicate charge or message.

## Tests

`AgentRuntime.IntegrationTests/CrashRecoveryTests.cs` kills the silo abruptly in the middle of a
tool call and starts a fresh one, which recovers from durable storage alone. Each scenario is
checked without anything poking the agent:
- a non-idempotent call runs exactly once, and the agent is told its outcome is unknown;
- an idempotent call is retried with the same key;
- a message sent just before the crash is seen exactly once;
- a stop request sent just before the crash is honoured;
- a running world finishes its ticks.

The durable test doubles (`TestSupport/DurableTestInfrastructure.cs`) keep state outside the
silo, as a database would. These tests fail if that storage is made non-durable, or if the
intent journal is disabled (the card would be charged twice).
