/**
 * Turning a solution built in the dashboard into a package: aktor.yaml as readable YAML, the
 * skills as SKILL.md files, and a .zip of the whole thing, all in the browser with no
 * dependencies. The server reads the result exactly as it reads a package made by hand, so
 * what the builder makes can be validated, deployed, downloaded and edited with the CLI later.
 */

export type Json = string | number | boolean | null | Json[] | { [key: string]: Json };

// ---------------------------------------------------------------- YAML

const PLAIN = /^[A-Za-z_][A-Za-z0-9_ ./()+-]*$/;
const RESERVED = /^(true|false|yes|no|on|off|null|~|y|n)$/i;
const KEY = /^[A-Za-z_][A-Za-z0-9_-]*$/;

function scalar(value: string): string {
  // Plain only when YAML can't read it as something else; otherwise JSON-style double quotes,
  // which are valid YAML with the same escapes.
  if (value !== "" && PLAIN.test(value) && !RESERVED.test(value) && !/^[0-9.+-]/.test(value) && !value.endsWith(" ") && !value.includes(": ") && !value.includes(" #")) {
    return value;
  }
  return JSON.stringify(value);
}

function key(k: string) {
  return KEY.test(k) && !RESERVED.test(k) ? k : JSON.stringify(k);
}

function isEmpty(v: Json | undefined): boolean {
  return v === null || v === undefined || (Array.isArray(v) && v.length === 0) || (typeof v === "object" && !Array.isArray(v) && Object.keys(v).length === 0);
}

/** A short, flat object of scalars fits on one line: `{ type: string, required: true }`. */
function inlineObject(o: { [key: string]: Json }): string | null {
  const entries = Object.entries(o);
  if (entries.length === 0 || entries.length > 6) return null;
  const parts: string[] = [];
  for (const [k, v] of entries) {
    if (v !== null && typeof v === "object") {
      if (!Array.isArray(v) || v.some((x) => x !== null && typeof x === "object")) return null;
      parts.push(`${key(k)}: [${v.map((x) => (typeof x === "string" ? scalar(x) : String(x))).join(", ")}]`);
    } else {
      if (typeof v === "string" && v.includes("\n")) return null;
      parts.push(`${key(k)}: ${typeof v === "string" ? scalar(v) : String(v)}`);
    }
  }
  const line = `{ ${parts.join(", ")} }`;
  return line.length <= 100 ? line : null;
}

function emit(value: Json, indent: number, out: string[], prefix: string) {
  const pad = " ".repeat(indent);
  if (value === null) {
    out.push(`${prefix}null`);
  } else if (typeof value === "string") {
    if (value.includes("\n")) {
      const chomp = value.endsWith("\n") ? (value.endsWith("\n\n") ? "+" : "") : "-";
      out.push(`${prefix}|${chomp}`);
      const body = value.endsWith("\n") ? value.slice(0, -1) : value;
      for (const line of body.split("\n")) out.push(line.length ? `${pad}  ${line}` : "");
    } else {
      out.push(`${prefix}${scalar(value)}`);
    }
  } else if (typeof value === "number" || typeof value === "boolean") {
    out.push(`${prefix}${String(value)}`);
  } else if (Array.isArray(value)) {
    if (value.length === 0) {
      out.push(`${prefix}[]`);
      return;
    }
    if (value.every((v) => v === null || typeof v !== "object") && value.length <= 12) {
      const line = `[${value.map((v) => (typeof v === "string" ? scalar(v) : String(v))).join(", ")}]`;
      if (prefix.length + line.length <= 110) {
        out.push(`${prefix}${line}`);
        return;
      }
    }
    if (prefix.trim()) out.push(prefix.trimEnd());
    for (const item of value) {
      if (item !== null && typeof item === "object" && !Array.isArray(item)) {
        const inline = inlineObject(item);
        if (inline) {
          out.push(`${pad}- ${inline}`);
          continue;
        }
        const entries = Object.entries(item).filter(([, v]) => v !== undefined);
        entries.forEach(([k, v], i) => emitEntry(k, v, indent + 2, out, i === 0 ? `${pad}- ` : `${pad}  `));
      } else {
        emit(item, indent + 2, out, `${pad}- `);
      }
    }
  } else {
    const entries = Object.entries(value).filter(([, v]) => v !== undefined);
    if (entries.length === 0) {
      out.push(`${prefix}{}`);
      return;
    }
    if (prefix.trim()) out.push(prefix.trimEnd());
    for (const [k, v] of entries) emitEntry(k, v, indent, out, pad);
  }
}

function emitEntry(k: string, v: Json, indent: number, out: string[], lead: string) {
  const head = `${lead}${key(k)}: `;
  if (v !== null && typeof v === "object") {
    if (!Array.isArray(v)) {
      const inline = inlineObject(v);
      if (inline && Object.keys(v).length <= 4) {
        out.push(`${head}${inline}`);
        return;
      }
    }
    if (isEmpty(v)) {
      out.push(`${head}${Array.isArray(v) ? "[]" : "{}"}`);
      return;
    }
    out.push(`${lead}${key(k)}:`);
    emit(v, indent + 2, out, " ".repeat(indent + 2));
    return;
  }
  emit(v, indent, out, head);
}

/** YAML for a JSON value, written for people: block style, short objects inline, long text as `|` blocks. */
export function toYaml(value: Json, header?: string): string {
  const out: string[] = [];
  if (header) out.push(...header.split("\n").map((l) => `# ${l}`.trimEnd()));
  emit(value, 0, out, "");
  return out.join("\n") + "\n";
}

// ---------------------------------------------------------------- cleaning a manifest

/** Values the server fills in by default, left out so the YAML says only what was chosen. */
const DEFAULTS: Record<string, Record<string, Json>> = {
  pipeline: { version: 0, updated_by: "", note: "", max_run_minutes: 30, max_concurrent_runs: 2, result_urgency: "info" },
  stage: { role: "", may_message_stages: true, retries: 1, on_failure: "FailRun", kind: "Agent", max_helpers: 0, instructions: "" },
  // A field's type is always written: it's what a reader looks for, and a field with nothing
  // else set would otherwise vanish.
  field: { required: false, indexed: false },
  input: { required: false, type: "string" },
  assistant: { max_tokens: 30000, max_cost_usd: 0.5, max_conversations_per_day: 20, max_messages_per_minute: 10 },
  connector: { required: true },
  eval: { timeout_seconds: 300 },
  trigger: { instruction: "", source_arguments_json: "{}" },
  safety: { autonomy: "Autonomous", approval_timeout_hours: 72 },
  jwt: { user_claim: "sub", name_claim: "name" },
};

function dropDefaults(o: { [key: string]: Json } | undefined, kind: keyof typeof DEFAULTS) {
  if (!o) return;
  for (const [k, v] of Object.entries(DEFAULTS[kind])) if (o[k] === v) delete o[k];
}

/** Removes nulls, empty strings, empty lists and objects, recursively (but keeps `0` and `false`). */
function prune(v: Json): Json | undefined {
  if (v === null || v === "") return undefined;
  if (Array.isArray(v)) {
    const items = v.map(prune).filter((x): x is Json => x !== undefined);
    return items.length ? items : undefined;
  }
  if (typeof v === "object") {
    const o: { [key: string]: Json } = {};
    for (const [k, x] of Object.entries(v)) {
      const p = prune(x);
      if (p !== undefined) o[k] = p;
    }
    return Object.keys(o).length ? o : undefined;
  }
  return v;
}

type Obj = { [key: string]: Json };
const obj = (v: Json | undefined): Obj | undefined => (v && typeof v === "object" && !Array.isArray(v) ? v : undefined);
const arr = (v: Json | undefined): Json[] => (Array.isArray(v) ? v : []);

/**
 * A manifest as the builder holds it (the server's full JSON, with every default) cleaned to what
 * a person would write: defaults and runtime-only fields removed, keys in a readable order.
 */
export function cleanManifest(manifest: Obj): Obj {
  const m = structuredClone(manifest) as Obj;
  if (m.format === 1) delete m.format;
  for (const e of Object.values(obj(m.entities) ?? {})) {
    for (const f of Object.values(obj(obj(e)?.fields) ?? {})) dropDefaults(obj(f), "field");
  }
  for (const c of arr(m.connectors)) dropDefaults(obj(c), "connector");
  for (const i of arr(m.inputs)) dropDefaults(obj(i), "input");
  for (const w of arr(m.workspaces)) {
    const ws = obj(w);
    const pipeline = obj(ws?.pipeline);
    if (pipeline) {
      delete pipeline.updated_at;
      delete pipeline.layout;
      dropDefaults(pipeline, "pipeline");
      for (const s of arr(pipeline.stages)) dropDefaults(obj(s), "stage");
    }
    for (const t of arr(ws?.triggers)) {
      const trigger = obj(t);
      dropDefaults(trigger, "trigger");
      // Arguments as an object read better than a JSON string; the server takes either.
      if (trigger && typeof trigger.source_arguments_json === "string") {
        try {
          trigger.source_arguments = JSON.parse(trigger.source_arguments_json);
          delete trigger.source_arguments_json;
        } catch { /* keep the string */ }
      }
    }
    for (const s of arr(pipeline?.stages)) {
      const stage = obj(s);
      if (stage && typeof stage.arguments === "string") {
        try { stage.arguments = JSON.parse(stage.arguments); } catch { /* keep the string */ }
      }
    }
    dropDefaults(obj(ws?.safety), "safety");
  }
  dropDefaults(obj(m.assistant), "assistant");
  dropDefaults(obj(obj(m.end_users)?.jwt), "jwt");
  for (const e of arr(m.evals)) {
    const ev = obj(e);
    dropDefaults(ev, "eval");
    const expect = obj(ev?.expect);
    if (expect?.status === "Completed") delete expect.status;
  }

  const order = ["format", "name", "title", "version", "requires_platform", "description", "inputs", "connectors", "entities", "workspaces",
    "skills", "knowledge", "assistant", "end_users", "seed", "webhooks", "evals"];
  const cleaned = (prune(m) ?? {}) as Obj;
  const sorted: Obj = { format: 1 };
  for (const k of order) if (k in cleaned && k !== "format") sorted[k] = cleaned[k];
  for (const [k, v] of Object.entries(cleaned)) if (!(k in sorted)) sorted[k] = v;
  return sorted;
}

// ---------------------------------------------------------------- skills as files

export type BuilderSkill = { name: string; description: string; instructions: string; workspace?: string | null };

export function skillMarkdown(s: BuilderSkill): string {
  const quote = (v: string) => (/[:#'"\n]/.test(v) || v.trim() !== v ? JSON.stringify(v) : v);
  return `---\nname: ${quote(s.name)}\ndescription: ${quote(s.description)}\n---\n\n${s.instructions.trim()}\n`;
}

/** Reads a SKILL.md's name, description and instructions (frontmatter plus body). */
export function parseSkillMarkdown(text: string): BuilderSkill {
  const m = text.match(/^---\s*\n([\s\S]*?)\n---\s*\n?([\s\S]*)$/);
  const front = m?.[1] ?? "";
  const field = (k: string) => {
    const line = front.match(new RegExp(`^${k}:\\s*(.*)$`, "m"))?.[1]?.trim() ?? "";
    if (line.startsWith('"')) { try { return JSON.parse(line) as string; } catch { /* fall through */ } }
    return line.replace(/^'(.*)'$/, "$1");
  };
  return { name: field("name"), description: field("description"), instructions: (m?.[2] ?? text).trim() };
}

// ---------------------------------------------------------------- zip

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

function crc32(bytes: Uint8Array) {
  let c = 0xffffffff;
  for (let i = 0; i < bytes.length; i++) c = CRC_TABLE[(c ^ bytes[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

/** A .zip (stored, not compressed: packages are small text) of path → text, inside a top folder. */
export function zipPackage(folder: string, files: Record<string, string>): Blob {
  const enc = new TextEncoder();
  const chunks: Uint8Array[] = [];
  const central: Uint8Array[] = [];
  let offset = 0;
  const u16 = (v: number) => [v & 0xff, (v >>> 8) & 0xff];
  const u32 = (v: number) => [v & 0xff, (v >>> 8) & 0xff, (v >>> 16) & 0xff, (v >>> 24) & 0xff];
  for (const [path, text] of Object.entries(files)) {
    const name = enc.encode(`${folder}/${path}`);
    const data = enc.encode(text);
    const crc = crc32(data);
    // UTF-8 names (flag bit 11), no compression, no timestamps that matter (1980-01-01).
    const common = [...u16(20), ...u16(0x0800), ...u16(0), ...u16(0), ...u16(0x21), ...u32(crc), ...u32(data.length), ...u32(data.length), ...u16(name.length), ...u16(0)];
    const local = new Uint8Array([...u32(0x04034b50), ...common]);
    chunks.push(local, name, data);
    central.push(new Uint8Array([...u32(0x02014b50), ...u16(20), ...common, ...u16(0), ...u16(0), ...u16(0), ...u32(0), ...u32(offset)]), name);
    offset += local.length + name.length + data.length;
  }
  const centralSize = central.reduce((n, c) => n + c.length, 0);
  const count = Object.keys(files).length;
  const end = new Uint8Array([...u32(0x06054b50), ...u16(0), ...u16(0), ...u16(count), ...u16(count), ...u32(centralSize), ...u32(offset), ...u16(0)]);
  return new Blob([...chunks, ...central, end].map((c) => c.slice().buffer as ArrayBuffer), { type: "application/zip" });
}

/** Offers a file to the person: a download, nothing sent anywhere. */
export function download(fileName: string, content: Blob | string, type = "text/yaml") {
  const blob = typeof content === "string" ? new Blob([content], { type }) : content;
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
