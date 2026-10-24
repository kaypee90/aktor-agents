// A tiny "customer site" for trying a solution's assistant locally (README.md).
//
// It plays the part of your product: visitors sign in here (any name, no password), and this
// backend exchanges that sign-in for an Aktor end-user token with the solution's app key. The page
// embeds the assistant with Aktor's widget. No dependencies: Node 18+ only.
//
//   cp .env.example .env    # fill in AKTOR_INSTALL_ID and AKTOR_APP_KEY
//   node server.mjs         # http://localhost:4000

import { createServer } from "node:http";
import { readFileSync, existsSync } from "node:fs";
import { randomUUID } from "node:crypto";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));

// .env next to this file (KEY=value lines); real environment variables win.
const envFile = join(here, ".env");
if (existsSync(envFile)) {
  for (const line of readFileSync(envFile, "utf8").split("\n")) {
    const m = /^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$/.exec(line);
    if (m && !line.trimStart().startsWith("#") && process.env[m[1]] === undefined) process.env[m[1]] = m[2].replace(/^["']|["']$/g, "");
  }
}

const AKTOR_URL = (process.env.AKTOR_URL || "http://localhost:5080").replace(/\/$/, "");
const INSTALL_ID = process.env.AKTOR_INSTALL_ID || "";
const APP_KEY = process.env.AKTOR_APP_KEY || "";
const PORT = Number(process.env.PORT || 4000);
const SITE_NAME = process.env.SITE_NAME || "Acme Rentals";

if (!INSTALL_ID || !APP_KEY) {
  console.error("Set AKTOR_INSTALL_ID and AKTOR_APP_KEY (see .env.example). Both are on the solution's Integrate tab in Aktor.");
  process.exit(1);
}

// Signed-in visitors, by session cookie. In memory: restarting signs everyone out.
const sessions = new Map();

/** Calls Aktor's apps API with the app key, from this server (never from the page). */
async function aktor(method, path, body) {
  const res = await fetch(`${AKTOR_URL}/api/v1/apps/${encodeURIComponent(INSTALL_ID)}${path}`, {
    method,
    headers: { Authorization: `Bearer ${APP_KEY}`, ...(body ? { "Content-Type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!res.ok) throw Object.assign(new Error(json?.error || `${method} ${path}: ${res.status} ${text.slice(0, 200)}`), { status: res.status });
  return json;
}

const slug = (name) => name.toLowerCase().trim().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 40);

function cookies(req) {
  return Object.fromEntries((req.headers.cookie || "").split(";").map((c) => c.trim().split("=")).filter(([k]) => k).map(([k, ...v]) => [k, decodeURIComponent(v.join("="))]));
}

function send(res, status, body, headers = {}) {
  const isJson = typeof body !== "string";
  res.writeHead(status, { "Content-Type": isJson ? "application/json" : "text/html; charset=utf-8", ...headers });
  res.end(isJson ? JSON.stringify(body) : body);
}

async function readBody(req) {
  let data = "";
  for await (const chunk of req) data += chunk;
  try { return data ? JSON.parse(data) : {}; } catch { return {}; }
}

const page = () => readFileSync(join(here, "index.html"), "utf8")
  .replaceAll("{{SITE_NAME}}", SITE_NAME)
  .replaceAll("{{AKTOR_URL}}", AKTOR_URL)
  .replaceAll("{{INSTALL_ID}}", INSTALL_ID);

createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`);
  const sid = cookies(req).sid;
  const user = sid ? sessions.get(sid) : undefined;
  try {
    if (req.method === "GET" && url.pathname === "/") return send(res, 200, page());

    // "Sign in": any name. Your real app would check a password, SSO, etc.
    if (req.method === "POST" && url.pathname === "/api/login") {
      const { name } = await readBody(req);
      if (!name?.trim() || !slug(name)) return send(res, 400, { error: "Enter a name." });
      const id = randomUUID();
      sessions.set(id, { userId: `user-${slug(name)}`, name: name.trim() });
      return send(res, 200, sessions.get(id), { "Set-Cookie": `sid=${id}; HttpOnly; SameSite=Lax; Path=/` });
    }
    if (req.method === "POST" && url.pathname === "/api/logout") {
      if (sid) sessions.delete(sid);
      return send(res, 200, {}, { "Set-Cookie": "sid=; Max-Age=0; Path=/" });
    }
    if (req.method === "GET" && url.pathname === "/api/me") return send(res, 200, { user: user ?? null });

    // What the widget calls: an end-user token for whoever is signed in here.
    if (req.method === "POST" && url.pathname === "/api/aktor-token") {
      if (!user) return send(res, 401, { error: "Sign in first." });
      const t = await aktor("POST", "/end-user-tokens", { user_id: user.userId, name: user.name, ttl_minutes: 60 });
      return send(res, 200, { token: t.token, expires_at: t.expires_at });
    }

    // Checks the integration: is Aktor reachable, is the key good, what does the app have?
    if (req.method === "GET" && url.pathname === "/api/check") {
      return send(res, 200, { aktor_url: AKTOR_URL, app: await aktor("GET", "") });
    }

    send(res, 404, { error: "Not found." });
  } catch (e) {
    console.error(e.message);
    send(res, e.status === 401 || e.status === 403 ? 502 : 500, { error: e.message });
  }
}).listen(PORT, () => {
  console.log(`${SITE_NAME} test site: http://localhost:${PORT}`);
  console.log(`Aktor: ${AKTOR_URL}  install: ${INSTALL_ID}`);
});
