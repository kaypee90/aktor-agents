# Autonomous Actor-Based AI Agent Runtime

## 1. Project Overview

Build a production-oriented prototype of an **autonomous multi-agent runtime based on the Actor Model**.

The system accepts a high-level human goal such as:

> "Research the market for an AI-powered property management SaaS and produce a technical/business proposal."

The system creates an initial Root Agent. The Root Agent autonomously determines what work needs to be done and can:

- create/spawn additional agents
- assign goals to those agents
- communicate with other agents
- discover existing agents
- request information from other agents
- create further subagents
- use tools
- observe results
- revise its plan
- determine when its assigned work is complete
- report the final result

The human should **not have to manually orchestrate individual agents**.

The system should provide a real-time UI showing:

- agents being created
- agents becoming active/idle
- messages between agents
- agents spawning other agents
- tool calls
- task progress
- failures
- retries
- completion
- the evolving agent hierarchy/network

The core architectural principle is:

> **The LLM provides reasoning. The Actor Runtime provides execution, isolation, messaging, lifecycle, resource management, and governance.**

Do not make the LLM responsible for enforcing system-level constraints.

---

# 2. Primary Technology Stack

## Backend

- C#
- .NET 10 or the latest supported LTS/.NET version available at implementation time
- Microsoft Orleans
- ASP.NET Core
- Entity Framework Core
- PostgreSQL
- OpenTelemetry
- Serilog or Microsoft.Extensions.Logging

Use the latest stable versions compatible with the selected .NET version.

Before implementation, verify current Orleans APIs against official Microsoft Orleans documentation rather than relying on outdated examples.

## Frontend

- Next.js
- React
- TypeScript
- React Flow for agent graph visualization
- Tailwind CSS
- WebSocket or Server-Sent Events for live events

## Infrastructure

Development environment:

- Docker Compose
- PostgreSQL
- optionally Redis if required by the implementation

Agent tool execution should support isolated Docker containers where appropriate.

## LLM

Create an abstraction that supports multiple providers.

Do not hard-code the application around one LLM provider.

Create:

```text
ILLMProvider
```

with implementations such as:

```text
OpenAIProvider
AnthropicProvider
GeminiProvider
```

Initially implement one provider completely, but design the system so additional providers can be added without modifying the Agent runtime.

---

# 3. Core Architectural Principle

The system must model agents as Actors.

Conceptually:

```text
Agent = State + Behavior + Mailbox + Goal + Capabilities
```

Agents must not directly mutate another agent's state.

Agents communicate through messages.

```text
Agent A
   |
   | Message
   v
Message Runtime
   |
   v
Agent B
```

Do NOT implement agents as ordinary singleton service objects communicating through direct method calls.

Use Orleans Grains as the primary actor abstraction.

---

# 4. High-Level Architecture

```text
                         USER
                          |
                          | Goal
                          v
                 ┌──────────────────┐
                 │   ASP.NET API    │
                 └────────┬─────────┘
                          |
                          v
                 ┌──────────────────┐
                 │  Root Agent      │
                 │  Orleans Grain   │
                 └────────┬─────────┘
                          |
                    LLM reasoning
                          |
              ┌───────────┼───────────┐
              |           |           |
              v           v           v
           Agent A     Agent B     Agent C
              |           |           |
              |       spawn Agent D   |
              |           |           |
              └───────────┼───────────┘
                          |
                     Agent Messages
                          |
                          v
                 ┌──────────────────┐
                 │  Event/Messaging │
                 │     Runtime      │
                 └────────┬─────────┘
                          |
              ┌───────────┼────────────┐
              v           v            v
          PostgreSQL   Tool Runtime   WebSocket
                                      |
                                      v
                                React Dashboard
```

---

# 5. Repository Structure

Create a monorepo with approximately this structure:

```text
/autonomous-agents

  /src

    /AgentRuntime
      /Agents
      /Contracts
      /Messaging
      /Tools
      /LLM
      /Memory
      /Planning
      /Supervision
      /Resources
      /Events
      /Persistence
      /Configuration

    /AgentRuntime.Api

    /AgentRuntime.Infrastructure

    /AgentRuntime.Tests
    /AgentRuntime.IntegrationTests

    /Web

  /docker

  /docs

  docker-compose.yml
  README.md
  .env.example
```

Keep domain logic separate from infrastructure.

---

# 6. Agent Model

Create an Agent entity/state containing at minimum:

```csharp
AgentState
{
    AgentId
    ParentAgentId
    RootAgentId

    Name
    Role

    Goal
    Status

    Capabilities

    CreatedAt
    StartedAt
    CompletedAt

    CurrentTask

    ParentRelationship

    Children

    TokenBudget
    TokensUsed

    TimeBudget
    StartedExecutionAt

    SpawnBudget
    ChildrenSpawned

    ToolBudget
    ToolCallsUsed

    Metadata
}
```

Possible statuses:

```text
Created
Initializing
Idle
Thinking
Executing
Waiting
Spawning
Completed
Failed
Terminated
TimedOut
```

Use explicit state transitions.

Do not allow arbitrary status mutation.

---

# 7. Orleans Grain

Create:

```csharp
IAgentGrain
```

with operations conceptually equivalent to:

```csharp
Task Initialize(AgentInitializationRequest request);

Task Start();

Task SendMessage(AgentMessage message);

Task HandleEvent(EnvironmentEvent environmentEvent);

Task Pause();

Task Resume();

Task Stop();

Task<AgentSnapshot> GetSnapshot();

Task<AgentStatus> GetStatus();
```

The actual Orleans implementation should follow current Orleans conventions.

The Grain owns its state.

Other agents communicate with it through messages.

---

# 8. Agent Runtime

Create an `AgentRuntime` responsible for:

- creating agents
- locating agents
- validating spawn requests
- enforcing resource limits
- assigning IDs
- maintaining relationships
- routing messages
- publishing lifecycle events
- enforcing permissions
- handling failures
- collecting metrics

The runtime, not the LLM, is authoritative.

---

# 9. Agent Spawning

This is the most important feature.

Every agent should have access to a logical tool:

```text
spawn_agent
```

Example:

```json
{
  "role": "database specialist",
  "goal": "Design a PostgreSQL schema for the application",
  "capabilities": [
    "postgresql",
    "database-design"
  ],
  "initial_context": "The application manages rental properties."
}
```

The LLM decides whether it needs another agent.

The runtime validates the request.

If valid:

```text
Parent Agent
     |
     | spawn_agent
     v
Agent Runtime
     |
     | validate limits
     |
     v
Orleans Agent Grain
```

Return:

```json
{
  "agent_id": "agent-123",
  "status": "created"
}
```

The parent can then communicate with that agent.

---

# 10. Recursive Spawning

Agents must be able to spawn agents themselves.

Example:

```text
Root
 |
 +-- Research Agent
       |
       +-- Competitor Agent
       |
       +-- Market Data Agent
 |
 +-- Engineering Agent
       |
       +-- Backend Agent
             |
             +-- Database Agent
             |
             +-- Security Agent
```

Do not assume a fixed hierarchy depth.

However, enforce configurable limits:

```text
MAX_AGENT_DEPTH
MAX_CHILDREN_PER_AGENT
MAX_TOTAL_AGENTS
MAX_ACTIVE_AGENTS
```

Defaults:

```text
MAX_AGENT_DEPTH = 5
MAX_CHILDREN_PER_AGENT = 10
MAX_TOTAL_AGENTS = 100
MAX_ACTIVE_AGENTS = 50
```

Make these configuration values.

---

# 11. Agent Discovery

Agents must be able to discover other agents.

Provide a tool:

```text
find_agents
```

Example:

```json
{
  "capabilities": [
    "postgresql"
  ],
  "status": "Idle"
}
```

Return suitable agents.

This is important because an agent should not always create a new agent if an existing agent can perform the work.

The LLM should be instructed:

> Prefer collaborating with an existing suitable agent when appropriate. Spawn a new agent when additional specialization or parallel execution is justified.

---

# 12. Agent Messaging

Define:

```csharp
AgentMessage
```

with:

```text
MessageId
FromAgentId
ToAgentId
ConversationId
CorrelationId
MessageType
Priority
Timestamp
Payload
ReplyTo
```

Message types:

```text
TaskRequest
TaskResponse
InformationRequest
InformationResponse
StatusUpdate
DelegationRequest
DelegationResponse
SpawnNotification
CompletionNotification
FailureNotification
Cancellation
```

Example:

```json
{
  "from": "agent-001",
  "to": "agent-014",
  "type": "TaskRequest",
  "payload": {
    "goal": "Analyze PostgreSQL performance"
  }
}
```

Messages must be persisted.

---

# 13. Autonomous Communication

Agents must be allowed to communicate without passing every message through the Root Agent.

For example:

```text
ResearchAgent
      |
      | asks
      v
DatabaseAgent
      |
      | responds
      v
ResearchAgent
```

The Root Agent does not need to relay every interaction.

The system should therefore support:

```text
Agent A ↔ Agent B
Agent B ↔ Agent C
Agent C ↔ Agent D
```

This is a true actor network.

---

# 14. Agent Goals

Every agent must have an explicit goal.

Example:

```text
Goal:
"Determine whether PostgreSQL or MongoDB is more appropriate
for this application's data model."
```

The goal should remain available to the agent throughout its lifecycle.

Agents should distinguish between:

```text
Goal
Current task
Completed work
Pending work
Blocked work
```

---

# 15. Agent Planning

The Root Agent should initially receive the user's high-level goal.

Example:

```text
"Build a SaaS application for property management."
```

The Root Agent should reason about the goal and produce an internal plan.

The plan might be:

```text
1. Understand requirements
2. Research competitors
3. Design architecture
4. Design database
5. Implement backend
6. Implement frontend
7. Add authentication
8. Test
9. Review
10. Produce final result
```

The Root Agent should then decide which work should be delegated.

Do not force a predetermined workflow.

---

# 16. LLM Tool Interface

The LLM should receive a tool catalog containing tools such as:

```text
spawn_agent
find_agents
send_message
get_agent_status
list_children
read_memory
write_memory
search_knowledge
execute_tool
complete_task
```

The model should be able to call these tools through structured tool/function calling.

Do not parse arbitrary natural-language output to determine tool calls.

Use structured tool calls.

---

# 17. Tool Execution

Create:

```csharp
ITool
```

with:

```csharp
ToolDefinition
ToolExecutionRequest
ToolExecutionResult
```

Example tools:

```text
WebSearchTool
FilesystemTool
GitTool
ShellTool
DockerTool
DatabaseTool
HttpTool
```

Each agent receives only the tools permitted by its capabilities.

Example:

```text
SecurityAgent:
  web-search
  github
  filesystem
  security-scanner

DatabaseAgent:
  postgres
  filesystem
  shell
```

Agents must not automatically receive every tool.

---

# 18. Tool Permissions

Define permissions such as:

```text
ReadFilesystem
WriteFilesystem
ExecuteShell
NetworkAccess
GitRead
GitWrite
DatabaseRead
DatabaseWrite
SpawnAgents
SendMessages
```

Permissions are enforced by the runtime.

An LLM cannot grant itself permissions.

---

# 19. Execution Isolation

Tools capable of executing arbitrary code must run in isolated environments.

For example:

```text
Agent
  |
  v
Tool Runtime
  |
  v
Docker Container
  |
  +-- filesystem
  +-- network policy
  +-- CPU limit
  +-- memory limit
  +-- timeout
```

Do not allow arbitrary agent-generated shell commands to execute directly on the host machine.

---

# 20. Agent Memory

Implement three memory categories.

## Working Memory

Current task/context.

## Episodic Memory

Events/actions/results from the agent's work.

## Shared Knowledge

Knowledge available to multiple agents.

Start with PostgreSQL-backed storage.

Vector search can be added behind an abstraction later.

Create:

```text
IMemoryStore
```

and do not tightly couple agents to a specific vector database.

---

# 21. Agent Lifecycle

Implement:

```text
Created
   ↓
Initializing
   ↓
Idle
   ↓
Thinking
   ↓
Executing
   ↓
Waiting
   ↓
Thinking
   ↓
Completed
```

Failure path:

```text
Executing
   ↓
Failed
   ↓
Retry
   ↓
Executing
```

or:

```text
Failed
   ↓
Terminated
```

Timeout:

```text
Executing
   ↓
TimedOut
```

Every lifecycle transition must emit an event.

---

# 22. Supervision

Implement a supervisor mechanism.

The supervisor should monitor:

- agent failures
- repeated failures
- timeouts
- resource exhaustion
- malformed tool calls
- agents that stop making progress

Possible policies:

```text
Retry
Restart
Replace
Terminate
Escalate
```

The initial implementation can use simple configurable policies.

Example:

```json
{
  "max_retries": 3,
  "failure_policy": "restart"
}
```

---

# 23. Avoid Infinite Loops

Agents can become stuck in loops such as:

```text
Agent A asks B
B asks A
A asks B
...
```

Implement:

- maximum message count per task
- maximum task duration
- maximum reasoning iterations
- maximum repeated tool calls
- maximum agent depth
- maximum total agents
- cycle detection where practical

Detect repeated equivalent actions.

---

# 24. Resource Budgets

Every task and agent receives budgets.

Example:

```json
{
  "max_tokens": 50000,
  "max_duration_seconds": 900,
  "max_children": 5,
  "max_tool_calls": 100,
  "max_cost_usd": 2.00
}
```

Budgets should propagate to children.

Example:

```text
Root budget = $20

Root spawns Agent A
Agent A budget = $5

Agent A spawns Agent B
Agent B budget <= remaining Agent A budget
```

The runtime must enforce budgets.

Do not trust the LLM to obey them.

---

# 25. Task Completion

Agents need a structured mechanism to declare completion.

Provide:

```text
complete_task
```

with:

```json
{
  "status": "completed",
  "summary": "...",
  "artifacts": [],
  "evidence": [],
  "remaining_work": []
}
```

The runtime should validate completion.

The Root Agent decides whether the overall task is complete.

---

# 26. Artifact Management

Agents should be able to create artifacts:

```text
documents
code
reports
images
data
repositories
```

Create an artifact abstraction:

```text
Artifact
ArtifactId
Type
Location
CreatedByAgent
CreatedAt
Metadata
```

Artifacts should be traceable to the agent and task that created them.

---

# 27. Environment Model

The environment should expose:

```text
Filesystem
Git repository
Database
External APIs
Web
Tool services
Event stream
Agent registry
```

Agents interact with the environment through tools.

The environment should produce events.

Example:

```text
DeploymentCompleted
FileChanged
TestFailed
DatabaseThresholdExceeded
PullRequestCreated
ExternalDataUpdated
```

Agents may subscribe to relevant event types.

---

# 28. Event System

Create an internal event stream.

Events should include:

```text
AgentCreated
AgentStarted
AgentThinking
AgentToolCalled
AgentToolCompleted
AgentMessageSent
AgentMessageReceived
AgentSpawnRequested
AgentSpawned
AgentCompleted
AgentFailed
AgentRestarted
AgentTerminated
TaskCreated
TaskCompleted
ArtifactCreated
EnvironmentChanged
```

Persist important events.

---

# 29. Real-Time Dashboard

Create a Next.js dashboard.

Main screen:

```text
┌──────────────────────────────────────────────────────────┐
│ Autonomous Agent Runtime                                 │
├──────────────────────────────────────────────────────────┤
│                                                          │
│  Agent Graph                                             │
│                                                          │
│             ┌─────────┐                                  │
│             │  Root   │                                  │
│             └────┬────┘                                  │
│                  │                                        │
│        ┌─────────┼─────────┐                              │
│        ▼         ▼         ▼                              │
│     Research  Backend   Security                         │
│        │         │                                        │
│        ▼         ▼                                        │
│     Market     Database                                   │
│                                                          │
├──────────────────────────────────────────────────────────┤
│ Event Stream                                             │
│                                                          │
│ 18:42 Agent-01 spawned Agent-07                          │
│ 18:42 Agent-07 started research                          │
│ 18:43 Agent-07 → Agent-01                                │
│ 18:43 Agent-04 spawned Agent-09                          │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

Use React Flow to render the graph.

---

# 30. Agent Details Panel

Clicking an agent should show:

```text
Agent ID
Role
Goal
Parent
Children
Status
Current Task
Capabilities
Tools
Messages
Token Usage
Cost
Runtime
Created At
Started At
Last Activity
```

Also show its recent reasoning/action trace.

Do not expose hidden chain-of-thought.

Instead expose structured telemetry such as:

```text
Decision:
"Need database expertise"

Action:
spawn_agent

Reason summary:
"Database schema requires specialized PostgreSQL knowledge."
```

Never store or display private chain-of-thought.

---

# 31. Message Visualization

When Agent A sends a message to Agent B:

```text
Agent A ───────────────→ Agent B
       TaskRequest
```

Animate/display the event in the UI.

Allow users to inspect the structured message.

---

# 32. Human Interaction

The system should initially be autonomous after the initial task is submitted.

Human controls:

```text
Start
Pause
Resume
Stop
Cancel Task
Inspect Agent
Inspect Messages
Inspect Events
Terminate Agent
```

Do not require approval for every agent action.

However, the runtime must support configurable policies for sensitive operations.

---

# 33. Autonomy Levels

Implement:

```text
Supervised
SemiAutonomous
Autonomous
```

For the first prototype, default to:

```text
Autonomous
```

In Autonomous mode:

- agents can spawn agents
- agents can communicate
- agents can use permitted tools
- agents can retry
- agents can create artifacts
- agents can dynamically change their plan

Runtime safety/resource policies still apply.

---

# 34. Initial Demonstration Scenario

Implement one complete end-to-end demo.

User enters:

> "Research the feasibility of building an AI-powered property management SaaS."

Expected behavior:

```text
Root Agent
   |
   +-- spawns Market Research Agent
   |
   +-- spawns Competitor Research Agent
   |
   +-- spawns Technical Architecture Agent
   |
   +-- spawns Business Model Agent
```

Then:

```text
Market Research Agent
   |
   +-- may spawn Data Collection Agent

Technical Architecture Agent
   |
   +-- spawns Backend Agent
   |
   +-- spawns Database Agent

Competitor Research Agent
   |
   +-- communicates with Market Research Agent
```

Agents work independently and exchange messages.

Eventually:

```text
Root Agent
   |
   +-- receives research
   +-- receives architecture
   +-- receives business model
   +-- evaluates completeness
   |
   ▼
Final Report
```

The dashboard should visibly show this occurring.

---

# 35. Second Demonstration Scenario

Create a coding-oriented demo.

Goal:

> "Add user authentication to this application."

Expected autonomous behavior:

```text
Root
 |
 +-- Architecture Agent
 |
 +-- Security Agent
 |
 +-- Backend Agent
 |      |
 |      +-- Database Agent
 |
 +-- Frontend Agent
 |
 +-- Testing Agent
```

Agents should communicate directly where useful.

Example:

```text
Security Agent
      |
      | authentication requirements
      v
Backend Agent
      |
      | API contract
      v
Frontend Agent
      |
      | implementation complete
      v
Testing Agent
```

---

# 36. Agent Prompt Architecture

Every agent should receive a system prompt containing:

```text
ROLE

GOAL

CURRENT STATE

AVAILABLE CAPABILITIES

AVAILABLE TOOLS

RESOURCE LIMITS

MESSAGING RULES

SPAWNING RULES

COMPLETION CRITERIA

ENVIRONMENT INFORMATION
```

Do not hard-code the entire prompt into one large string.

Create a prompt builder:

```text
IAgentPromptBuilder
```

---

# 37. Agent Behavioral Rules

Every agent should be instructed:

1. Work toward your assigned goal.
2. Prefer completing work yourself when trivial.
3. Delegate when specialization or parallelism provides value.
4. Reuse suitable existing agents when possible.
5. Do not spawn unnecessary agents.
6. Communicate relevant findings to collaborators.
7. Validate important results.
8. Do not assume another agent completed work without evidence.
9. Respect resource limits.
10. Stop when the goal is complete.
11. Report blockers explicitly.
12. Never attempt to bypass runtime permissions.

---

# 38. Agent Identity

Every agent gets a unique ID.

Example:

```text
root-01
agent-4f81
agent-92bd
```

Also maintain:

```text
ParentAgentId
RootAgentId
ConversationId
TaskId
```

This makes the complete agent tree reconstructable.

---

# 39. Persistence

Persist:

```text
Agents
AgentRelationships
Tasks
Messages
Events
ToolCalls
Artifacts
Memory
ResourceUsage
```

PostgreSQL should be the source of truth for durable history.

Orleans state should be optimized for active actor execution.

Do not rely solely on in-memory state.

---

# 40. Observability

Use OpenTelemetry.

Track:

```text
agent.created
agent.started
agent.completed
agent.failed

agent.message.sent
agent.message.received

agent.spawned

agent.tool.started
agent.tool.completed

llm.request
llm.response

task.started
task.completed
```

Metrics:

```text
active_agents
total_agents_created
messages_per_second
agent_failures
agent_restarts
llm_tokens
llm_cost
tool_calls
task_duration
```

---

# 41. Logging

Every log entry related to an agent should include:

```text
AgentId
TaskId
ConversationId
CorrelationId
```

Example:

```text
[Agent=agent-92bd]
[Task=task-123]
[Correlation=abc]
Spawning database specialist
```

---

# 42. Testing

Create unit tests for:

- agent lifecycle
- spawn validation
- budget enforcement
- message routing
- permissions
- status transitions
- failure handling
- retry handling
- cycle prevention

Create integration tests for:

- Root Agent spawning child
- child spawning grandchild
- Agent A communicating with Agent B
- multiple agents working concurrently
- agent failure and restart
- budget exhaustion
- task completion
- persistence/recovery

Create an end-to-end test:

```text
Submit goal
    ↓
Root Agent created
    ↓
Root spawns agents
    ↓
Agents communicate
    ↓
Agents complete tasks
    ↓
Root produces result
```

---

# 43. Deterministic Testing

LLM behavior is nondeterministic.

Create a `MockLLMProvider`.

It should allow deterministic scenarios.

Example:

```text
When Root receives task:
    return spawn_agent(database)

When DatabaseAgent receives task:
    return complete_task(...)
```

Use this for most automated tests.

Real LLMs should only be used for dedicated integration/evaluation tests.

---

# 44. Security Requirements

Never allow:

- unrestricted host shell access
- unrestricted filesystem access
- unrestricted network access
- arbitrary credential access
- agents modifying runtime configuration
- agents increasing their own resource budgets

Secrets must be injected securely.

Do not expose API keys to agents unless explicitly required.

Tools should receive credentials through the runtime.

---

# 45. Configuration

Use environment/configuration for:

```text
LLM provider
LLM model
API keys
PostgreSQL connection
Orleans configuration
maximum agents
maximum depth
maximum budget
tool permissions
Docker settings
logging level
autonomy mode
```

Provide:

```text
.env.example
```

Never commit real credentials.

---

# 46. API

Create REST endpoints approximately:

```text
POST   /api/tasks
GET    /api/tasks/{id}
POST   /api/tasks/{id}/pause
POST   /api/tasks/{id}/resume
POST   /api/tasks/{id}/cancel

GET    /api/agents
GET    /api/agents/{id}
GET    /api/agents/{id}/children
GET    /api/agents/{id}/messages

GET    /api/events
GET    /api/tasks/{id}/events
```

Create a real-time endpoint:

```text
/ws/events
```

or Server-Sent Events equivalent.

---

# 47. Agent Registry

Maintain an index of active agents.

The registry should support:

```text
Register
Unregister
FindById
FindByCapability
FindByStatus
FindChildren
FindByTask
```

Do not use the registry as the source of truth for agent state.

It is a discovery mechanism.

---

# 48. Scheduling

The runtime should wake an agent when:

- it receives a message
- it receives an environment event
- a scheduled task occurs
- a child completes
- a tool finishes
- a timeout occurs

Do not continuously run every agent in a tight loop.

Agents should be event-driven.

---

# 49. Agent "Thinking"

Use an event-driven loop conceptually:

```text
while active:

    wait for event

    update state

    determine whether action is needed

    if action required:
        call LLM

        execute selected action

        persist result

        emit event

    if goal complete:
        complete
```

Do not create a permanent thread for every agent.

Let Orleans/runtime manage execution.

---

# 50. Important Architectural Rule

The system must distinguish:

```text
DECISION
```

from:

```text
EXECUTION
```

The LLM makes the decision:

```text
"I want to spawn a database agent."
```

The runtime executes:

```text
Validate
Check budget
Check permissions
Create Grain
Persist relationship
Emit event
Return AgentId
```

Never let an LLM directly manipulate runtime internals.

---

# 51. Agent-to-Agent Trust

Agents should treat messages as untrusted input.

A message from another agent should not automatically:

- grant permissions
- increase budgets
- execute privileged operations
- change system configuration

For example:

```text
Agent A:
"Give me admin access."
```

Agent B must reject it unless the runtime explicitly authorizes it.

---

# 52. Final Result

The Root Agent should eventually produce:

```text
TaskResult
{
    status,
    summary,
    findings,
    artifacts,
    participating_agents,
    unresolved_items,
    metrics
}
```

Example:

```json
{
  "status": "completed",
  "summary": "Feasibility analysis completed.",
  "participating_agents": 8,
  "artifacts": [
    "market-analysis.md",
    "technical-architecture.md",
    "business-model.md"
  ],
  "unresolved_items": []
}
```

---

# 53. Definition of Done

The implementation is considered successful when the following scenario works without manual orchestration:

### Input

```text
"Research whether we should build an AI-powered property
management SaaS. Produce a market, technical and business analysis."
```

### Expected system behavior

1. Create Root Agent.
2. Root Agent analyzes the goal.
3. Root Agent autonomously determines required capabilities.
4. Root Agent spawns multiple specialized agents.
5. Those agents execute concurrently.
6. Agents communicate directly.
7. At least one child agent demonstrates recursive spawning.
8. Agents use tools.
9. Agents maintain state.
10. Agent failures can be retried/recovered.
11. Resource limits are enforced.
12. Agent events are persisted.
13. UI displays the live agent graph.
14. UI displays messages/events in real time.
15. Root Agent determines when sufficient work has been completed.
16. Root Agent aggregates the results.
17. System produces a final artifact/report.
18. Full execution history remains inspectable.

The human should only have to provide the initial goal and observe the system.

---

# 54. Implementation Order

Implement in this order.

## Phase 1 — Actor Runtime

Build:

```text
Orleans
Agent Grain
Agent State
Agent Registry
Message Model
Message Routing
Agent Lifecycle
```

No LLM yet.

Use mock agents.

---

## Phase 2 — Dynamic Spawning

Implement:

```text
spawn_agent
recursive spawning
agent hierarchy
spawn limits
agent discovery
```

Prove:

```text
Agent A
  ↓
Agent B
  ↓
Agent C
```

works.

---

## Phase 3 — LLM Integration

Implement:

```text
ILLMProvider
structured tool calling
prompt builder
agent reasoning loop
```

Connect the Root Agent to an actual LLM.

---

## Phase 4 — Agent Communication

Implement:

```text
send_message
find_agents
task delegation
task responses
direct agent-to-agent communication
```

---

## Phase 5 — Tools

Implement:

```text
filesystem
web
git
shell
docker
```

Start with safe read-only tools before enabling write/execute capabilities.

---

## Phase 6 — Persistence

Implement PostgreSQL persistence for:

```text
agents
messages
events
tasks
artifacts
memory
```

---

## Phase 7 — Autonomous Runtime

Implement:

```text
event-driven scheduling
supervision
retry
timeouts
budgets
cycle prevention
```

---

## Phase 8 — Dashboard

Implement:

```text
agent graph
agent details
message stream
event stream
task status
resource usage
```

Connect through WebSocket/SSE.

---

## Phase 9 — End-to-End Demonstration

Run the complete autonomous scenario.

The final demonstration must clearly show:

```text
Human Goal
    ↓
Root Agent
    ↓
Autonomous Planning
    ↓
Agent Spawn
    ↓
Parallel Work
    ↓
Agent-to-Agent Communication
    ↓
Recursive Agent Spawn
    ↓
Tool Usage
    ↓
Results
    ↓
Validation
    ↓
Final Result
```

---

# 55. Coding Standards

Follow these principles:

- Clean Architecture where practical
- Dependency Injection
- Interfaces around external providers
- asynchronous APIs
- cancellation tokens
- immutable message contracts
- structured logging
- no static mutable global state
- no hard-coded API keys
- no provider-specific logic inside the Agent domain
- no direct agent-to-agent state mutation
- no LLM-controlled security policies
- no unbounded loops
- no unbounded agent creation

Use nullable reference types.

Use modern C# features appropriately.

Write XML documentation for important public interfaces.

---

# 56. README Requirements

The README must explain:

1. What the project is.
2. Actor Model architecture.
3. How an agent works.
4. How agents spawn agents.
5. How agents communicate.
6. How the LLM interacts with the runtime.
7. How to run locally.
8. How to configure an LLM provider.
9. How to run the demonstration.
10. How to inspect the agent graph.
11. Resource/security controls.
12. Architecture diagram.
13. Example agent interaction.

Include diagrams using Mermaid where appropriate.

---

# 57. Critical Design Philosophy

The implementation should NOT be a workflow engine disguised as a multi-agent system.

Avoid hard-coding:

```text
Research → Design → Code → Test
```

as the required execution sequence.

Instead, the runtime should provide primitives:

```text
spawn
message
discover
observe
act
use_tool
remember
complete
```

The agents decide how to compose those primitives.

The goal is to create an environment in which **complex agent behavior emerges from relatively simple actor primitives**.

---

# 58. Final Architecture Principle

The final system should conceptually satisfy:

```text
                    ┌─────────────────────┐
                    │        GOAL         │
                    └──────────┬──────────┘
                               │
                               ▼
                    ┌─────────────────────┐
                    │     ROOT ACTOR      │
                    │                     │
                    │ LLM + Goal + State  │
                    └──────────┬──────────┘
                               │
                     decides to delegate
                               │
              ┌────────────────┼────────────────┐
              ▼                ▼                ▼
         ┌─────────┐      ┌─────────┐      ┌─────────┐
         │ Actor A │      │ Actor B │      │ Actor C │
         └────┬────┘      └────┬────┘      └────┬────┘
              │                │                │
              │ spawn          │ message        │
              ▼                │                │
         ┌─────────┐            │                │
         │ Actor D │◄───────────┴────────────────┘
         └────┬────┘
              │
              │ spawn
              ▼
         ┌─────────┐
         │ Actor E │
         └─────────┘

             ALL ACTORS
                 │
                 ▼
          SHARED ENVIRONMENT
                 │
       ┌─────────┼──────────┐
       ▼         ▼          ▼
     Tools     Memory     Events
```

The defining property of the system is:

> **A human provides a goal. Agents determine the decomposition, dynamically create the actors they need, communicate with each other, use available tools, recover from failures, and coordinate toward completion.**

The runtime provides the constraints and infrastructure, but the task-specific organization of the agents should emerge autonomously.
