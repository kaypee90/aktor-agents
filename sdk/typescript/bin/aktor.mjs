#!/usr/bin/env node
// aktor: build, deploy and test solutions on an Aktor server (docs/solutions.md).
//
//   aktor init my-app                 scaffold a solution package
//   aktor validate [dir]              check it against the server (nothing is installed)
//   aktor deploy [dir]                install it, or upgrade the installed version
//   aktor dev [dir]                   redeploy on every change (forces same-version upgrades)
//   aktor test [dir|name]             run its evals for real; exits 1 if any fail
//   aktor status [name]               installed solutions, or one in detail
//   aktor records <name> <Entity>     list records (--where k=v, --filter "ends_on<=today+30d")
//   aktor token <name> <user-id>      an end-user token (for the embed widget / apps API)
//   aktor uninstall <name> [--purge]  archive its workspaces (and delete its records with --purge)
//   aktor publish [dir] [--public]    publish this version to the registry
//   aktor search [query]              packages in the registry
//   aktor install <name[@version]>    install (or upgrade to) a registry package
//   aktor yank <name> <version>       stop new installs of a published version (--undo)
//   aktor usage <name> [--by end_user|workspace|day] [--csv]   what it cost, for billing
//
// Configure with AKTOR_URL (default http://localhost:5080) and AKTOR_API_KEY (an Admin key from
// Settings → API keys), or --url and --key. No dependencies; Node 18+.

import { readFileSync, writeFileSync, mkdirSync, existsSync, readdirSync, statSync, watch } from "node:fs";
import { join, relative, resolve, extname, basename, sep } from "node:path";

const MANIFESTS = ["aktor.yaml", "aktor.yml", "aktor.json"];
const TEXT = new Set([".yaml", ".yml", ".json", ".md", ".txt", ".csv", ".py", ".js", ".html", ".sql"]);
const SKIP = new Set(["node_modules", ".git", "bin", "obj", ".aktor"]);

// ---------------------------------------------------------------- arguments

function parseArgs(argv) {
  const positional = [];
  const flags = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith("--")) {
      const [k, v] = a.slice(2).split(/=(.*)/s, 2);
      const value = v !== undefined ? v : argv[i + 1] && !argv[i + 1].startsWith("--") ? argv[++i] : true;
      (flags[k] ??= []).push(value);
    } else positional.push(a);
  }
  const one = (k) => flags[k]?.at(-1);
  const many = (k) => (flags[k] ?? []).filter((v) => v !== true);
  return { positional, one, many, has: (k) => k in flags };
}

const args = parseArgs(process.argv.slice(2));
const command = args.positional.shift();
const baseUrl = String(args.one("url") ?? process.env.AKTOR_URL ?? "http://localhost:5080").replace(/\/$/, "");
const apiKey = args.one("key") ?? process.env.AKTOR_API_KEY;
const json = args.has("json");

const color = process.stdout.isTTY && !process.env.NO_COLOR;
const c = (code) => (s) => (color ? `\x1b[${code}m${s}\x1b[0m` : String(s));
const bold = c(1), dim = c(2), red = c(31), green = c(32), yellow = c(33), cyan = c(36);

function fail(message, code = 1) {
  process.stderr.write(red("✗ ") + message + "\n");
  process.exit(code);
}

// ---------------------------------------------------------------- HTTP

async function api(method, path, body) {
  if (!apiKey && !args.has("no-key")) fail("Set AKTOR_API_KEY (an Admin API key from Settings → API keys), or pass --key.", 2);
  let response;
  try {
    response = await fetch(baseUrl + path, {
      method,
      headers: { Accept: "application/json", ...(body !== undefined ? { "Content-Type": "application/json" } : {}), ...(apiKey ? { Authorization: `Bearer ${apiKey}` } : {}) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch (err) {
    fail(`Can't reach ${baseUrl} (${err.cause?.code ?? err.message}). Is the server running? Set AKTOR_URL.`);
  }
  const text = await response.text();
  let data;
  try { data = text ? JSON.parse(text) : undefined; } catch { data = text; }
  return { status: response.status, ok: response.ok, data };
}

function problems(data) {
  const list = data?.errors?.length ? data.errors : [data?.error ?? JSON.stringify(data)];
  return list.map((e) => "  " + red("•") + " " + e).join("\n");
}

// ---------------------------------------------------------------- packages

function packageDir(dir = ".") {
  const root = resolve(dir);
  const manifest = MANIFESTS.map((n) => join(root, n)).find((p) => existsSync(p));
  if (!manifest) fail(`No aktor.yaml in ${root}. Run 'aktor init' to make one.`);
  return { root, manifest };
}

function bundle(dir) {
  const { root, manifest } = packageDir(dir);
  const files = {};
  const walk = (d) => {
    for (const entry of readdirSync(d)) {
      if (entry.startsWith(".") || SKIP.has(entry)) continue;
      const full = join(d, entry);
      const st = statSync(full);
      if (st.isDirectory()) walk(full);
      else if (full !== manifest && TEXT.has(extname(entry).toLowerCase())) files[relative(root, full).split(sep).join("/")] = readFileSync(full, "utf8");
    }
  };
  walk(root);
  return { manifest: readFileSync(manifest, "utf8"), files };
}

/** name: and version: from the manifest, without a YAML parser (the server does the real parsing). */
function identity(manifestText) {
  const field = (k) => manifestText.match(new RegExp(`^${k}:\\s*["']?([^"'#\\n]+?)["']?\\s*(#.*)?$`, "m"))?.[1]?.trim();
  return { name: field("name"), version: field("version") };
}

function pairs(list, what) {
  const out = {};
  for (const item of list) {
    const at = String(item).indexOf("=");
    if (at <= 0) fail(`--${what} takes key=value (got '${item}').`);
    out[item.slice(0, at)] = item.slice(at + 1);
  }
  return out;
}

function secretsFromFlags() {
  const out = {};
  for (const [path, value] of Object.entries(pairs(args.many("secret"), "secret"))) {
    const dot = path.indexOf(".");
    if (dot <= 0) fail(`--secret takes connector.key=value (got '${path}').`);
    (out[path.slice(0, dot)] ??= {})[path.slice(dot + 1)] = value;
  }
  return out;
}

// ---------------------------------------------------------------- commands

async function validate(dir) {
  const b = bundle(dir);
  const { data, ok } = await api("POST", "/api/v1/solutions/validate", b);
  if (!ok) fail(problems(data));
  if (json) return console.log(JSON.stringify(data, null, 2));
  const s = data.summary;
  if (s) {
    console.log(`${bold(s.title)} ${dim(s.version)}  ${dim(`(${Object.keys(b.files).length} files)`)}`);
    for (const w of s.workspaces) console.log(`  ${cyan("workspace")} ${w.name} ${dim(`· ${w.stages} stage(s)`)}${w.triggers.length ? dim(" · " + w.triggers.join(", ")) : ""}`);
    for (const e of s.entities) console.log(`  ${cyan("entity")}    ${e.name} ${dim(`· ${e.fields} fields`)}`);
    for (const cn of s.connectors) console.log(`  ${cyan("connector")} ${cn.name} ${dim(`(${cn.plugin})`)}`);
    if (s.assistant) console.log(`  ${cyan("assistant")} ${s.assistant}`);
    if (s.evals) console.log(`  ${cyan("evals")}     ${s.evals}`);
  }
  for (const w of data.warnings ?? []) console.log(yellow("  ! ") + w);
  if (!data.ok) {
    console.log(red(`\n${data.errors.length} problem(s):`));
    console.log(data.errors.map((e) => "  " + red("•") + " " + e).join("\n"));
    process.exit(1);
  }
  const needed = Object.entries(data.secrets ?? {}).filter(([, list]) => list.length);
  if (needed.length) {
    console.log(dim("\nSecrets to pass on first deploy:"));
    for (const [name, list] of needed) for (const f of list) console.log(dim(`  --secret ${name}.${f.key}=…${f.required ? "" : " (optional)"}  ${f.label}`));
  }
  console.log(green("\n✓ valid"));
}

async function findInstall(name) {
  const { status, ok, data } = await api("GET", `/api/v1/solutions/${encodeURIComponent(name)}`);
  if (status === 404) return null;
  if (!ok) fail(problems(data));
  return data;
}

async function deploy(dir, { force = false, quiet = false } = {}) {
  const b = bundle(dir);
  const { name, version } = identity(b.manifest);
  if (!name) fail("aktor.yaml has no name.");
  const body = { bundle: b, inputs: pairs(args.many("input"), "input"), secrets: secretsFromFlags(), force: force || args.has("force"), dry_run: args.has("dry-run") };
  const existing = await findInstall(name);
  const { ok, data } = existing && existing.status === "Active"
    ? await api("PUT", `/api/v1/solutions/${encodeURIComponent(existing.install_id)}`, body)
    : await api("POST", "/api/v1/solutions", body);
  if (!ok) {
    if (quiet) { console.log(red("✗ deploy failed\n") + problems(data)); return null; }
    fail(`Deploy failed:\n${problems(data)}`);
  }
  if (json) { console.log(JSON.stringify(data, null, 2)); return data; }

  if (data.dry_run) {
    const p = data.plan;
    console.log(`${bold(name)} ${p.from} → ${p.to} ${dim("(dry run: nothing changed)")}`);
    for (const k of ["added_workspaces", "removed_workspaces", "changed_workspaces", "added_entities", "removed_entities", "new_connectors"]) {
      if (p[k]?.length) console.log(`  ${k.replace(/_/g, " ")}: ${p[k].join(", ")}`);
    }
    for (const w of p.warnings ?? []) console.log(yellow("  ! ") + w);
    return data;
  }

  if (existing && existing.status === "Active") {
    console.log(`${green("✓")} ${bold(name)} ${existing.version} → ${data.version} ${dim(data.install_id)}`);
    for (const w of data.workspaces ?? []) {
      const changes = w.changes?.length ? dim(" · " + w.changes.join(", ")) : "";
      console.log(`  ${w.action === "unchanged" ? dim("=") : cyan("~")} ${w.key} ${dim(w.action)}${changes}`);
      for (const p of w.problems ?? []) console.log(yellow("    ! ") + p);
    }
    for (const w of data.plan?.warnings ?? []) console.log(yellow("  ! ") + w);
  } else {
    console.log(`${green("✓")} installed ${bold(name)} ${data.version} ${dim(data.install_id)}`);
    for (const w of data.workspaces ?? []) {
      console.log(`  ${cyan("+")} ${w.name} ${dim(w.workspace_id)}`);
      for (const cn of w.connections ?? []) if (!cn.ok) console.log(yellow(`    ! connection ${cn.name}: `) + cn.message);
      for (const t of w.triggers ?? []) {
        if (!t.ok) console.log(yellow(`    ! trigger ${t.name}: `) + t.message);
        else if (t.webhook_path) console.log(dim(`    webhook ${t.name}: POST ${baseUrl}${t.webhook_path}  (keep secret)`));
      }
    }
    if (data.seeded_records) console.log(dim(`  ${data.seeded_records} seed record(s)`));
    if (data.webhook_secret) console.log(dim(`  webhook signing secret: ${data.webhook_secret}`));
    for (const w of data.warnings ?? []) console.log(yellow("  ! ") + w);
  }
  return data;
}

async function dev(dir) {
  const { root } = packageDir(dir);
  console.log(dim(`Watching ${root} — every change redeploys to ${baseUrl}. Ctrl-C to stop.\n`));
  await deploy(dir, { force: true, quiet: true });
  let timer = null;
  let running = false;
  let again = false;
  const run = async () => {
    if (running) { again = true; return; }
    running = true;
    console.log(dim(`\n— ${new Date().toLocaleTimeString()} change detected`));
    await deploy(dir, { force: true, quiet: true });
    running = false;
    if (again) { again = false; run(); }
  };
  try {
    watch(root, { recursive: true }, (_, file) => {
      if (!file || file.split(sep).some((p) => p.startsWith(".") || SKIP.has(p))) return;
      clearTimeout(timer);
      timer = setTimeout(run, 400);
    });
  } catch {
    // Older Node on Linux: no recursive watch. Poll instead.
    let last = JSON.stringify(bundle(dir));
    setInterval(() => { const now = JSON.stringify(bundle(dir)); if (now !== last) { last = now; run(); } }, 1500);
  }
}

async function test(target) {
  let name = target;
  if (!name || existsSync(resolve(name))) name = identity(readFileSync(packageDir(target ?? ".").manifest, "utf8")).name;
  const install = await findInstall(name);
  if (!install) fail(`'${name}' isn't installed; run 'aktor deploy' first.`);
  const only = args.many("eval");
  console.log(dim(`Running ${only.length ? only.join(", ") : "all"} eval(s) of ${name} ${install.version} for real — this takes as long as the runs do…`));
  const { ok, data } = await api("POST", `/api/v1/solutions/${encodeURIComponent(install.install_id)}/evals`, { names: only.length ? only : undefined });
  if (!ok) fail(problems(data));
  if (json) console.log(JSON.stringify(data, null, 2));
  else {
    for (const r of data.results) {
      const time = r.duration_ms != null ? dim(` ${(r.duration_ms / 1000).toFixed(1)}s`) : "";
      console.log(`${r.passed ? green("✓") : red("✗")} ${r.name}${time}${r.run_id ? dim(" " + r.run_id) : ""}`);
      for (const f of r.failures ?? []) console.log("    " + red(f));
    }
    console.log(`\n${data.failed ? red(`${data.failed} failed`) : green("all passed")}, ${data.passed} passed`);
  }
  process.exit(data.failed ? 1 : 0);
}

async function status(name) {
  if (!name) {
    const { ok, data } = await api("GET", "/api/v1/solutions");
    if (!ok) fail(problems(data));
    if (json) return console.log(JSON.stringify(data, null, 2));
    if (!data.length) return console.log(dim("No solutions installed. Deploy one with 'aktor deploy'."));
    for (const s of data) console.log(`${bold(s.name)} ${s.version} ${dim(s.install_id)} ${s.status === "Active" ? green(s.status) : yellow(s.status)}`);
    return;
  }
  const install = await findInstall(name);
  if (!install) fail(`No solution '${name}'.`);
  if (json) return console.log(JSON.stringify(install, null, 2));
  console.log(`${bold(install.title)} ${install.version} ${dim(install.install_id)} ${install.status}`);
  for (const w of install.workspaces) console.log(`  ${cyan("workspace")} ${w.name ?? w.key} ${dim(w.workspace_id)}`);
  for (const e of install.entities) console.log(`  ${cyan("entity")}    ${e.name} ${dim(`${e.records} record(s)`)}`);
  if (install.assistant) console.log(`  ${cyan("assistant")} ${install.assistant.name}`);
  for (const h of install.history ?? []) console.log(dim(`  ${h.at?.slice(0, 19).replace("T", " ")} ${h.action} ${h.version}${h.by ? " by " + h.by : ""}`));
}

async function records(name, entity) {
  if (!name || !entity) fail("Usage: aktor records <solution> <Entity> [--where field=value] [--filter 'field<=value'] [--limit n]");
  const install = await findInstall(name);
  if (!install) fail(`No solution '${name}'.`);
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(pairs(args.many("where"), "where"))) q.append(`where.${k}`, v);
  for (const f of args.many("filter")) q.append("filter", f);
  for (const k of ["limit", "offset", "order_by"]) if (args.one(k)) q.set(k, args.one(k));
  const { ok, data } = await api("GET", `/api/v1/solutions/${encodeURIComponent(install.install_id)}/records/${encodeURIComponent(entity)}?${q}`);
  if (!ok) fail(problems(data));
  if (json) return console.log(JSON.stringify(data, null, 2));
  console.log(dim(`${data.total} ${entity} record(s)`));
  for (const r of data.items) {
    const { id, version, created_at, updated_at, ...fields } = r;
    console.log(`${cyan(id)} ${dim("v" + version)} ${Object.entries(fields).map(([k, v]) => `${k}=${typeof v === "string" ? v : JSON.stringify(v)}`).join("  ")}`);
  }
}

async function token(name, user) {
  if (!name || !user) fail("Usage: aktor token <solution> <user-id> [--name 'Sam'] [--ttl minutes]");
  const install = await findInstall(name);
  if (!install) fail(`No solution '${name}'.`);
  const { ok, data } = await api("POST", `/api/v1/solutions/${encodeURIComponent(install.install_id)}/end-user-tokens`,
    { user_id: user, name: args.one("name"), ttl_minutes: args.one("ttl") ? Number(args.one("ttl")) : undefined });
  if (!ok) fail(problems(data));
  if (json) return console.log(JSON.stringify(data, null, 2));
  console.log(data.token);
  process.stderr.write(dim(`\nFor ${user} until ${data.expires_at}. Embed:\n  <script src="${baseUrl}/embed.js" data-install="${install.install_id}" data-token="${data.token}" async></script>\n`));
}

async function uninstall(name) {
  if (!name) fail("Usage: aktor uninstall <solution> [--purge]");
  const install = await findInstall(name);
  if (!install) fail(`No solution '${name}'.`);
  const { ok, data } = await api("DELETE", `/api/v1/solutions/${encodeURIComponent(install.install_id)}${args.has("purge") ? "?purge=true" : ""}`);
  if (!ok) fail(problems(data));
  console.log(`${green("✓")} uninstalled ${name}${args.has("purge") ? " and deleted its records" : dim(" (records kept; --purge deletes them)")}`);
}

async function publish(dir) {
  const b = bundle(dir);
  const { ok, data } = await api("POST", "/api/v1/registry/packages", { bundle: b, visibility: args.has("public") ? "public" : "organization" });
  if (!ok) fail(`Publish failed:\n${problems(data)}`);
  if (json) return console.log(JSON.stringify(data, null, 2));
  console.log(`${green("✓")} published ${bold(data.package)} ${dim(`(${data.visibility}, sha256 ${data.sha256.slice(0, 12)}…)`)}`);
  for (const w of data.warnings ?? []) console.log(yellow("  ! ") + w);
  console.log(dim(`Install it anywhere it's visible with: aktor install ${data.name}@${data.version}`));
}

async function search(query) {
  const { ok, data } = await api("GET", `/api/v1/registry/packages${query ? `?q=${encodeURIComponent(query)}` : ""}`);
  if (!ok) fail(problems(data));
  if (json) return console.log(JSON.stringify(data, null, 2));
  if (!data.length) return console.log(dim("No packages found."));
  for (const p of data) {
    console.log(`${bold(p.name)} ${p.version} ${dim(`· ${p.publisher} · ${p.visibility} · ${p.installs} install(s)`)}`);
    if (p.description) console.log(dim("  " + p.description.slice(0, 160)));
  }
}

async function installPackage(reference) {
  if (!reference) fail("Usage: aktor install <name[@version]> [--input k=v] [--secret conn.key=v]");
  const name = reference.split("@")[0];
  const body = { package: reference, inputs: pairs(args.many("input"), "input"), secrets: secretsFromFlags(), force: args.has("force"), dry_run: args.has("dry-run") };
  const existing = await findInstall(name);
  const { ok, data } = existing && existing.status === "Active"
    ? await api("PUT", `/api/v1/solutions/${encodeURIComponent(existing.install_id)}`, body)
    : await api("POST", "/api/v1/solutions", body);
  if (!ok) fail(`Install failed:\n${problems(data)}`);
  if (json) return console.log(JSON.stringify(data, null, 2));
  console.log(`${green("✓")} ${existing?.status === "Active" ? `upgraded ${name} ${existing.version} →` : "installed"} ${bold(data.package ?? name)} ${dim(data.install_id)}`);
  for (const w of data.workspaces ?? []) console.log(`  ${cyan(existing ? "~" : "+")} ${w.name ?? w.key} ${dim(w.workspace_id)}`);
}

async function yank(name, version) {
  if (!name || !version) fail("Usage: aktor yank <name> <version> [--reason '…'] [--undo]");
  const { ok, data } = await api("POST", `/api/v1/registry/packages/${encodeURIComponent(name)}/${encodeURIComponent(version)}/yank`,
    { yank: !args.has("undo"), reason: args.one("reason") });
  if (!ok) fail(problems(data));
  console.log(`${green("✓")} ${args.has("undo") ? "un-yanked" : "yanked"} ${name}@${version}`);
}

async function usage(name) {
  if (!name) fail("Usage: aktor usage <solution> [--by end_user|workspace|day] [--from 2026-10-01] [--to 2026-11-01] [--csv]");
  const q = new URLSearchParams({ by: args.one("by") ?? "end_user" });
  if (args.one("from")) q.set("from", args.one("from"));
  if (args.one("to")) q.set("to", args.one("to"));
  if (args.has("csv")) {
    q.set("format", "csv");
    const response = await fetch(`${baseUrl}/api/v1/solutions/${encodeURIComponent(name)}/usage?${q}`, { headers: { Authorization: `Bearer ${apiKey}` } });
    if (!response.ok) fail(`HTTP ${response.status}: ${await response.text()}`);
    return process.stdout.write(await response.text());
  }
  const { ok, data } = await api("GET", `/api/v1/solutions/${encodeURIComponent(name)}/usage?${q}`);
  if (!ok) fail(problems(data));
  if (json) return console.log(JSON.stringify(data, null, 2));
  console.log(`${bold(name)} ${dim(`${data.from.slice(0, 10)} → ${data.to.slice(0, 10)}`)}  $${data.cost_usd.toFixed(4)} · ${data.tokens} tokens · ${data.conversations} conversation(s) · ${data.runs} run(s)`);
  for (const l of data.lines) console.log(`  ${l.key.padEnd(28)} $${l.cost_usd.toFixed(4).padStart(10)}  ${dim(`${l.tokens} tokens · ${l.conversations} conv · ${l.runs} runs`)}`);
}

function init(dir) {
  if (!dir) fail("Usage: aktor init <directory> [--name my-app]");
  const root = resolve(dir);
  if (existsSync(join(root, "aktor.yaml"))) fail(`${root} already has an aktor.yaml.`);
  const name = String(args.one("name") ?? basename(root)).toLowerCase().replace(/[^a-z0-9-]+/g, "-").replace(/^-+|-+$/g, "") || "my-app";
  const title = name.split("-").map((w) => w[0]?.toUpperCase() + w.slice(1)).join(" ");
  const files = {
    "aktor.yaml": `# ${title}: a solution built on Aktor (docs/solutions.md).
# aktor validate   → check it      aktor deploy → install or upgrade it
# aktor dev        → redeploy on every change   aktor test → run the evals
format: 1
name: ${name}
title: ${title}
version: 0.1.0
description: What this solution does, in a sentence or two.

inputs:
  - key: company_name
    label: Company name
    default: Example Co

# The data your agents work on. Every write is checked against these fields.
entities:
  Request:
    description: Something a customer asked for
    title_field: subject
    fields:
      subject: { type: string, required: true }
      details: { type: text }
      requester: { type: end_user }        # set by the runtime: whose request it is
      priority: { type: enum, values: [low, normal, urgent], default: normal }
      status: { type: enum, values: [new, handled], default: new }
      notes: { type: text }

workspaces:
  - file: workspaces/requests.yaml

skills:
  - path: skills/handling-requests/SKILL.md

knowledge:
  - key: Service levels
    path: knowledge/service-levels.md

# What your own customers talk to, through the embed widget or the apps API.
assistant:
  name: Assistant
  greeting: Hi! How can I help?
  instructions_file: assistant.md
  data: { Request: write }

evals:
  - file: evals/handles-a-request.yaml
`,
    "workspaces/requests.yaml": `key: requests
name: Requests
goal: Handle every new request by setting its priority, noting what to do and marking it handled.
data: { Request: write }
daily_cost_usd: 2
pipeline:
  stages:
    - stage_id: handle
      name: Handle
      role: Request handler
      instructions: >
        A request was created; its record is the run's input. Decide its priority using the
        handling-requests skill and the Service levels knowledge, then call data__update_request
        with priority, notes and status "handled". Summarise what you decided.
triggers:
  - kind: Record
    name: New request
    entity: Request
    on: [created]
    instruction: Handle this request.
`,
    "skills/handling-requests/SKILL.md": `---
name: handling-requests
description: How to prioritise a customer request and what to write in its notes.
---

- **urgent**: the customer can't use the service at all, or money or safety is at risk.
- **normal**: something doesn't work as it should, but there's a way around it.
- **low**: a question, a suggestion, or something cosmetic.

Notes say in one or two sentences what should happen next and who should do it.
`,
    "knowledge/service-levels.md": `# Service levels

- Urgent requests are answered within 1 hour.
- Normal requests are answered within 1 working day.
- Low-priority requests are answered within 5 working days.
`,
    "assistant.md": `You help customers of {{ inputs.company_name }}.

- When they ask for something, log it with data__create_request (subject in a few words, details
  in their words) and tell them it's logged and when to expect an answer.
- When they ask about a request they made, find it with data__list_request and tell them its status.
- Be brief and friendly. If you can't help, say so and suggest they contact support.
`,
    "evals/handles-a-request.yaml": `name: a request gets handled
workspace: requests
input: "Request created: the customer can't sign in since this morning."
input_data:
  entity: Request
  event: created
  record: { subject: "Can't sign in", details: "Since this morning I get an error when I sign in." }
timeout_seconds: 300
expect:
  status: Completed
`,
    ".gitignore": `.aktor/\n`,
    "README.md": `# ${title}

A solution for [Aktor](https://github.com/). Deploy it to a server with an Admin API key:

\`\`\`bash
export AKTOR_URL=http://localhost:5080 AKTOR_API_KEY=ak_...
aktor validate      # check the package
aktor deploy        # install or upgrade
aktor dev           # redeploy on every change while you work
aktor test          # run the evals for real
aktor records ${name} Request
aktor token ${name} customer-42   # a token for the embed widget
\`\`\`
`,
  };
  for (const [path, content] of Object.entries(files)) {
    const full = join(root, path);
    mkdirSync(join(full, ".."), { recursive: true });
    writeFileSync(full, content);
  }
  console.log(`${green("✓")} created ${bold(name)} in ${root}`);
  for (const path of Object.keys(files)) console.log(dim("  " + path));
  console.log(`\nNext: ${cyan(`cd ${relative(process.cwd(), root) || "."} && aktor validate`)}`);
}

function help() {
  console.log(`${bold("aktor")} — build, deploy and test solutions on Aktor

  aktor init <dir> [--name n]          scaffold a solution package
  aktor validate [dir]                 check it against the server
  aktor deploy [dir]                   install, or upgrade the installed version
        --input key=value              an input value (repeatable)
        --secret connector.key=value   a connector secret (repeatable; first deploy)
        --dry-run                      show what an upgrade would change
        --force                        redeploy the same (or an older) version
  aktor dev [dir]                      redeploy on every change
  aktor test [dir|name] [--eval n]     run the evals for real (exit 1 on failure)
  aktor status [name]                  installed solutions, or one in detail
  aktor records <name> <Entity>        --where k=v  --filter 'ends_on<=today+30d'  --limit n
  aktor token <name> <user-id>         an end-user token (--name, --ttl minutes)
  aktor uninstall <name> [--purge]     archive its workspaces (--purge deletes its records)
  aktor publish [dir] [--public]       publish this version to the registry
  aktor search [query]                 packages in the registry
  aktor install <name[@version]>       install, or upgrade to, a registry package
  aktor yank <name> <version>          stop new installs of a version (--undo, --reason)
  aktor usage <name>                   what it cost: --by end_user|workspace|day, --from, --to, --csv

  --url, --key   or AKTOR_URL / AKTOR_API_KEY     --json  machine-readable output
`);
}

const commands = {
  init: () => init(args.positional[0]),
  validate: () => validate(args.positional[0]),
  deploy: () => deploy(args.positional[0]),
  dev: () => dev(args.positional[0]),
  test: () => test(args.positional[0]),
  status: () => status(args.positional[0]),
  records: () => records(args.positional[0], args.positional[1]),
  token: () => token(args.positional[0], args.positional[1]),
  uninstall: () => uninstall(args.positional[0]),
  publish: () => publish(args.positional[0]),
  search: () => search(args.positional[0]),
  install: () => installPackage(args.positional[0]),
  yank: () => yank(args.positional[0], args.positional[1]),
  usage: () => usage(args.positional[0]),
  help,
};

if (!command || command === "--help" || command === "-h") help();
else if (!commands[command]) fail(`Unknown command '${command}'. Run 'aktor help'.`, 2);
else await commands[command]();
