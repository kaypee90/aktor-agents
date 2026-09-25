// Unit tests with a fake fetch: auth, retries, errors, idempotent sends, SSE parsing.
import { test } from "node:test";
import assert from "node:assert/strict";
import { Aktor, AktorError } from "../dist/index.js";

function fakeFetch(responses) {
  const calls = [];
  const impl = async (url, init) => {
    calls.push({ url: String(url), init });
    const next = responses.shift();
    if (next instanceof Error) throw next;
    return typeof next === "function" ? next() : next;
  };
  return { impl, calls };
}

const json = (status, body) => new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

test("sends the API key and parses JSON", async () => {
  const { impl, calls } = fakeFetch([json(200, [{ workspace_id: "ws-1" }])]);
  const aktor = new Aktor({ apiKey: "ak_abc", baseUrl: "http://api.test/", fetch: impl });
  const list = await aktor.workspaces.list();
  assert.equal(list[0].workspace_id, "ws-1");
  assert.equal(calls[0].url, "http://api.test/api/workspaces");
  assert.equal(calls[0].init.headers.Authorization, "Bearer ak_abc");
});

test("retries reads on 5xx and 429, then succeeds", async () => {
  const { impl, calls } = fakeFetch([json(503, {}), new Response("", { status: 429, headers: { "retry-after": "0" } }), json(200, { ok: 1 })]);
  const aktor = new Aktor({ fetch: impl, maxRetries: 2 });
  assert.deepEqual(await aktor.billing.get(), { ok: 1 });
  assert.equal(calls.length, 3);
});

test("never retries a non-idempotent POST", async () => {
  const { impl, calls } = fakeFetch([json(500, { error: "boom" })]);
  const aktor = new Aktor({ fetch: impl });
  await assert.rejects(aktor.workspaces.create({ name: "x", goal: "y" }), (err) => err instanceof AktorError && err.status === 500 && err.message === "boom");
  assert.equal(calls.length, 1);
});

test("a message send is retried with the same client_message_id", async () => {
  const { impl, calls } = fakeFetch([new TypeError("network down"), json(200, { seq: 7 })]);
  const aktor = new Aktor({ fetch: impl });
  await aktor.workspaces.send("ws-1", "hello");
  assert.equal(calls.length, 2);
  const [a, b] = calls.map((c) => JSON.parse(c.init.body).client_message_id);
  assert.ok(a);
  assert.equal(a, b);
});

test("API errors surface the server's message and status", async () => {
  const { impl } = fakeFetch([json(404, { error: "Not found here" })]);
  const aktor = new Aktor({ fetch: impl, maxRetries: 0 });
  await assert.rejects(aktor.workspaces.get("ws-x"), (err) => err.status === 404 && err.message === "Not found here");
});

test("events.stream parses server-sent events across chunk boundaries", async () => {
  const frames = ['data: {"type":"AgentSpawned","sum', 'mary":"one"}\n\n', 'data: {"type":"AgentCompleted","summary":"two"}\r\n\r\n'];
  const body = new ReadableStream({
    start(controller) {
      for (const f of frames) controller.enqueue(new TextEncoder().encode(f));
      controller.close();
    },
  });
  const { impl } = fakeFetch([new Response(body, { status: 200, headers: { "content-type": "text/event-stream" } })]);
  const aktor = new Aktor({ fetch: impl });
  const seen = [];
  for await (const evt of aktor.events.stream({ reconnect: false })) seen.push(evt.summary);
  assert.deepEqual(seen, ["one", "two"]);
});
