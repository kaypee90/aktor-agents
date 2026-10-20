/**
 * The documentation shown at /docs: what the platform does and how to use it. Each section is
 * Markdown. The repository's docs/ folder has the full reference for each area; these pages are
 * the guided tour, kept in step with it.
 */
export type DocSection = { id: string; group: string; title: string; summary: string; body: string };

export const DOCS: DocSection[] = [
  // ---------------------------------------------------------------- Start here
  {
    id: "overview",
    group: "Start here",
    title: "What Aktor is",
    summary: "Autonomous agent teams on a governed actor runtime, and a platform to build your own agentic products on.",
    body: `
Aktor runs **teams of AI agents**. You give a goal, such as a market analysis, a code change, or
triage for every new support ticket. A root agent plans the work, spawns the specialists it needs,
and those agents can spawn their own. They message each other directly, use tools, and report back
with a checked result.

The rule underneath everything: **the model reasons; the runtime governs.** Every agent is an
*actor* (an Orleans grain) with its own state, mailbox, goal and capabilities. Spawning, messaging,
tools, budgets, permissions and approvals all go through the runtime, which checks each request
before anything happens. The model is never trusted to enforce a limit.

You can use Aktor in two ways:

| As… | You… |
|---|---|
| **A product** | Chat with a team in **Tasks**, run reusable pipelines in **Workspaces**, research with your own data in **Studies**. |
| **A platform** | Package a business application as a **solution**, with its own data model, the assistant your customers talk to, and evals. Deploy it with the \`aktor\` CLI and put it in front of your own customers. |

Everything is on the record: messages, tool calls, decisions, spend and approvals. You can watch a
run live, then audit, replay or fork it afterwards.
`,
  },
  {
    id: "whats-new",
    group: "Start here",
    title: "What's new: solutions",
    summary: "Aktor is now a platform to build your own agentic products on.",
    body: `
| Area | What changed |
|---|---|
| **Solutions** | Package a business app (workspaces, a data model, an assistant for your customers, skills, knowledge, evals) and install, upgrade or uninstall it as one unit. Upgrades change only what the new version changed. |
| **The builder** | Make, check and deploy a solution with forms in the dashboard, with no CLI and no YAML. Download the package if you want it. |
| **Data** | Typed entities your agents work on through \`data__\` tools. Every write is checked; filters run in SQL; fields can be indexed. A record change can start a run, or reach your backend as a signed webhook. |
| **End users** | Your customers talk to your solution's assistant through an embed widget or the apps API, with your own sign-in if you like. Each reaches only their own data, enforced in the tools, with rate limits and monthly cost caps. |
| **Registry** | Publish signed, versioned packages; install them by name; yank a bad version. |
| **Usage** | Cost per end user, workspace or day, to bill your customers. |
| **Tools** | The \`aktor\` CLI, the TypeScript and Python SDKs, and remote plugins in Node or Python. |
| **Global connectors** | Set an integration up once on the **Connectors** page and link it from any workspace, workflow, solution or (if allowed) task. Rotate a key once; every link uses it. |
| **Design with AI** | Describe a solution, or a change to it, in the builder's chat. Each answer lists validated changes you apply (or undo). |
| **Mentions in the builder** | Type \`@\` in the builder's chat or its instruction boxes to name a stage, workspace, skill, knowledge or connector. Agents are told exactly what each one is, and mentioning a connector connects it. |
| **API** | \`/api/v1\` is the stable contract; the solution platform is at version 1.3.0. |

Upgrading a server: rebuild and restart, and a database migration is applied automatically. Back
up \`SECRETS_MASTER_KEY\`: it now also signs end-user tokens, records bindings and registry
packages. The full list of changes is in \`docs/solutions-changelog.md\`.
`,
  },
  {
    id: "getting-started",
    group: "Start here",
    title: "Getting started",
    summary: "Run it locally, sign in, connect a model, start a first task.",
    body: `
### Run it

\`\`\`bash
cp .env.example .env          # set LLM_PROVIDER and its key, or leave LLM_PROVIDER=Mock to try it free
docker compose up --build     # Postgres, the API (with its Orleans silo) and this dashboard
\`\`\`

Open **http://localhost:3000**. The first person to sign up owns the organization. For a single-user
machine, set \`AUTH_MODE=disabled\` and skip sign-in.

### Connect a model

Go to **Settings → Models** and add Anthropic, OpenAI, Gemini or a local Ollama model, each with its
own key. Keys are encrypted and never shown to agents. The **Mock** model runs every feature
deterministically with no key, which is useful for trying things out and for tests, but it doesn't
really reason.

### Your first task

On **Tasks**, describe the outcome you want and attach files if you have any. Mention knowledge
with \`@\` if you like, then check the cost preview and start. The **Agents** view shows the team
forming live, and the chat shows the result and any files the team produced. Ask a follow-up to
continue with the whole context.
`,
  },
  {
    id: "concepts",
    group: "Start here",
    title: "Core concepts",
    summary: "Agents, spawning, messaging, budgets, the step journal.",
    body: `
| Concept | What it means |
|---|---|
| **Agent** | An actor with a role, a goal, capabilities (which decide its tools), a budget and a status (Thinking, Executing, Waiting, Completed…). Status changes are explicit transitions, and each one emits an event. |
| **Spawning** | An agent calls \`spawn_agent\`. The runtime checks depth, children per agent, team size, team-shape policy and budget, then creates the child. A child's tools and permissions can only narrow. |
| **Messaging** | Agents find each other with \`find_agents\` and talk with \`send_message\`, without relaying through the root. Messages are untrusted input and are persisted. |
| **Budgets** | Tokens, tool calls, dollars and time. A child's budget is carved from what its parent has left and reserved against it, so a whole tree never spends more than its root was given. |
| **Completion** | \`complete_task\` with a summary, artifacts, evidence and remaining work. The root decides when the whole goal is done. |
| **Durability** | Each step of an agent's turn is journaled before the next runs. After a crash, work resumes. A non-idempotent tool call that was cut off is reported as "outcome unknown", never repeated blindly. |
| **Organizations** | Everything belongs to one organization (a tenant). Another organization's agents, data and runs don't exist as far as you can see. Roles are Viewer, Member, Admin and Owner. |
`,
  },

  // ---------------------------------------------------------------- Use it
  {
    id: "tasks",
    group: "Use it",
    title: "Tasks",
    summary: "One-off work by a team, as a chat with follow-ups, files and a live agent graph.",
    body: `
A task is one goal handed to a team. It opens as a **chat**. You see your messages, the team's
reports, and the files it wrote (Word, PDF, Excel, slides, CSV, Markdown), each previewed in place.

- **Before:** the cost preview estimates the team and its spend, so you can set a budget.
- **During:** switch to **Agents** for the live graph, with spawns, messages, tool calls and statuses.
  You can switch the task's model mid-run and the whole team follows.
- **After:** ask a follow-up and the root picks up with its whole history. **Continue** a task that
  stopped partway with more budget. **Replay** or **fork** it from its step journal.

From code: \`POST /api/v1/tasks\` with a goal, then \`GET /api/v1/tasks/{id}/wait\` and \`…/result\`.
You can also start tasks over MCP, A2A or ACP (see Integrations).
`,
  },
  {
    id: "workspaces",
    group: "Use it",
    title: "Workspaces, pipelines and triggers",
    summary: "Reusable pipelines of agents, actions and scripts, run by schedules, webhooks, watches, forms and record changes.",
    body: `
A **workspace** is standing work: a pipeline that runs again and again, with its own connections,
safety policy, daily budget, knowledge and chat.

**Pipeline stages**

| Stage | Does | Model call? |
|---|---|---|
| **Agent** | A role with instructions and tools, autonomous within its step | yes |
| **Action** | One call (or one per item) of a connection's tool, with arguments built from the run's data (\`{{ input.email }}\`, \`{{ stages.lookup.rows }}\`, \`{{ vars.team }}\`) | no |
| **Script** | Python or JavaScript turning data into data, in a sandbox with no network | no |

Edit the pipeline in plain language ("add a fact checker in parallel with Research") or on the canvas.
Every change is a new version you can restore.

**Triggers** start runs:

- **Schedule**: every N minutes or a cron expression.
- **Webhook**: a secret URL; the payload is the run's input.
- **Watch**: a compiled check. The runtime calls a read-only tool on a schedule and evaluates a rule
  in code. The model runs only when something new matches, so routine checks cost zero tokens.
- **Form**: a page members fill in.
- **Record**: a record of a solution was created, updated or deleted (see Solutions).

Start from **Templates** (incident response, support triage, PR review, research reports and more),
or save a workspace as your organization's own template.
`,
  },
  {
    id: "studies",
    group: "Use it",
    title: "Studies",
    summary: "Research a question with your own data, with reviewed models and cited evidence.",
    body: `
A study answers a question from your own datasets (CSV, Excel, Parquet, JSON). Agents explore the
data, record hypotheses, and fit models in a sandbox. A second agent reviews every model, and results
are checked on held-out data. Simulated experiments are calibrated against the real data. The report
cites the evidence behind each finding.
`,
  },
  {
    id: "knowledge",
    group: "Use it",
    title: "Knowledge and skills",
    summary: "Shared memory agents search, and skills that teach your ways of working.",
    body: `
- **Shared memory** holds documents and facts for the organization or one workspace. Upload PDFs,
  Word, Excel, slides or text, or type facts. Agents search it by meaning and by words (pgvector),
  and \`@knowledge:name\` points them at something specific.
- **Skills** use the Agent Skills format: a \`SKILL.md\` with a name, a description and instructions.
  Agents see each skill's one-line description and load the rest only when their work matches. A
  skill is instructions only: it never grants tools, budget or access.
`,
  },
  {
    id: "integrations",
    group: "Use it",
    title: "Integrations and plugins",
    summary: "Connect tools and channels, serve them as MCP, and call Aktor from other systems.",
    body: `
A **plugin** adds an integration, and you install it into a workspace (or a task) as a
**connection**. Agents see its tools as \`connection__tool\`. Secrets go to an encrypted vault and
are never shown to agents.

- **Built in:** MCP servers, any REST API (endpoints by hand or from an OpenAPI document), Slack,
  Twilio SMS, email (SMTP, Brevo, SendGrid), Telegram, Google Sheets, BigQuery, Airtable, Zoho CRM
  and Sentry.
- **Remote plugins:** your own integration in any language, as a small HTTP service the runtime
  calls with a shared secret. The Python SDK (\`aktor.Plugin\`) and the TypeScript SDK
  (\`@aktor/sdk/plugin\`) include the server. See *Remote plugins* below.
- **Global connectors:** set an integration up once for the organization (**Connectors** in the
  sidebar) and link it from any workspace, workflow or solution, and from tasks if an admin allows
  it. Linked connections show a **Global** badge, and global connector ids start with \`gc-\`.
  Settings and secrets stay with the connector, so a rotated key reaches every link.
- **MCP gateway:** serve a connection to Claude Code or n8n as an MCP server; the credential stays in the vault.
- **Notifications and inbound:** run results, alerts and approvals reach people by Slack, SMS, email
  or Telegram. Replies like "approve A1" come back the same way.

**Aktor from other systems:** an MCP server at \`/mcp\` (\`run_goal\`, \`get_task_status\`…), an A2A
agent at \`/a2a\`, an ACP endpoint at \`/acp\` (with the \`aktor-acp\` stdio bridge), completion
webhooks, and the REST API.
`,
  },
  {
    id: "safety",
    group: "Use it",
    title: "Safety, approvals and the audit log",
    summary: "Autonomy levels, rules that need a person's approval, and a tamper-evident log.",
    body: `
- **Autonomy:** Autonomous, SemiAutonomous (external writes wait for approval) or Supervised.
- **Rules:** tool patterns (\`billing__*\`, \`data__delete_*\`) with Allow, Deny or RequireApproval,
  for reads, writes or non-idempotent calls. An approval parks the agent's call durably. It runs
  exactly once if approved, and never if rejected or expired.
- **Organization policy:** rules and team limits that apply to every workspace and task. A
  workspace can tighten them, never loosen them.
- **Team shape:** limits on team size, fan-out per level, who may spawn, and duplicate roles.
- **Audit log:** every tool call, approval, policy change and command goes into a per-workspace
  SHA-256 hash chain. Editing, inserting or deleting a record breaks verification.
- **Sandboxes:** shell commands run in a network-less, resource-capped container. File tools stay
  inside the task's folder. Agent HTTP can't reach private networks.
`,
  },

  // ---------------------------------------------------------------- Build on Aktor
  {
    id: "solutions",
    group: "Build on Aktor",
    title: "Solutions",
    summary: "Package a business application, with its data, assistant and evals, and deploy it as one unit.",
    body: `
A **solution** is your own agentic product built on Aktor: a maintenance desk, a helpdesk, a
recruiting pipeline. It's a folder of plain files, installed into an organization as one unit:

\`\`\`
my-app/
  aktor.yaml                 the manifest: entities, workspaces, assistant, seed, webhooks, evals
  workspaces/*.yaml          pipelines, triggers, safety, budgets
  skills/<name>/SKILL.md     know-how for its agents
  knowledge/*.md             reference text they search
  evals/*.yaml               expected behaviour, checked for real
\`\`\`

\`\`\`yaml
format: 1
name: property-mgmt
title: Property management
version: 1.2.0
requires_platform: ">=1.2.0"
inputs:
  - { key: company_name, default: Acme Properties }     # workspaces see {{ vars.company_name }}
connectors:
  - { name: sms, plugin: twilio-sms }                    # secrets are asked for at install, never packaged
entities: { … }                                          # the data model
workspaces: [ { file: workspaces/maintenance-desk.yaml } ]
assistant: { … }                                         # what your customers talk to
evals: [ { file: evals/urgent-leak.yaml } ]
\`\`\`

**Build it in the dashboard:** **Solutions → New solution** opens the builder. It has forms for the
data model, workspaces, triggers, the assistant, connectors, knowledge, skills and evals, checked
against the server as you go. **Deploy** installs it from the page, with no files and no CLI.
**Package (.zip)** downloads the same package for git or the CLI, and **Edit YAML** lets you
change the text and apply it back to the forms. **Edit in builder** opens an installed solution.

A solution never needs changes to Aktor. It's assembled from the same primitives you'd use by hand,
under the same quotas, policies and audit. A package can't grant itself more than an Admin could
configure.

**Upgrades** change only what the new version changed. Workspaces are matched by key. A pipeline
gets a new version, and runs in progress finish on the old one. Webhooks keep their URLs. Edits
people made in a workspace survive an upgrade that didn't touch them. Every install and upgrade is
in the install's history. Two complete examples live in \`samples/solutions\`: **property-mgmt**
and **helpdesk**.
`,
  },
  {
    id: "builder",
    group: "Build on Aktor",
    title: "The builder: no code needed",
    summary: "Make, check and deploy a solution with forms. Download the package if you want it.",
    body: `
**Solutions → New solution** opens the builder. **Edit in builder** on an installed solution opens
that version.

| Section | What you set |
|---|---|
| Basics | Title, name, version (with +patch/+minor), description |
| Data model | Entities and fields: type, required, indexed, enum values, which entity a ref points at. Renaming an entity updates everything that names it. |
| Workspaces | Goal, data access per entity, autonomy, connectors, agent and action stages with "runs after", and triggers: a record change, a schedule, a webhook |
| Assistant | Instructions, greeting, data access, limits, allowed sites, monthly cost cap, your own sign-in |
| Inputs & connectors | Values asked for at install; integrations picked from this server's plugins (secrets are asked for at deploy), or backed by one of your global connectors |
| Knowledge & skills | Reference text agents search; skills in the Agent Skills format |
| Evals | Expected status, what the summary mentions, records written |
| Review & deploy | The \`aktor.yaml\` it makes, the full check, downloads |

- **Checked as you type:** it uses the same validation the CLI and every deploy use. Each problem
  shows in its section, with a count in the left nav.
- **Deploy** installs it from the page (or upgrades it, when that name is installed). It asks for
  inputs and secrets, then shows any webhook URLs it created. Nothing has to be downloaded.
- **Package (.zip)** downloads \`aktor.yaml\` and its skill files, for git or the CLI.
  **Edit YAML** lets you change the text and apply it back to the forms.
- Drafts are saved in your browser until you deploy or discard them.

**Design with AI** (bottom right) is a chat about the solution. Describe the whole app, or one
change ("add a Vendor entity with name, phone and hourly rate", "add an eval for each workspace").
It can change every section. Each answer lists the changes, already checked against the server;
nothing changes until you press **Apply**, and an applied change can be undone. It works on the
draft as it is now, your own edits included, and asks when a decision is yours to make.

Type \`@\` in the chat, or in any box agents read (goals and instructions, trigger instructions,
eval inputs), to name a stage (\`@triage\`), a skill (\`@skill:handling-requests\`), knowledge
(\`@knowledge:refund-policy\`) or a connector (\`@connector:sms\`). Each box offers only what its
agents can use. Mentioning a connector connects it to that workspace, and renames carry over to
mentions.

The CLI and SDKs are alternatives for people who want them; anything made in one opens in the other.
`,
  },
  {
    id: "data",
    group: "Build on Aktor",
    title: "Data: entities, records and record triggers",
    summary: "A typed data model your agents work on, with checked writes, triggers and webhooks.",
    body: `
\`\`\`yaml
entities:
  Ticket:
    fields:
      subject:   { type: string, required: true }
      priority:  { type: enum, values: [low, normal, urgent], default: normal, indexed: true }
      lease:     { type: ref, entity: Lease }
      requester: { type: end_user }          # set by the runtime: whose record it is
      opened_at: { type: datetime, indexed: true }
\`\`\`

Field types: \`string text integer number boolean date datetime enum ref end_user json\`. Every
write is checked against them, whoever writes it. Unknown fields are refused, types are normalized,
and refs must point at a record the writer can see.

**Agents** get a \`data\` connection with \`data__list_ticket\`, \`get_\`, \`create_\`, \`update_\`
and \`delete_\` tools, limited to the access each workspace declares (\`data: { Ticket: write }\`).
Creates are idempotent, so a replay after a crash finds the record it made. Filters accept relative
dates like \`today+30d\`. All filtering and sorting runs in SQL, and \`indexed: true\` fields get a
database index.

**Record triggers** start a run when a record changes:

\`\`\`yaml
triggers:
  - { kind: Record, name: Urgent tickets, entity: Ticket, on: [created, updated],
      rule: { conditions: [{ field: priority, op: "==", value: urgent }] } }
\`\`\`

Each change fires each matching trigger once. A workspace's own runs don't re-trigger it, so it
can't feed itself.

**Your backend** uses \`/api/v1/solutions/{name}/records/{Entity}\`: \`GET\` with
\`?where.status=open&filter=opened_at<=now-2h\`, \`POST\` with an \`Idempotency-Key\`, \`PATCH\`
with \`If-Match\`, and \`DELETE\`. It gets **signed webhooks** for the changes it subscribes to.
`,
  },
  {
    id: "end-users",
    group: "Build on Aktor",
    title: "End users, the assistant and the embed widget",
    summary: "Put agents in front of your own customers, each reaching only their own data.",
    body: `
Your customers aren't members of your organization. They sign in to *your* app, and Aktor accepts
one of two credentials for them on the **apps API**:

1. **An Aktor end-user token.** Your backend calls
   \`POST /api/v1/solutions/{name}/end-user-tokens\` with \`{ user_id, name }\` and gets a
   short-lived \`aeu_…\` token.
2. **Your own sign-in token (JWT).** Declare your identity provider, and Aktor checks your JWTs
   against its published keys (RS/PS/ES algorithms):

\`\`\`yaml
end_users:
  jwt:
    issuer: https://auth.example.com/
    audience: my-app
    jwks_url: https://auth.example.com/.well-known/jwks.json
    user_claim: sub
\`\`\`

**The assistant** is the agent your customers talk to:

\`\`\`yaml
assistant:
  name: Resident helper
  instructions_file: assistant.md
  data: { Ticket: write, Lease: read }
  allowed_origins: ["https://app.example.com"]
  max_messages_per_minute: 10
  monthly_cost_limit_usd_per_user: 2.00
\`\`\`

Each conversation's tools are **bound to that end user**. For entities with an \`end_user\` field,
it sees, changes and links only that user's records, whatever a prompt says. Rate limits hold
across every server.

**Embed it** on your site:

\`\`\`html
<script src="https://YOUR-AKTOR/embed.js" data-install="sol-…" data-token-url="/api/aktor-token" async></script>
\`\`\`

A workspace's webhook triggers have secret URLs, shown only when they're created: in the CLI's
install output, in the deploy dialog, and in the workspace's chat. They're never shown in the
triggers list, and they stay the same across upgrades.

The widget runs in a shadow DOM, follows light and dark mode, refreshes expired tokens through your
URL, and keeps the conversation across page loads. You can also call
\`/api/v1/apps/{installId}/conversations\` directly, or use \`AktorApp\` from the SDKs.
`,
  },
  {
    id: "registry",
    group: "Build on Aktor",
    title: "The registry",
    summary: "Publish signed packages, find others' public ones, install by name.",
    body: `
\`\`\`bash
aktor publish --public            # publish this version
aktor search maintenance          # find packages
aktor install property-mgmt       # install the latest (or name@1.2.0); run again to upgrade
aktor yank property-mgmt 1.2.1 --reason "broken migration"
\`\`\`

- A package name belongs to the organization that first published it.
- Versions are immutable and only go up. A bad version can be **yanked**: installs of "latest" skip
  it, and existing installs keep working.
- **Organization** visibility (the default) lists a package for your organization only; **public**
  lists it for every organization on the server.
- Each version is stored with its SHA-256 and the registry's signature, and installs verify both. A
  package altered after publishing is refused.

In the dashboard, **Solutions** lists the registry with an *Install* button, and an installed
solution's page has *Publish to registry*.
`,
  },
  {
    id: "usage",
    group: "Build on Aktor",
    title: "Usage and billing your customers",
    summary: "What a solution cost, per end user, workspace or day.",
    body: `
\`GET /api/v1/solutions/{name}/usage?by=end_user\` (or \`workspace\`, \`day\`, with \`from\`/\`to\`
and \`format=csv\`) reports tokens and dollars from the runtime's own metering, the same numbers
your plan is metered by. Each end user is charged for their conversations; workspace runs belong to
no one end user. Use it to bill your customers, and cap them with
\`assistant.monthly_cost_limit_usd_per_user\`. Over the cap, a user gets 402 until next month.

\`\`\`bash
aktor usage property-mgmt --by end_user --csv > october.csv
\`\`\`

The **Usage** tab on a solution's page shows the same report.
`,
  },
  {
    id: "evals",
    group: "Build on Aktor",
    title: "Evals",
    summary: "Expected behaviour, checked by running the workspace for real; fails CI on regressions.",
    body: `
\`\`\`yaml
name: urgent leak is triaged
workspace: maintenance-desk
input: "Ticket created: water is pouring through the ceiling."
expect:
  status: Completed
  summary_contains: [urgent]
  records:
    Ticket: { at_least: 1, where: { priority: urgent } }
\`\`\`

\`aktor test\` (or the **Evals** tab) starts a real run for each case, waits, and checks the status,
the summary, and the records the run wrote. It exits with code 1 if any fail, so it fits CI. With
the Mock model, evals check the plumbing; with a real model, they check behaviour.
`,
  },
  {
    id: "cli",
    group: "Build on Aktor",
    title: "The aktor CLI and the SDKs",
    summary: "Scaffold, validate, deploy, test, publish; clients for TypeScript and Python.",
    body: `
The CLI is for people who prefer the terminal or want packages in git. Everything it does
(validate, deploy, upgrade) can also be done from the dashboard's builder, with no CLI and no YAML.

\`\`\`bash
export AKTOR_URL=http://localhost:5080 AKTOR_API_KEY=ak_...    # an Admin key: Settings → API keys
npx aktor init my-app          # a working package to start from
npx aktor validate             # every problem, before anything is installed
npx aktor deploy               # install, or upgrade (--dry-run, --force, --input k=v, --secret conn.key=v)
npx aktor dev                  # redeploy on every save
npx aktor test                 # run the evals
npx aktor records my-app Request --filter "status==new"
npx aktor token my-app customer-42
npx aktor publish | search | install | yank | usage | status | uninstall
\`\`\`

**TypeScript** (\`@aktor/sdk\`): \`new Aktor({ apiKey })\` gives you \`tasks\`, \`workspaces\`,
\`solutions\` (deploy, records, usage, end-user tokens) and \`registry\`. \`AktorApp\` is the
end-user side.

**Python** (\`aktor\`, no dependencies): \`Aktor\`, \`AktorApp\`, \`bundle_directory\`, and
\`Plugin\` for remote plugins.
`,
  },
  {
    id: "remote-plugins",
    group: "Build on Aktor",
    title: "Remote plugins",
    summary: "Your own integration in any language, as an HTTP service the runtime calls.",
    body: `
The runtime POSTs JSON to \`/validate\`, \`/tools/list\`, \`/tools/call\`, \`/notify\` and
\`/inbound\` under your plugin's URL, with \`Authorization: Bearer <shared secret>\`. Each call
carries the connection's config and its credentials from the vault. Tool calls also carry an
\`idempotency_key\` to pass to the system you call.

\`\`\`js
import { Plugin } from "@aktor/sdk/plugin";

new Plugin("crm")
  .tool("find_customer", { description: "Find a customer by email.", sideEffects: "read_only",
    inputSchema: { type: "object", properties: { email: { type: "string" } }, required: ["email"] } },
    async ({ email }, ctx) => lookup(email, ctx.credentials.api_key))
  .listen({ port: 8787, token: process.env.PLUGIN_TOKEN });
\`\`\`

\`\`\`python
from aktor import Plugin
plugin = Plugin("crm")

@plugin.tool(description="Find a customer by email.", side_effects="read_only")
def find_customer(args, ctx):
    return lookup(args["email"], ctx.credentials["api_key"])

plugin.serve(port=8787, token=os.environ["PLUGIN_TOKEN"])
\`\`\`

Add it to a workspace as a **Remote plugin** connection with its URL and the secret. Declare
\`side_effects\` honestly: read-only tools may be re-run after a crash, and others never are.
`,
  },

  // ---------------------------------------------------------------- Reference
  {
    id: "api",
    group: "Reference",
    title: "API",
    summary: "The versioned REST contract, credentials and conventions.",
    body: `
\`/api/v1/...\` is the stable contract: within v1, changes are additive only. The OpenAPI
description is at \`/openapi/v1.json\`.

| Credential | Header | Reaches |
|---|---|---|
| API key | \`Authorization: Bearer ak_…\` | your organization, by the key's role |
| End-user token or your JWT | \`Authorization: Bearer aeu_…\` / your JWT | \`/api/v1/apps/{installId}/…\` only |

| Area | Endpoints |
|---|---|
| Tasks | \`POST /tasks\`, \`POST /tasks/preview\`, \`GET /tasks/{id}\`, \`/wait\`, \`/result\`, \`/chat\`, \`POST /tasks/{id}/messages\` (a follow-up), \`/continue\`, \`/replay\`, \`/cancel\` |
| Workspaces | \`/workspaces\`, \`/workspaces/{id}/runs\`, \`/triggers\`, \`/connections\`, \`/policy\`, \`/approvals\`, \`/audit\` |
| Solutions | \`POST /solutions/validate\`, \`POST /solutions/parse\`, \`GET /solutions/{id}/package\`, \`POST /solutions\` (bundle or \`package\`), \`PUT /solutions/{id}\`, \`DELETE …?purge=true\`, \`/evals\`, \`/usage\`, \`/publish\`, \`/end-user-tokens\`, \`/records/{Entity}\` |
| Registry | \`GET/POST /registry/packages\`, \`GET /registry/packages/{name}[/{version}]\`, \`POST …/{version}/yank\` |
| Apps (end users) | \`GET /apps/{id}\`, \`/conversations\`, \`/conversations/{id}\`, \`/conversations/{id}/messages\`, \`/records/{Entity}\` |
| Live events | \`GET /ws/events\` (Server-Sent Events) |

Errors are \`{ "error": "…" }\`, with \`"errors": [...]\` listing every problem on validation.
A 404 also covers "exists, but in another organization".
`,
  },
  {
    id: "guarantees",
    group: "Reference",
    title: "What the runtime guarantees",
    summary: "Limits that hold whatever the model decides, each proven by a test.",
    body: `
- **Budgets** can't be exceeded by a team, and only a person adds more.
- **Permissions** only narrow from parent to child.
- **Durability:** an interrupted run resumes; a non-idempotent call is never repeated blindly.
- **Approvals:** a call marked "ask" never runs before a person approves it, and runs once after.
- **Tenant isolation** in messaging, discovery, memory, SQL, records, and every way in.
- **End users** reach only their own conversations and records, enforced in the tools themselves.
- **Every record write is checked**, and each change fires a trigger once.
- **Registry packages** are verified against their digest and signature at install.
- **The audit chain** detects edits, insertions and deletions.

Each guarantee names the code that enforces it and the test that proves it, in \`docs/guarantees.md\`.
`,
  },
  {
    id: "testing",
    group: "Reference",
    title: "Testing by hand",
    summary: "A walkthrough to check solutions work on your own server.",
    body: `
Each step builds on the last; \`docs/manual-testing.md\` has the full version, with checks for
tasks, studies and workspaces too. With the Mock model you check the plumbing; with a real model,
the judgement.

1. **Build and deploy with no files:** Solutions → New solution, change something, break it on
   purpose (an enum with no values), fix it, Deploy.
2. **Install a sample:** \`aktor deploy samples/solutions/property-mgmt\`. The Data tab shows its
   seed records, and Workspaces shows its two workspaces.
3. **A record change starts a run:** create a Ticket on the Data tab, then watch the Maintenance
   desk start a run.
4. **End users:** on the End users tab, issue a token for \`demo-resident\`, open the widget and
   chat. A second user, \`demo-resident-2\`, never sees the first one's data.
5. **Usage:** the Usage tab shows cost per end user, with a CSV export.
6. **Evals and upgrades:** run the evals, then Edit in builder, change a stage, +patch, Upgrade.
   History shows it.
7. **Webhooks:** install the helpdesk, take the contact-form URL from the deploy dialog (or the
   Intake workspace's chat), POST a ticket, and see it reach \`awaiting_review\`.
8. **Registry:** Publish to registry, then install a package from the Registry list.
9. **Clean up:** \`aktor uninstall <name> --purge\`.
`,
  },
  {
    id: "configuration",
    group: "Reference",
    title: "Configuration",
    summary: "The settings you're most likely to change.",
    body: `
| Setting | What it does |
|---|---|
| \`LLM_PROVIDER\`, \`LLM_MODEL\`, \`LLM_API_KEY\` | The server's default model (organizations add their own under Settings → Models) |
| \`AUTH_MODE\` | \`accounts\` (sign-in, organizations, roles) or \`disabled\` (one local owner) |
| \`SECRETS_MASTER_KEY\` | The key that encrypts the vault and signs bindings, tokens and packages. Back it up. |
| \`Silo__Storage\`, \`Silo__Clustering\` | Postgres-backed durable state and multi-silo clustering, or in-memory for a demo |
| \`RuntimeLimits__*\` | Max depth, children per agent, total and active agents |
| \`TaskBudgetCeiling__*\` | The most any task may be given, whoever asks |
| \`Solutions__MaxEndUserTokenLifetime\` | The longest an end-user token may live (default 7 days) |
| \`CALLBACKS_ALLOW_PRIVATE_NETWORKS\` | Allow webhooks to private addresses (self-hosted setups) |
| \`PUBLIC_BASE_URL\` | Your public URL, for inbound channel webhooks |
| \`EMBEDDING_PROVIDER\` | Semantic search for shared memory (Ollama or OpenAI embeddings) |

The full list, with defaults, is in \`.env.example\` and \`appsettings.json\`.
`,
  },
];
