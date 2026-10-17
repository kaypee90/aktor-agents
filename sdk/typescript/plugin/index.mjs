// Remote plugins in Node (docs/plugins.md#remote-plugins): an Aktor plugin as a small HTTP
// service. The runtime POSTs /validate, /tools/list, /tools/call, /notify and /inbound with a
// shared secret; this module answers them. No dependencies (node:http, node:crypto).
//
//   import { Plugin, ToolError } from "@aktor/sdk/plugin";
//
//   const plugin = new Plugin("crm");
//   plugin.tool("find_customer", {
//     description: "Find a customer by email.",
//     sideEffects: "read_only",
//     inputSchema: { type: "object", properties: { email: { type: "string" } }, required: ["email"] },
//   }, async (args, ctx) => ({ name: "Ada", plan: "pro" }));
//   plugin.listen({ port: 8787, token: process.env.PLUGIN_TOKEN });
//
// ctx carries the connection's config and credentials (from the vault) and, for tool calls, the
// idempotency key to pass to the system you call, so a repeated call can't act twice.

import { createServer } from "node:http";
import { timingSafeEqual } from "node:crypto";

/** Throw from a tool for a clean failure the agent sees (with an optional machine-readable code). */
export class ToolError extends Error {
  /** @param {string} message @param {string} [code] */
  constructor(message, code = "tool_error") {
    super(message);
    this.name = "ToolError";
    this.code = code;
  }
}

const SIDE_EFFECTS = new Set(["read_only", "idempotent", "non_idempotent"]);

function context(body) {
  const c = body.connection ?? {};
  return {
    connectionId: c.id,
    connectionName: c.name,
    workspaceId: c.workspace_id,
    config: c.config ?? null,
    credentials: body.credentials ?? null,
    idempotencyKey: body.idempotency_key ?? null,
    agentId: body.agent_id ?? null,
    taskId: body.task_id ?? null,
  };
}

export class Plugin {
  /** @param {string} [name] */
  constructor(name = "plugin") {
    this.name = name;
    this.tools = new Map();
    this.onValidate = null;
    this.onNotify = null;
    this.onInbound = null;
  }

  /**
   * Registers a tool. `sideEffects` is "read_only", "idempotent" or "non_idempotent" (the safe
   * default: the runtime never repeats it blindly after a crash).
   * @param {string} name
   * @param {{ description?: string, inputSchema?: object, sideEffects?: string }} options
   * @param {(args: any, ctx: any) => any} handler
   */
  tool(name, options, handler) {
    const sideEffects = options.sideEffects ?? "non_idempotent";
    if (!SIDE_EFFECTS.has(sideEffects)) throw new Error("sideEffects is read_only, idempotent or non_idempotent");
    this.tools.set(name, { handler, description: options.description ?? name, inputSchema: options.inputSchema ?? { type: "object", properties: {} }, sideEffects });
    return this;
  }

  /** `fn(ctx) => [ok, message]`: checks a new connection (e.g. its credentials). */
  validate(fn) { this.onValidate = fn; return this; }

  /** `fn(notification, ctx)`: delivers a notification; throw to have it retried. */
  notify(fn) { this.onNotify = fn; return this; }

  /** `fn(request, ctx) => { sender_id, text, message_id, ... } | null`: turns an inbound request into a message. */
  inbound(fn) { this.onInbound = fn; return this; }

  /** Answers one call: `[status, body]`. Exposed for tests and other servers. */
  async handle(path, body) {
    const ctx = context(body);
    switch (path) {
      case "/validate": {
        if (!this.onValidate) return [200, { ok: true, message: `${this.name}: ${this.tools.size} tool(s).` }];
        const [ok, message] = await this.onValidate(ctx);
        return [200, { ok: !!ok, message }];
      }
      case "/tools/list":
        return [200, { tools: [...this.tools].map(([name, t]) => ({ name, description: t.description, input_schema: t.inputSchema, side_effects: t.sideEffects })) }];
      case "/tools/call": {
        const tool = this.tools.get(body.tool);
        if (!tool) return [200, { ok: false, error: `No tool '${body.tool}'.`, code: "unknown_tool" }];
        try {
          return [200, { ok: true, result: (await tool.handler(body.arguments ?? {}, ctx)) ?? null }];
        } catch (err) {
          if (err instanceof ToolError) return [200, { ok: false, error: err.message, code: err.code }];
          throw err;
        }
      }
      case "/notify": {
        if (!this.onNotify) return [200, { delivered: false, error: "This plugin doesn't deliver notifications.", retryable: false }];
        try {
          await this.onNotify(body, ctx);
          return [200, { delivered: true }];
        } catch (err) {
          return [200, { delivered: false, error: err.message, retryable: !(err instanceof ToolError && err.code === "permanent") }];
        }
      }
      case "/inbound": {
        if (!this.onInbound) return [200, { is_message: false }];
        const message = await this.onInbound(body, ctx);
        return [200, message ? { is_message: true, ...message } : { is_message: false }];
      }
      default:
        return [404, { error: `No such endpoint '${path}'.` }];
    }
  }

  /** An http.Server answering the protocol; calls without the shared token are refused. */
  server({ token }) {
    if (!token) throw new Error("A shared token is required: the runtime sends it with every call.");
    const expected = Buffer.from(`Bearer ${token}`);
    return createServer(async (req, res) => {
      const answer = (status, body) => {
        const data = JSON.stringify(body);
        res.writeHead(status, { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(data) });
        res.end(data);
      };
      if (req.method !== "POST") return answer(405, { error: "POST only." });
      const given = Buffer.from(req.headers.authorization ?? "");
      if (given.length !== expected.length || !timingSafeEqual(given, expected)) return answer(401, { error: "Wrong or missing token." });
      let raw = "";
      for await (const chunk of req) {
        raw += chunk;
        if (raw.length > 5_000_000) return answer(413, { error: "Too large." });
      }
      let body;
      try { body = raw ? JSON.parse(raw) : {}; } catch { return answer(400, { error: "The body isn't JSON." }); }
      try {
        const [status, out] = await this.handle(new URL(req.url ?? "/", "http://x").pathname.replace(/\/$/, ""), body);
        answer(status, out);
      } catch (err) {
        console.error(err);
        answer(500, { error: `${err?.name ?? "Error"}: ${err?.message ?? err}` });
      }
    });
  }

  /** Serves the plugin; resolves with the server once it listens. */
  listen({ port = 8787, host = "127.0.0.1", token }) {
    const server = this.server({ token });
    return new Promise((resolve) => server.listen(port, host, () => {
      const address = server.address();
      console.error(`${this.name} plugin on http://${host}:${typeof address === "object" ? address?.port : port} (${this.tools.size} tools)`);
      resolve(server);
    }));
  }
}
