"use client";

import { useEffect, useState } from "react";
import {
  API_BASE,
  addConnection,
  apiErrorMessage,
  listConnections,
  listPlugins,
  refreshConnection,
  removeConnection,
  updateConnection,
} from "@/lib/api";
import type { ConnectionView, NotifyLevel, PluginInfo, SideEffects } from "@/lib/workspaceTypes";

const LEVELS: NotifyLevel[] = ["Off", "Urgent", "Warning", "All"];
const LEVEL_HELP: Record<NotifyLevel, string> = {
  Off: "never",
  Urgent: "urgent only",
  Warning: "warnings + urgent",
  All: "everything",
};
const EFFECT_BADGE: Record<SideEffects, string> = {
  ReadOnly: "bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300",
  Idempotent: "bg-sky-100 text-sky-700 dark:bg-sky-950 dark:text-sky-300",
  NonIdempotent: "bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300",
};
const EFFECT_LABEL: Record<SideEffects, string> = { ReadOnly: "read", Idempotent: "safe write", NonIdempotent: "write" };

const field = "w-full rounded border border-neutral-300 bg-white px-2 py-1 dark:border-neutral-700 dark:bg-neutral-900";

export function IntegrationsPanel({ workspaceId, onChanged }: { workspaceId: string; onChanged: () => void }) {
  const [plugins, setPlugins] = useState<PluginInfo[]>([]);
  const [connections, setConnections] = useState<ConnectionView[]>([]);
  const [adding, setAdding] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function reload() {
    try {
      setConnections(await listConnections(workspaceId));
    } catch (err) {
      setError(apiErrorMessage(err));
    }
  }

  useEffect(() => {
    let cancelled = false;
    Promise.all([listPlugins(), listConnections(workspaceId)])
      .then(([p, c]) => {
        if (cancelled) return;
        setPlugins(p);
        setConnections(c);
      })
      .catch((err) => !cancelled && setError(apiErrorMessage(err)));
    return () => { cancelled = true; };
  }, [workspaceId]);

  const changed = async () => {
    await reload();
    onChanged();
  };

  return (
    <div className="space-y-3 p-3 text-xs">
      {error && <div className="rounded bg-rose-50 p-2 text-rose-700 dark:bg-rose-950 dark:text-rose-300">{error}</div>}
      {connections.length === 0 && !adding && (
        <div className="text-neutral-500">
          No connections yet. Connect an MCP server, any REST API (CRM, store, payments, ticketing…), or a messaging channel so agents
          can reach you by SMS, Slack, email or Telegram.
        </div>
      )}

      {connections.map((c) => (
        <ConnectionCard key={c.connection_id} workspaceId={workspaceId} connection={c}
          plugin={plugins.find((p) => p.id === c.plugin_id)} onChanged={changed} onError={setError} />
      ))}

      {adding ? (
        <AddConnectionForm workspaceId={workspaceId} plugins={plugins}
          onDone={async () => { setAdding(false); await changed(); }} onCancel={() => setAdding(false)} />
      ) : (
        <button onClick={() => { setAdding(true); setError(null); }} className="rounded bg-blue-600 px-3 py-1 font-medium text-white hover:bg-blue-700">
          + Add connection
        </button>
      )}
    </div>
  );
}

function ConnectionCard({ workspaceId, connection: c, plugin, onChanged, onError }: {
  workspaceId: string;
  connection: ConnectionView;
  plugin?: PluginInfo;
  onChanged: () => void;
  onError: (e: string) => void;
}) {
  const [senders, setSenders] = useState(c.allowed_senders.join(", "));
  const [busy, setBusy] = useState(false);

  async function run(action: () => Promise<unknown>) {
    setBusy(true);
    try {
      await action();
      onChanged();
    } catch (err) {
      onError(apiErrorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  const toggleTool = (name: string, on: boolean) => {
    const enabled = c.tools.filter((t) => (t.name === name ? on : t.enabled)).map((t) => t.name);
    run(() => updateConnection(workspaceId, c.connection_id, { enabled_tools: enabled }));
  };

  return (
    <div className="space-y-2 rounded border border-neutral-200 p-2 dark:border-neutral-800">
      <div className="flex items-center justify-between gap-2">
        <div>
          <span className="font-semibold">{c.name}</span>
          <span className="ml-1 text-neutral-500">· {plugin?.name ?? c.plugin_id}</span>
        </div>
        <div className="flex gap-2">
          {c.supports_tools && (
            <button disabled={busy} onClick={() => run(() => refreshConnection(workspaceId, c.connection_id))} className="text-[10px] text-blue-600 hover:underline">refresh</button>
          )}
          <button disabled={busy}
            onClick={() => confirm(`Remove '${c.name}'? Its stored secrets are deleted.`) && run(() => removeConnection(workspaceId, c.connection_id))}
            className="text-[10px] text-rose-600 hover:underline">remove</button>
        </div>
      </div>
      {c.last_error && <div className="text-[11px] text-amber-700 dark:text-amber-300">Last error: {c.last_error}</div>}
      <div className="text-[10px] text-neutral-400">
        {Object.entries(c.settings).map(([k, v]) => `${k}=${v}`).join(" · ")}
        {c.secret_keys.length > 0 && ` · secrets set: ${c.secret_keys.join(", ")}`}
      </div>

      {c.supports_notifications && (
        <label className="flex items-center gap-2">
          <span className="text-neutral-500">Forward notifications:</span>
          <select value={c.notify_level} disabled={busy}
            onChange={(e) => run(() => updateConnection(workspaceId, c.connection_id, { notify_level: e.target.value }))}
            className="rounded border border-neutral-300 bg-white px-1 py-0.5 dark:border-neutral-700 dark:bg-neutral-900">
            {LEVELS.map((l) => <option key={l} value={l}>{LEVEL_HELP[l]}</option>)}
          </select>
        </label>
      )}

      {c.tools.length > 0 && (
        <div>
          <div className="text-neutral-500">Tools ({c.tools.filter((t) => t.enabled).length}/{c.tools.length} enabled; each enabled tool costs tokens on every agent call)</div>
          <ul className="mt-1 max-h-48 space-y-0.5 overflow-y-auto">
            {c.tools.map((t) => (
              <li key={t.name} className="flex items-start gap-1.5" title={t.description}>
                <input type="checkbox" checked={t.enabled} disabled={busy} onChange={(e) => toggleTool(t.name, e.target.checked)} className="mt-0.5" />
                <span className="min-w-0 flex-1 truncate font-mono text-[10px]">{t.name}</span>
                <span className={`shrink-0 rounded px-1 text-[9px] ${EFFECT_BADGE[t.side_effects]}`}>{EFFECT_LABEL[t.side_effects]}</span>
              </li>
            ))}
          </ul>
        </div>
      )}

      {c.supports_inbound && (
        <div className="space-y-1">
          <div className="text-neutral-500">Commands from (allowed senders, comma-separated):</div>
          <div className="flex gap-1">
            <input value={senders} onChange={(e) => setSenders(e.target.value)} placeholder="+15557654321" className={field} />
            <button disabled={busy}
              onClick={() => run(() => updateConnection(workspaceId, c.connection_id, { allowed_senders: senders.split(",").map((s) => s.trim()).filter(Boolean) }))}
              className="shrink-0 rounded border border-neutral-300 px-2 hover:bg-neutral-100 dark:border-neutral-700 dark:hover:bg-neutral-800">save</button>
          </div>
          {c.inbound_path && (
            <div className="text-[10px] text-neutral-500">
              Inbound URL (keep secret; set it as the provider&apos;s webhook):
              <code className="mt-0.5 block break-all rounded bg-neutral-100 p-1 dark:bg-neutral-800">{API_BASE}{c.inbound_path}</code>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function AddConnectionForm({ workspaceId, plugins, onDone, onCancel }: {
  workspaceId: string;
  plugins: PluginInfo[];
  onDone: () => void;
  onCancel: () => void;
}) {
  const [pluginId, setPluginId] = useState(plugins[0]?.id ?? "");
  const plugin = plugins.find((p) => p.id === pluginId);
  const [name, setName] = useState(plugins[0]?.id ?? "");
  const [values, setValues] = useState<Record<string, string>>({});
  const [level, setLevel] = useState<NotifyLevel>("Warning");
  const [senders, setSenders] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function choose(id: string) {
    setPluginId(id);
    setName(id);
    setValues({});
    setError(null);
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!plugin) return;
    setBusy(true);
    setError(null);
    const settings: Record<string, string> = {};
    const secrets: Record<string, string> = {};
    for (const s of plugin.settings) {
      const v = values[s.key] ?? s.default_value ?? "";
      if (v) (s.secret ? secrets : settings)[s.key] = v;
    }
    try {
      await addConnection(workspaceId, {
        plugin_id: plugin.id,
        name,
        settings,
        secrets,
        notify_level: plugin.supports_notifications ? level : undefined,
        allowed_senders: senders.split(",").map((s) => s.trim()).filter(Boolean),
      });
      onDone();
    } catch (err) {
      setError(apiErrorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="space-y-2 rounded border border-dashed border-neutral-300 p-2 dark:border-neutral-700">
      <select value={pluginId} onChange={(e) => choose(e.target.value)} className={field}>
        {plugins.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
      </select>
      {plugin && (
        <>
          <div className="text-neutral-500">{plugin.description}</div>
          {plugin.setup_help && <div className="rounded bg-neutral-100 p-1.5 text-[11px] text-neutral-600 dark:bg-neutral-800 dark:text-neutral-300">{plugin.setup_help}</div>}
          <label className="block space-y-0.5">
            <span className="text-neutral-500">Connection name (tool prefix)</span>
            <input value={name} onChange={(e) => setName(e.target.value)} className={field} />
          </label>
          {plugin.settings.map((s) => (
            <label key={s.key} className="block space-y-0.5">
              <span className="text-neutral-500">{s.label}{s.required && " *"}{s.secret && " 🔒"}</span>
              {s.options ? (
                <select value={values[s.key] ?? s.default_value ?? ""} onChange={(e) => setValues({ ...values, [s.key]: e.target.value })} className={field}>
                  {s.options.map((o) => <option key={o} value={o}>{o}</option>)}
                </select>
              ) : s.label.toLowerCase().includes("one key=value per line") ? (
                <textarea rows={3} value={values[s.key] ?? ""} onChange={(e) => setValues({ ...values, [s.key]: e.target.value })} className={field} />
              ) : (
                <input type={s.secret ? "password" : "text"} autoComplete="off" value={values[s.key] ?? ""}
                  placeholder={s.placeholder ?? s.default_value ?? ""} onChange={(e) => setValues({ ...values, [s.key]: e.target.value })} className={field} />
              )}
            </label>
          ))}
          {plugin.supports_notifications && (
            <label className="block space-y-0.5">
              <span className="text-neutral-500">Forward notifications</span>
              <select value={level} onChange={(e) => setLevel(e.target.value as NotifyLevel)} className={field}>
                {LEVELS.map((l) => <option key={l} value={l}>{LEVEL_HELP[l]}</option>)}
              </select>
            </label>
          )}
          {plugin.supports_inbound && (
            <label className="block space-y-0.5">
              <span className="text-neutral-500">Accept commands from (your number / chat id, comma-separated)</span>
              <input value={senders} onChange={(e) => setSenders(e.target.value)} className={field} />
            </label>
          )}
          <div className="text-[10px] text-neutral-500">🔒 Secret fields are encrypted in the vault and never shown again, or to agents.</div>
        </>
      )}
      {error && <div className="text-rose-600">{error}</div>}
      <div className="flex gap-2">
        <button type="submit" disabled={busy || !plugin} className="rounded bg-blue-600 px-3 py-1 font-medium text-white hover:bg-blue-700 disabled:opacity-50">
          {busy ? "Connecting…" : "Connect"}
        </button>
        <button type="button" onClick={onCancel} className="rounded border border-neutral-300 px-3 py-1 dark:border-neutral-700">Cancel</button>
      </div>
    </form>
  );
}
