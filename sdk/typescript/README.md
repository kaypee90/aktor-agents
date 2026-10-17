# @aktor/sdk

A typed TypeScript client for the Aktor Agents platform API. It has no dependencies and works in
Node 18+, Deno, Bun and browsers.

```bash
npm install @aktor/sdk   # or, from this repo: cd sdk/typescript && npm run build
```

Create an API key under **Settings → API keys** in the dashboard. A key belongs to one
organization and has a role (Viewer, Member or Admin).

```ts
import { Aktor } from "@aktor/sdk";

const aktor = new Aktor({ apiKey: process.env.AKTOR_API_KEY!, baseUrl: "http://localhost:5080" });

// A long-running workspace: its coordinator starts on the goal right away.
const { workspace_id } = await aktor.workspaces.create({
  name: "Support triage",
  goal: "For every new support ticket, classify its urgency and alert me about urgent ones.",
});

// Give the agents a new instruction at any time. Retries are safe: one client_message_id is
// generated per call.
await aktor.workspaces.send(workspace_id, "Also watch for refund requests.");

// Require approval before anything irreversible.
await aktor.workspaces.policy.update(workspace_id, {
  autonomy: "SemiAutonomous",
  rules: [{ tool_pattern: "billing__*", applies: "Writes", decision: "RequireApproval" }],
  approval_timeout_hours: 24,
});

for (const a of await aktor.workspaces.approvals.list(workspace_id, "Pending")) {
  await aktor.workspaces.approvals.decide(workspace_id, a.code, true, "looks right");
}

// Live events for the whole organization (or one workspace, with taskId).
const controller = new AbortController();
for await (const event of aktor.events.stream({ taskId: workspace_id, signal: controller.signal })) {
  console.log(event.type, event.summary);
}

// This month's metered usage against the plan.
const { usage, plan, quota } = await aktor.billing.get();
```

The client does the following for you:
- **Errors** are thrown as `AktorError`, with the HTTP `status` and the server's message.
- **Retries.** Reads, `PUT`s, `DELETE`s, message sends and approval decisions are retried on
  429, 5xx and network errors, with backoff that honours `Retry-After`. Creates are never retried
  automatically.
- **Reconnects.** `events.stream` reconnects after a dropped connection until you abort it.

Everything the SDK covers is also plain REST. The full description is at `/openapi/v1.json` on
your server.

## Solutions: build and ship your own agentic app

A solution is a business app packaged as a folder (`aktor.yaml` plus workspaces, skills, knowledge
and evals); see [docs/solutions.md](../../docs/solutions.md). The **`aktor` CLI** builds and ships it:

```bash
export AKTOR_URL=http://localhost:5080 AKTOR_API_KEY=ak_...   # an Admin key
npx aktor init my-app && cd my-app
npx aktor validate          # check it against the server
npx aktor deploy            # install, or upgrade the installed version (--dry-run, --force, --input k=v, --secret conn.key=v)
npx aktor dev               # redeploy on every change
npx aktor test              # run its evals for real; exit 1 if any fail (CI)
npx aktor records my-app Request --filter "status==new"
npx aktor token my-app customer-42 --name Sam   # an end-user token, plus the embed snippet
npx aktor usage my-app --by end_user --csv      # what it cost, for billing your customers
npx aktor publish --public                      # share this version through the registry
npx aktor search | install name@1.0.0 | yank name 1.0.1
```

From code, your backend works with the solution's records and issues end-user tokens:

```ts
const aktor = new Aktor({ apiKey: process.env.AKTOR_API_KEY!, baseUrl: "http://localhost:5080" });

// Writes are checked against the data model, and fire record triggers and webhooks.
const ticket = await aktor.solutions.records.create("property-mgmt", "Ticket", { issue: "No hot water" }, { idempotencyKey: "req-81" });
const soon = await aktor.solutions.records.list("property-mgmt", "Lease", { where: { status: "active" }, filters: ["ends_on<=today+60d"] });

// After your own sign-in: a short-lived token that reaches this user's data only.
// (Or declare end_users.jwt in the manifest and use your identity provider's tokens directly.)
const { token } = await aktor.solutions.endUserToken("property-mgmt", "resident-4b", { name: "Sam" });

// Cost per end user this month, and the registry.
const { lines } = await aktor.solutions.usage("property-mgmt", { by: "end_user" });
await aktor.registry.publish(bundle, "public");
await aktor.solutions.installPackage("helpdesk@1.0.0");
```

Your customers' side uses `AktorApp` with that token (or the `/embed.js` widget):

```ts
import { AktorApp } from "@aktor/sdk";

const app = new AktorApp({ installId: "sol-…", token, baseUrl: "https://agents.example.com" });
const { conversation_id } = await app.conversations.start("The kitchen tap is leaking");
const reply = await app.conversations.waitForReply(conversation_id);
console.log(reply.messages.at(-1)?.text);
const myTickets = await app.records("Ticket");
```

## Remote plugins in Node

`@aktor/sdk/plugin` serves your own integration as an Aktor remote plugin (docs/plugins.md#remote-plugins):

```js
import { Plugin, ToolError } from "@aktor/sdk/plugin";

new Plugin("crm")
  .tool("find_customer", {
    description: "Find a customer by email.",
    sideEffects: "read_only",
    inputSchema: { type: "object", properties: { email: { type: "string" } }, required: ["email"] },
  }, async ({ email }, ctx) => {
    const customer = await lookup(email, ctx.credentials.api_key);   // credentials come from the vault
    if (!customer) throw new ToolError("No customer with that email.", "not_found");
    return customer;
  })
  .listen({ port: 8787, token: process.env.PLUGIN_TOKEN });
```

## Tasks from other systems

`aktor.tasks.create(goal)` starts a one-off task. The REST endpoint behind it takes more than the
SDK method exposes today. Call it directly for those options (see
[docs/integrations.md](../../docs/integrations.md)):

```ts
// Budget, your own correlation id, a completion webhook (signed with the secret), and team-shape rules.
const res = await fetch(`${baseUrl}/api/tasks`, {
  method: "POST",
  headers: { Authorization: `Bearer ${apiKey}`, "Content-Type": "application/json", "X-Correlation-Id": "n8n-run-42" },
  body: JSON.stringify({
    goal: "Research the market for AI property management software.",
    budget: { max_cost_usd: 3 },
    callback_url: "https://n8n.example.com/webhook/aktor-done",
    callback_secret: process.env.AKTOR_CALLBACK_SECRET,
    team_policy: { max_agents: 6 },
  }),
});
const { task_id, dashboard_url } = await res.json();

// Long poll: answers when the task finishes, or after the timeout with done=false.
const status = await (await fetch(`${baseUrl}/api/tasks/${task_id}/wait?timeout_seconds=120`, {
  headers: { Authorization: `Bearer ${apiKey}` },
})).json();
```

**MCP, A2A and ACP.** The same tasks are available to MCP clients at `/mcp`, A2A agents at
`/a2a`, and ACP clients through the bridge in this package:

```bash
# Node 22+: runs Aktor as an Agent Client Protocol agent over stdio (OpenClaw/acpx, Zed).
AKTOR_URL=https://your-aktor AKTOR_API_KEY=ak_… npx aktor-acp
```

## Tests

```bash
npm test                                      # unit tests (fake fetch)
AKTOR_E2E_URL=http://localhost:5081 npm test  # also end to end against a running server
```

The end-to-end tests sign up two organizations. They check that neither one's key can see,
instruct or stream the other's work.
