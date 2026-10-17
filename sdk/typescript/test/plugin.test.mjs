import { test } from "node:test";
import assert from "node:assert/strict";
import { Plugin, ToolError } from "../plugin/index.mjs";

const plugin = new Plugin("test")
  .tool("echo", { description: "Echo.", sideEffects: "read_only" }, (args, ctx) => ({ args, key: ctx.idempotencyKey, config: ctx.config, credentials: ctx.credentials }))
  .tool("broken", {}, () => { throw new ToolError("nope", "bad"); });

async function withServer(fn) {
  const server = plugin.server({ token: "secret" });
  await new Promise((r) => server.listen(0, "127.0.0.1", r));
  const url = `http://127.0.0.1:${server.address().port}`;
  const post = async (path, body, token = "secret") => {
    const res = await fetch(url + path, { method: "POST", headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` }, body: JSON.stringify(body) });
    return [res.status, await res.json()];
  };
  try { await fn(post); } finally { server.close(); }
}

test("lists tools with their side effects", () => withServer(async (post) => {
  const [, body] = await post("/tools/list", {});
  const tools = Object.fromEntries(body.tools.map((t) => [t.name, t]));
  assert.equal(tools.echo.side_effects, "read_only");
  assert.equal(tools.broken.side_effects, "non_idempotent");
}));

test("calls carry arguments, config, credentials and the idempotency key", () => withServer(async (post) => {
  const [, body] = await post("/tools/call", { tool: "echo", arguments: { x: 1 }, idempotency_key: "a:1", connection: { config: { region: "eu" } }, credentials: { api_key: "k" } });
  assert.deepEqual(body, { ok: true, result: { args: { x: 1 }, key: "a:1", config: { region: "eu" }, credentials: { api_key: "k" } } });
}));

test("tool errors are clean failures; unknown tools too", () => withServer(async (post) => {
  assert.deepEqual((await post("/tools/call", { tool: "broken" }))[1], { ok: false, error: "nope", code: "bad" });
  assert.equal((await post("/tools/call", { tool: "missing" }))[1].code, "unknown_tool");
}));

test("refuses calls without the token, and answers the defaults", () => withServer(async (post) => {
  assert.equal((await post("/tools/list", {}, "wrong"))[0], 401);
  assert.equal((await post("/validate", {}))[1].ok, true);
  assert.equal((await post("/notify", { text: "hi" }))[1].delivered, false);
  assert.equal((await post("/inbound", { body: "x" }))[1].is_message, false);
}));
