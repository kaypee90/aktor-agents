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

## Tests

```bash
npm test                                      # unit tests (fake fetch)
AKTOR_E2E_URL=http://localhost:5081 npm test  # also end to end against a running server
```

The end-to-end tests sign up two organizations. They check that neither one's key can see,
instruct or stream the other's work.
