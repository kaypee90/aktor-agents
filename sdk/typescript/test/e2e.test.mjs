// End to end against a running server (Mock LLM is fine). Skipped unless AKTOR_E2E_URL is set:
//   AKTOR_E2E_URL=http://localhost:5081 npm test
// It signs up two organizations and checks that each one's key can't see the other's work.
import { test } from "node:test";
import assert from "node:assert/strict";
import { Aktor, AktorError } from "../dist/index.js";

const base = process.env.AKTOR_E2E_URL;

async function signUp(email) {
  const res = await fetch(`${base}/api/auth/signup`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ email, password: "correct horse battery", organization: email.split("@")[0] }),
  });
  assert.equal(res.status, 200, await res.clone().text());
  const cookie = res.headers.get("set-cookie").split(";")[0];
  const keyRes = await fetch(`${base}/api/api-keys`, {
    method: "POST",
    headers: { "content-type": "application/json", cookie },
    body: JSON.stringify({ name: "e2e", role: "Admin" }),
  });
  assert.equal(keyRes.status, 200, await keyRes.clone().text());
  return { cookie, key: (await keyRes.json()).key };
}

test("two organizations are isolated end to end", { skip: !base }, async () => {
  const stamp = Date.now();
  const a = await signUp(`alice-${stamp}@example.com`);
  const b = await signUp(`bob-${stamp}@example.com`);
  const alice = new Aktor({ baseUrl: base, apiKey: a.key });
  const bob = new Aktor({ baseUrl: base, apiKey: b.key });

  assert.equal((await alice.me()).via, "api_key");

  const { workspace_id } = await alice.workspaces.create({ name: "Alice's", goal: "Keep an eye on things." });
  const ws = await alice.workspaces.waitFor(workspace_id, (w) => w.agents.length > 0);
  assert.equal(ws.workspace_id, workspace_id);

  // Bob can't see, list, instruct or change it, and can't reach its agents.
  await assert.rejects(bob.workspaces.get(workspace_id), (e) => e instanceof AktorError && e.status === 404);
  await assert.rejects(bob.workspaces.send(workspace_id, "hi"), (e) => e.status === 404);
  await assert.rejects(bob.workspaces.policy.update(workspace_id, { autonomy: "Autonomous", rules: [], approval_timeout_hours: 1 }), (e) => e.status === 404);
  await assert.rejects(bob.workspaces.audit.list(workspace_id), (e) => e.status === 404);
  await assert.rejects(bob.agents.get(ws.coordinator_agent_id), (e) => e.status === 404);
  assert.ok(!(await bob.workspaces.list()).some((w) => w.workspace_id === workspace_id));
  assert.ok(!(await bob.agents.list()).some((x) => x.agent_id === ws.coordinator_agent_id));

  // Alice's own view works, including the audit chain and metered usage.
  await alice.workspaces.policy.update(workspace_id, { autonomy: "SemiAutonomous", rules: [], approval_timeout_hours: 24 });
  assert.equal((await alice.workspaces.policy.get(workspace_id)).autonomy, "SemiAutonomous");
  assert.ok((await alice.workspaces.audit.verify(workspace_id)).valid);
  const billing = await alice.billing.get();
  assert.ok(billing.usage.llm_calls >= 1, JSON.stringify(billing.usage));
  assert.equal((await bob.billing.get()).usage.llm_calls, 0);

  // A revoked or made-up key is refused.
  await assert.rejects(new Aktor({ baseUrl: base, apiKey: "ak_000000000000_" + "x".repeat(43) }).workspaces.list(), (e) => e.status === 401);
});

test("the live event stream only carries the caller's organization", { skip: !base }, async () => {
  const stamp = Date.now();
  const a = await signUp(`carol-${stamp}@example.com`);
  const b = await signUp(`dave-${stamp}@example.com`);
  const carol = new Aktor({ baseUrl: base, apiKey: a.key });
  const dave = new Aktor({ baseUrl: base, apiKey: b.key });

  const controller = new AbortController();
  const seenByDave = [];
  const listening = (async () => {
    for await (const evt of dave.events.stream({ signal: controller.signal })) seenByDave.push(evt);
  })().catch(() => {});

  await new Promise((r) => setTimeout(r, 500));
  const { workspace_id } = await carol.workspaces.create({ name: "Carol's", goal: "Watch the weather." });
  await carol.workspaces.waitFor(workspace_id, (w) => w.agents.length > 0);
  await new Promise((r) => setTimeout(r, 1500));
  controller.abort();
  await listening;

  assert.ok(!seenByDave.some((e) => e.task_id === workspace_id || e.summary.includes("Carol's")), JSON.stringify(seenByDave.slice(0, 3)));
});
