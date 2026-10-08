"use client";

import { useState } from "react";
import { API_BASE, apiErrorMessage, importOpenApi } from "@/lib/api";
import type { ApiEndpoint, ApiEndpointParam, ConnectionView } from "@/lib/workspaceTypes";

const field = "w-full rounded border border-zinc-300 bg-white px-2 py-1 dark:border-zinc-700 dark:bg-zinc-900";
const small = "rounded border border-zinc-300 bg-white px-1 py-0.5 dark:border-zinc-700 dark:bg-zinc-900";
const button = "rounded border border-zinc-300 px-2 py-0.5 hover:bg-zinc-100 dark:border-zinc-700 dark:hover:bg-zinc-800";
const METHODS: ApiEndpoint["method"][] = ["GET", "POST", "PUT", "PATCH", "DELETE"];
const METHOD_BADGE: Record<ApiEndpoint["method"], string> = {
  GET: "bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300",
  POST: "bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300",
  PUT: "bg-sky-100 text-sky-700 dark:bg-sky-950 dark:text-sky-300",
  PATCH: "bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300",
  DELETE: "bg-rose-100 text-rose-700 dark:bg-rose-950 dark:text-rose-300",
};

/** The endpoints stored in an HTTP API connection's "endpoints" setting (a JSON array). */
export function parseEndpoints(json: string | undefined): ApiEndpoint[] {
  if (!json) return [];
  try {
    const parsed = JSON.parse(json);
    return Array.isArray(parsed) ? (parsed as ApiEndpoint[]).map((e) => ({ ...e, params: e.params ?? [] })) : [];
  } catch {
    return [];
  }
}

export function serializeEndpoints(endpoints: ApiEndpoint[]): string {
  return endpoints.length === 0 ? "" : JSON.stringify(endpoints);
}

function placeholders(path: string): string[] {
  return [...path.matchAll(/\{([^{}/]+)\}/g)].map((m) => m[1].trim());
}

/** A tool name from free text: "Get order" → get_order. */
function toolName(text: string): string {
  return text.trim().replace(/([a-z0-9])([A-Z])/g, "$1_$2").toLowerCase().replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "").slice(0, 48);
}

/**
 * The operations of an HTTP API connection, each a typed tool for agents and the MCP gateway:
 * added by hand (method, path with {placeholders}, query parameters, a JSON body schema) or
 * imported from an OpenAPI document. The server checks them again when the connection is saved.
 */
export function EndpointsEditor({ value, onChange, writesAllowed, onBaseUrl }: {
  value: ApiEndpoint[];
  onChange: (next: ApiEndpoint[]) => void;
  writesAllowed: boolean;
  /** Called with the document's server URL after an import, to fill an empty base URL. */
  onBaseUrl?: (url: string) => void;
}) {
  const [editing, setEditing] = useState<number | "new" | null>(null);
  const [importing, setImporting] = useState(false);

  const save = (endpoint: ApiEndpoint) => {
    const next = [...value];
    if (editing === "new") next.push(endpoint);
    else if (typeof editing === "number") next[editing] = endpoint;
    onChange(next);
    setEditing(null);
  };

  const merge = (imported: ApiEndpoint[]) => {
    const byName = new Map(value.map((e) => [e.name, e]));
    for (const e of imported) byName.set(e.name, e);
    onChange([...byName.values()]);
    setImporting(false);
  };

  return (
    <div className="space-y-1.5">
      {value.length === 0 && editing === null && !importing && (
        <div className="text-[11px] text-zinc-500">
          No endpoints yet: agents get a general <code>get</code> tool (and <code>send</code> with writes allowed). Add your API&apos;s
          operations so each becomes its own tool with named parameters.
        </div>
      )}
      <ul className="space-y-1">
        {value.map((e, i) => (
          <li key={`${e.name}-${i}`} className="rounded border border-zinc-200 px-1.5 py-1 dark:border-zinc-800">
            <div className="flex items-center gap-1.5">
              <span className={`shrink-0 rounded px-1 font-mono text-[9px] ${METHOD_BADGE[e.method] ?? ""}`}>{e.method}</span>
              <span className="min-w-0 flex-1 truncate font-mono text-[10px]" title={e.description ?? undefined}>{e.path}</span>
              <span className="shrink-0 font-mono text-[10px] text-zinc-500">{e.name}</span>
              <button type="button" onClick={() => setEditing(i)} className="text-[10px] text-blue-600 hover:underline">edit</button>
              <button type="button" onClick={() => onChange(value.filter((_, j) => j !== i))} className="text-[10px] text-rose-600 hover:underline">remove</button>
            </div>
            {e.method !== "GET" && !writesAllowed && (
              <div className="mt-0.5 text-[10px] text-amber-700 dark:text-amber-300">Not offered until writes are allowed on this connection.</div>
            )}
          </li>
        ))}
      </ul>

      {editing !== null ? (
        <EndpointForm initial={typeof editing === "number" ? value[editing] : undefined}
          taken={value.filter((_, i) => i !== editing).map((e) => e.name)} onSave={save} onCancel={() => setEditing(null)} />
      ) : importing ? (
        <OpenApiImporter onImport={merge} onBaseUrl={onBaseUrl} onCancel={() => setImporting(false)} />
      ) : (
        <div className="flex gap-2">
          <button type="button" onClick={() => setEditing("new")} className={button}>+ Add endpoint</button>
          <button type="button" onClick={() => setImporting(true)} className={button}>Import from OpenAPI…</button>
        </div>
      )}
    </div>
  );
}

function EndpointForm({ initial, taken, onSave, onCancel }: {
  initial?: ApiEndpoint;
  taken: string[];
  onSave: (e: ApiEndpoint) => void;
  onCancel: () => void;
}) {
  const [name, setName] = useState(initial?.name ?? "");
  const [method, setMethod] = useState<ApiEndpoint["method"]>(initial?.method ?? "GET");
  const [path, setPath] = useState(initial?.path ?? "/");
  const [description, setDescription] = useState(initial?.description ?? "");
  const [query, setQuery] = useState<ApiEndpointParam[]>(initial?.params.filter((p) => p.in === "query") ?? []);
  const [pathInfo, setPathInfo] = useState<Record<string, ApiEndpointParam>>(
    Object.fromEntries((initial?.params ?? []).filter((p) => p.in === "path").map((p) => [p.name, p])));
  const [body, setBody] = useState(initial?.body_schema ? JSON.stringify(initial.body_schema, null, 2) : "");
  const [error, setError] = useState<string | null>(null);

  const pathParams = placeholders(path);
  const hasBody = method !== "GET";
  const setPathParam = (p: string, patch: Partial<ApiEndpointParam>) => setPathInfo({
    ...pathInfo,
    [p]: { name: p, in: "path", required: true, type: pathInfo[p]?.type ?? "string", description: pathInfo[p]?.description ?? null, ...patch },
  });

  function submit() {
    const finalName = name.trim() || toolName(`${method} ${path.replace(/[{}]/g, "")}`);
    if (!/^[a-z][a-z0-9_]{0,47}$/.test(finalName)) return setError("The name must start with a letter and use only lowercase letters, digits and underscores.");
    if (finalName === "get" || finalName === "send") return setError(`'${finalName}' is reserved for the general tools.`);
    if (taken.includes(finalName)) return setError(`Another endpoint is named '${finalName}'.`);
    if (!path.trim().startsWith("/") || path.includes("..") || path.includes("://")) return setError("The path must start with / and stay under the base URL.");
    let schema: unknown = undefined;
    if (hasBody && body.trim()) {
      try {
        schema = JSON.parse(body);
      } catch {
        return setError("The body schema isn't valid JSON.");
      }
      if (typeof schema !== "object" || schema === null || Array.isArray(schema)) return setError("The body schema must be a JSON object.");
    }
    const queryParams = query.filter((p) => p.name.trim());
    if (queryParams.some((p) => pathParams.includes(p.name.trim()))) return setError("A query parameter has the same name as a path placeholder.");
    onSave({
      name: finalName,
      method,
      path: path.trim(),
      description: description.trim() || null,
      params: [
        ...pathParams.map((p) => ({ name: p, in: "path" as const, type: pathInfo[p]?.type ?? "string", required: true, description: pathInfo[p]?.description ?? null })),
        ...queryParams.map((p) => ({ ...p, name: p.name.trim() })),
      ],
      ...(schema !== undefined ? { body_schema: schema } : {}),
    });
  }

  return (
    <div className="space-y-1.5 rounded border border-dashed border-zinc-300 p-2 dark:border-zinc-700">
      <div className="flex gap-1">
        <select value={method} onChange={(e) => setMethod(e.target.value as ApiEndpoint["method"])} className={small} aria-label="Method">
          {METHODS.map((m) => <option key={m}>{m}</option>)}
        </select>
        <input value={path} onChange={(e) => setPath(e.target.value)} placeholder="/orders/{order_id}" className={`${field} font-mono`} aria-label="Path" />
      </div>
      <label className="block space-y-0.5">
        <span className="text-zinc-500">Tool name</span>
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder={toolName(`${method} ${path.replace(/[{}]/g, "")}`) || "get_order"}
          className={`${field} font-mono`} />
      </label>
      <label className="block space-y-0.5">
        <span className="text-zinc-500">What it does (shown to agents)</span>
        <input value={description} onChange={(e) => setDescription(e.target.value)} placeholder="Returns one order with its lines and total" className={field} />
      </label>

      {pathParams.length > 0 && (
        <div className="space-y-0.5">
          <div className="text-zinc-500">Path parameters (from the {"{placeholders}"}, always required)</div>
          {pathParams.map((p) => (
            <div key={p} className="flex items-center gap-1">
              <span className="w-24 shrink-0 truncate font-mono text-[10px]">{p}</span>
              <TypeSelect value={pathInfo[p]?.type ?? "string"} onChange={(type) => setPathParam(p, { type })} />
              <input value={pathInfo[p]?.description ?? ""} placeholder="description"
                onChange={(e) => setPathParam(p, { description: e.target.value })} className={field} />
            </div>
          ))}
        </div>
      )}

      <div className="space-y-0.5">
        <div className="text-zinc-500">Query parameters</div>
        {query.map((p, i) => (
          <div key={i} className="flex items-center gap-1">
            <input value={p.name} placeholder="name" onChange={(e) => setQuery(query.map((q, j) => (j === i ? { ...q, name: e.target.value } : q)))}
              className={`${small} w-24 font-mono`} aria-label="Parameter name" />
            <TypeSelect value={p.type} onChange={(type) => setQuery(query.map((q, j) => (j === i ? { ...q, type } : q)))} />
            <label className="flex shrink-0 items-center gap-0.5 text-[10px] text-zinc-500">
              <input type="checkbox" checked={!!p.required} onChange={(e) => setQuery(query.map((q, j) => (j === i ? { ...q, required: e.target.checked } : q)))} />
              required
            </label>
            <input value={p.description ?? ""} placeholder="description"
              onChange={(e) => setQuery(query.map((q, j) => (j === i ? { ...q, description: e.target.value } : q)))} className={field} />
            <button type="button" onClick={() => setQuery(query.filter((_, j) => j !== i))} className="text-[10px] text-rose-600 hover:underline">×</button>
          </div>
        ))}
        <button type="button" onClick={() => setQuery([...query, { name: "", in: "query", type: "string" }])} className="text-[10px] text-blue-600 hover:underline">
          + query parameter
        </button>
      </div>

      {hasBody && (
        <label className="block space-y-0.5">
          <span className="text-zinc-500">Body JSON schema (optional; without one, any JSON object)</span>
          <textarea rows={4} value={body} onChange={(e) => setBody(e.target.value)} className={`${field} font-mono text-[10px]`}
            placeholder={'{ "type": "object", "properties": { "amount": { "type": "number" } }, "required": ["amount"] }'} />
        </label>
      )}

      {error && <div className="text-rose-600">{error}</div>}
      <div className="flex gap-2">
        <button type="button" onClick={submit} className="rounded bg-brand-500 px-3 py-0.5 font-medium text-white hover:bg-brand-600">
          {initial ? "Save endpoint" : "Add endpoint"}
        </button>
        <button type="button" onClick={onCancel} className={button}>Cancel</button>
      </div>
    </div>
  );
}

function TypeSelect({ value, onChange }: { value: ApiEndpointParam["type"]; onChange: (t: ApiEndpointParam["type"]) => void }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value as ApiEndpointParam["type"])} className={small} aria-label="Type">
      {(["string", "integer", "number", "boolean"] as const).map((t) => <option key={t}>{t}</option>)}
    </select>
  );
}

function OpenApiImporter({ onImport, onBaseUrl, onCancel }: {
  onImport: (endpoints: ApiEndpoint[]) => void;
  onBaseUrl?: (url: string) => void;
  onCancel: () => void;
}) {
  const [spec, setSpec] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<{ title: string | null; base_url: string | null; endpoints: ApiEndpoint[]; warnings: string[] } | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());

  async function read(text: string) {
    setBusy(true);
    setError(null);
    try {
      const r = await importOpenApi(text);
      setResult(r);
      setPicked(new Set(r.endpoints.map((e) => e.name)));
    } catch (err) {
      setError(apiErrorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  if (result) {
    const all = picked.size === result.endpoints.length;
    return (
      <div className="space-y-1.5 rounded border border-dashed border-zinc-300 p-2 dark:border-zinc-700">
        <div className="flex items-center justify-between">
          <span className="font-medium">{result.title ?? "OpenAPI document"} · {result.endpoints.length} operations</span>
          <button type="button" onClick={() => setPicked(all ? new Set() : new Set(result.endpoints.map((e) => e.name)))}
            className="text-[10px] text-blue-600 hover:underline">{all ? "select none" : "select all"}</button>
        </div>
        {result.base_url && <div className="text-[10px] text-zinc-500">Server: <code>{result.base_url}</code></div>}
        <ul className="max-h-56 space-y-0.5 overflow-y-auto">
          {result.endpoints.map((e) => (
            <li key={e.name}>
              <label className="flex items-center gap-1.5" title={e.description ?? undefined}>
                <input type="checkbox" checked={picked.has(e.name)}
                  onChange={(ev) => { const next = new Set(picked); if (ev.target.checked) next.add(e.name); else next.delete(e.name); setPicked(next); }} />
                <span className={`shrink-0 rounded px-1 font-mono text-[9px] ${METHOD_BADGE[e.method] ?? ""}`}>{e.method}</span>
                <span className="min-w-0 flex-1 truncate font-mono text-[10px]">{e.path}</span>
                <span className="shrink-0 font-mono text-[10px] text-zinc-500">{e.name}</span>
              </label>
            </li>
          ))}
        </ul>
        {result.warnings.length > 0 && (
          <details className="text-[10px] text-amber-700 dark:text-amber-300">
            <summary>{result.warnings.length} note{result.warnings.length === 1 ? "" : "s"}</summary>
            <ul className="list-disc pl-4">{result.warnings.map((w, i) => <li key={i}>{w}</li>)}</ul>
          </details>
        )}
        <div className="text-[10px] text-zinc-500">Only the selected operations are added; each enabled one costs tokens on every agent call, so pick what agents need.</div>
        <div className="flex gap-2">
          <button type="button" disabled={picked.size === 0}
            onClick={() => { if (result.base_url) onBaseUrl?.(result.base_url); onImport(result.endpoints.filter((e) => picked.has(e.name))); }}
            className="rounded bg-brand-500 px-3 py-0.5 font-medium text-white hover:bg-brand-600 disabled:opacity-50">
            Add {picked.size} endpoint{picked.size === 1 ? "" : "s"}
          </button>
          <button type="button" onClick={onCancel} className={button}>Cancel</button>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-1.5 rounded border border-dashed border-zinc-300 p-2 dark:border-zinc-700">
      <div className="text-zinc-500">An OpenAPI 3 or Swagger 2 document, JSON or YAML. It&apos;s only read, never fetched from or saved.</div>
      <label className="block cursor-pointer rounded border border-zinc-300 px-2 py-1 text-center hover:bg-zinc-100 dark:border-zinc-700 dark:hover:bg-zinc-800">
        Choose a file (.json, .yaml)
        <input type="file" accept=".json,.yaml,.yml,application/json,application/yaml,text/yaml" className="sr-only"
          onChange={async (e) => { const file = e.target.files?.[0]; e.target.value = ""; if (file) await read(await file.text()); }} />
      </label>
      <textarea rows={5} value={spec} onChange={(e) => setSpec(e.target.value)} placeholder="…or paste the document here"
        className={`${field} font-mono text-[10px]`} />
      {error && <div className="text-rose-600">{error}</div>}
      <div className="flex gap-2">
        <button type="button" disabled={busy || !spec.trim()} onClick={() => read(spec)}
          className="rounded bg-brand-500 px-3 py-0.5 font-medium text-white hover:bg-brand-600 disabled:opacity-50">
          {busy ? "Reading…" : "Read document"}
        </button>
        <button type="button" onClick={onCancel} className={button}>Cancel</button>
      </div>
    </div>
  );
}

/**
 * Serving a workspace connection as its own MCP server: which of its tools callers get, the URL,
 * and how to connect a client. Calls use the organization's API keys and the workspace's rules.
 */
export function GatewaySettings({ connection: c, busy, onSave }: {
  connection: ConnectionView;
  busy: boolean;
  onSave: (gateway: { enabled: boolean; tools: string[] }) => void;
}) {
  const prefix = `${c.name}__`;
  const local = c.tools.map((t) => ({ ...t, local: t.name.startsWith(prefix) ? t.name.slice(prefix.length) : t.name }));
  const enabled = c.gateway?.enabled ?? false;
  const served = new Set(c.gateway?.tools ?? []);
  const [copied, setCopied] = useState<string | null>(null);
  if (!c.gateway_path) return null;

  const url = `${API_BASE}${c.gateway_path}`;
  const snippet = `claude mcp add --transport http ${c.name} ${url} --header "Authorization: Bearer ak_…"`;
  const copy = (text: string, what: string) => {
    void navigator.clipboard?.writeText(text);
    setCopied(what);
    setTimeout(() => setCopied(null), 1500);
  };
  // Switching it on serves the read tools to start with; writes are a deliberate choice.
  const toggle = (on: boolean) => onSave({
    enabled: on,
    tools: on && served.size === 0 ? local.filter((t) => t.side_effects === "ReadOnly").map((t) => t.local) : [...served],
  });
  const toggleTool = (name: string, on: boolean) => {
    const next = new Set(served);
    if (on) next.add(name); else next.delete(name);
    onSave({ enabled, tools: [...next] });
  };

  return (
    <div className="space-y-1 rounded bg-zinc-50 p-1.5 dark:bg-zinc-900">
      <label className="flex items-center gap-1.5">
        <input type="checkbox" checked={enabled} disabled={busy} onChange={(e) => toggle(e.target.checked)} />
        <span className="font-medium">Serve as an MCP server</span>
      </label>
      {!enabled && (
        <div className="text-[10px] text-zinc-500">Let agents outside Aktor (Claude Code, n8n, CrewAI…) call these tools with an API key.</div>
      )}
      {enabled && (
        <>
          <div className="text-[10px] text-zinc-500">
            Tools callers get (separate from what this workspace&apos;s agents use). Calls need a Member API key, follow this workspace&apos;s
            safety policy (a write that needs approval is refused) and land in its audit log. The stored credential is never returned.
          </div>
          <ul className="max-h-40 space-y-0.5 overflow-y-auto">
            {local.map((t) => (
              <li key={t.name}>
                <label className="flex items-center gap-1.5" title={t.description}>
                  <input type="checkbox" checked={served.has(t.local)} disabled={busy} onChange={(e) => toggleTool(t.local, e.target.checked)} />
                  <span className="min-w-0 flex-1 truncate font-mono text-[10px]">{t.local}</span>
                  {t.side_effects !== "ReadOnly" && <span className="shrink-0 text-[9px] text-amber-700 dark:text-amber-300">writes</span>}
                </label>
              </li>
            ))}
          </ul>
          <div className="space-y-0.5 text-[10px]">
            <div className="flex items-center justify-between text-zinc-500">
              <span>URL (streamable HTTP, header <code>Authorization: Bearer ak_…</code>)</span>
              <button type="button" onClick={() => copy(url, "url")} className="text-blue-600 hover:underline">{copied === "url" ? "copied" : "copy"}</button>
            </div>
            <code className="block break-all rounded bg-zinc-100 p-1 dark:bg-zinc-800">{url}</code>
            <div className="flex items-center justify-between text-zinc-500">
              <span>Claude Code</span>
              <button type="button" onClick={() => copy(snippet, "snippet")} className="text-blue-600 hover:underline">{copied === "snippet" ? "copied" : "copy"}</button>
            </div>
            <code className="block break-all rounded bg-zinc-100 p-1 dark:bg-zinc-800">{snippet}</code>
            <div className="text-zinc-500">Create a key under Settings → API keys.</div>
          </div>
        </>
      )}
    </div>
  );
}
