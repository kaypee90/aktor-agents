"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useAuth } from "@/components/platform/AuthProvider";
import {
  apiErrorMessage,
  createGlobalConnector,
  deleteGlobalConnector,
  listGlobalConnectors,
  listPlugins,
  testGlobalConnector,
  updateGlobalConnector,
  type GlobalConnector,
} from "@/lib/api";
import { atLeast, type Role } from "@/lib/platformTypes";
import type { PluginInfo } from "@/lib/workspaceTypes";
import { ConnectorIcon } from "@/components/workspace/ConnectorIcon";
import { EndpointsEditor, parseEndpoints, serializeEndpoints } from "@/components/workspace/ApiEndpoints";
import { Badge, Button, Card, EmptyState, ErrorBanner, Field, Modal, PageHeader, Toggle, ago, cx, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

/**
 * Global connectors (docs/plugins.md#global-connectors): integrations set up once for the whole
 * organization, then linked from workspaces, tasks and solutions instead of entering the same
 * keys again in each. Admins manage them; everyone sees what exists and where it's used.
 */
export default function ConnectorsPage() {
  const { me } = useAuth();
  const isAdmin = atLeast((me?.role ?? "Viewer") as Role, "Admin");
  const [connectors, setConnectors] = useState<GlobalConnector[] | null>(null);
  const [plugins, setPlugins] = useState<PluginInfo[]>([]);
  const [editing, setEditing] = useState<GlobalConnector | "new" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  async function reload() {
    try {
      setConnectors(await listGlobalConnectors());
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  useEffect(() => {
    listGlobalConnectors().then(setConnectors).catch((e) => setError(apiErrorMessage(e)));
    // The records connection belongs to one solution install; it can't be shared.
    listPlugins().then((p) => setPlugins(p.filter((x) => x.id !== "aktor-records"))).catch(() => {});
  }, []);

  return (
    <div>
      <PageHeader
        title="Connectors"
        description="Set up an integration once for your whole organization: its keys go into the vault one time, and every workspace, workflow, solution and task you allow can link it. Rotate a key here and every link uses the new one."
        actions={isAdmin
          ? <Button variant="primary" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => setEditing("new")}>New connector</Button>
          : <Badge>Admins set up connectors</Badge>}
      />
      <div className="mx-auto max-w-5xl space-y-4 px-6 py-6">
        <ErrorBanner error={error} onClose={() => setError(null)} />
        {notice && (
          <div className="flex items-start justify-between gap-3 rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-800 dark:border-emerald-900/60 dark:bg-emerald-950/40 dark:text-emerald-300">
            <span>{notice}</span>
            <button onClick={() => setNotice(null)} className="opacity-70 hover:opacity-100" aria-label="Dismiss"><Icons.X /></button>
          </div>
        )}

        {connectors === null ? <div className="text-sm text-zinc-500">Loading…</div>
          : connectors.length === 0 ? (
            <Card>
              <EmptyState icon={<Icons.Plug className="h-6 w-6" />} title="No global connectors yet"
                description="Connect Slack, Twilio, an MCP server or any API once, then pick it wherever you'd otherwise paste the same keys again."
                action={isAdmin ? <Button variant="primary" onClick={() => setEditing("new")}>New connector</Button> : undefined} />
            </Card>
          ) : connectors.map((c) => (
            <ConnectorCard key={c.connector_id} connector={c} plugin={plugins.find((p) => p.id === c.plugin_id)} isAdmin={isAdmin}
              onEdit={() => setEditing(c)} onError={setError} onNotice={setNotice} onChanged={reload} />
          ))}
      </div>

      {editing && (
        <ConnectorDialog connector={editing === "new" ? null : editing} plugins={plugins}
          onClose={() => setEditing(null)}
          onSaved={async (message) => { setEditing(null); setNotice(message); await reload(); }} />
      )}
    </div>
  );
}

function ConnectorCard({ connector: c, plugin, isAdmin, onEdit, onError, onNotice, onChanged }: {
  connector: GlobalConnector; plugin?: PluginInfo; isAdmin: boolean;
  onEdit: () => void; onError: (e: string) => void; onNotice: (m: string) => void; onChanged: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [test, setTest] = useState<{ ok: boolean; message: string } | null>(null);

  async function runTest() {
    setBusy(true);
    try {
      setTest(await testGlobalConnector(c.connector_id));
    } catch (e) {
      onError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    const uses = c.used_by.length;
    const question = uses === 0
      ? `Delete '${c.name}' and its secrets?`
      : `'${c.name}' is used by ${uses} connection${uses === 1 ? "" : "s"}. Delete it anyway? Those connections are removed too.`;
    if (!window.confirm(question)) return;
    setBusy(true);
    try {
      const result = await deleteGlobalConnector(c.connector_id, uses > 0);
      onNotice(result.removed_connections > 0 ? `Deleted '${c.name}' and ${result.removed_connections} connection(s) that used it.` : `Deleted '${c.name}'.`);
      onChanged();
    } catch (e) {
      onError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const settings = Object.entries(c.settings).filter(([k]) => k !== "endpoints");
  return (
    <Card>
      <div className="flex flex-wrap items-start gap-4 p-5">
        <ConnectorIcon pluginId={c.plugin_id} name={c.plugin_name} className="mt-0.5 h-8 w-8" />
        <div className="min-w-0 flex-1 space-y-1.5">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-mono text-sm font-semibold text-zinc-900 dark:text-zinc-50">{c.name}</span>
            <Badge tone="brand">Global</Badge>
            <span className="text-sm text-zinc-500">{c.plugin_name}</span>
            {c.usable_in_tasks && <Badge tone="blue">Usable in tasks</Badge>}
          </div>
          {c.description && <p className="text-sm text-zinc-600 dark:text-zinc-400">{c.description}</p>}
          <div className="text-xs text-zinc-500">
            <span className="font-mono">{c.connector_id}</span>
            {settings.length > 0 && <> · {settings.map(([k, v]) => `${k}=${v}`).join(" · ")}</>}
            {c.secret_keys.length > 0 && <> · 🔒 {c.secret_keys.join(", ")}</>}
            {c.plugin_id === "http-api" && <> · {parseEndpoints(c.settings.endpoints).length} endpoints</>}
            {" · "}updated {ago(c.updated_at)}
          </div>
          <div className="pt-1 text-xs">
            {c.used_by.length === 0 ? <span className="text-zinc-400">Not used anywhere yet. Link it from a workspace&apos;s or task&apos;s connections, or a solution&apos;s deploy.</span> : (
              <div className="flex flex-wrap items-center gap-1.5">
                <span className="text-zinc-500">Used by</span>
                {c.used_by.map((u) => (
                  <Link key={`${u.owner_id}/${u.connection_id}`}
                    href={u.owner_kind === "workspace" ? `/workspaces?id=${encodeURIComponent(u.owner_id)}` : `/?task=${encodeURIComponent(u.owner_id)}`}
                    className="rounded-md border border-zinc-200 px-1.5 py-0.5 text-zinc-700 hover:border-brand-300 hover:text-brand-700 dark:border-zinc-700 dark:text-zinc-300"
                    title={`Its connection there is named '${u.connection_name}'`}>
                    {u.owner_kind === "workspace" ? "Workspace" : "Task"}: {u.owner_name || u.owner_id}
                  </Link>
                ))}
              </div>
            )}
          </div>
          {test && (
            <div className={cx("text-xs", test.ok ? "text-emerald-600 dark:text-emerald-400" : "text-rose-600 dark:text-rose-400")}>
              {test.ok ? "✓ " : "✗ "}{test.message}
            </div>
          )}
        </div>
        {isAdmin && (
          <div className="flex shrink-0 gap-2">
            <Button size="sm" disabled={busy} onClick={runTest}>{busy ? "…" : "Test"}</Button>
            <Button size="sm" disabled={busy} onClick={onEdit}>Edit</Button>
            <Button size="sm" disabled={busy} onClick={remove} title={plugin ? undefined : "Its plugin isn't installed"}>
              <Icons.Trash className="h-3.5 w-3.5" />
            </Button>
          </div>
        )}
      </div>
    </Card>
  );
}

/** Creates a connector, or edits one: settings, rotating secrets (empty keeps them), who may use it. */
function ConnectorDialog({ connector, plugins, onClose, onSaved }: {
  connector: GlobalConnector | null; plugins: PluginInfo[]; onClose: () => void; onSaved: (message: string) => void;
}) {
  const editing = connector !== null;
  const [pluginId, setPluginId] = useState(connector?.plugin_id ?? "");
  const plugin = plugins.find((p) => p.id === pluginId);
  const [name, setName] = useState(connector?.name ?? "");
  const [description, setDescription] = useState(connector?.description ?? "");
  const [values, setValues] = useState<Record<string, string>>(connector?.settings ?? {});
  const [usableInTasks, setUsableInTasks] = useState(connector?.usable_in_tasks ?? false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function choose(id: string) {
    setPluginId(id);
    if (!name || plugins.some((p) => p.id === name)) setName(id.replace(/-/g, "_"));
    setValues({});
    setError(null);
  }

  async function save() {
    if (!plugin) return;
    setBusy(true);
    setError(null);
    const settings: Record<string, string> = {};
    const secrets: Record<string, string> = {};
    for (const s of plugin.settings) {
      const v = values[s.key] ?? (editing ? "" : s.default_value ?? "");
      if (s.secret) { if (v) secrets[s.key] = v; }
      else settings[s.key] = v;
    }
    try {
      if (editing) {
        const result = await updateGlobalConnector(connector.connector_id, { description, settings, secrets, usable_in_tasks: usableInTasks });
        const refreshed = result.refreshed_connections > 0 ? ` ${result.refreshed_connections} linked connection(s) refreshed.` : "";
        const problems = result.problems.length > 0 ? ` Some couldn't be refreshed: ${result.problems.join("; ")}` : "";
        onSaved(`Saved '${connector.name}'.${refreshed}${problems}`);
      } else {
        const created = await createGlobalConnector({ name, plugin_id: plugin.id, description, settings, secrets, usable_in_tasks: usableInTasks });
        onSaved(`Connected '${created.name}'. Pick it from any workspace's connections, a task, or a solution's deploy.`);
      }
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open wide onClose={onClose}
      title={editing ? `Edit ${connector.name}` : "New global connector"}
      description={editing
        ? "Changes are checked against the service before they're saved, then every connection that links it picks them up."
        : "It's checked against the service before it's saved. Secrets go straight to the encrypted vault and are never shown again, or to agents."}
      footer={<>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="primary" disabled={busy || !plugin || (!editing && !name.trim())} onClick={save}>
          {busy ? "Checking…" : editing ? "Save" : "Connect"}
        </Button>
      </>}>
      <div className="space-y-4 text-sm">
        <ErrorBanner error={error} onClose={() => setError(null)} />
        {!editing && (
          <div className="grid grid-cols-2 gap-1.5 sm:grid-cols-3" role="radiogroup" aria-label="Service">
            {plugins.map((p) => (
              <button key={p.id} type="button" role="radio" aria-checked={p.id === pluginId} onClick={() => choose(p.id)} title={p.description}
                className={cx("flex min-w-0 items-center gap-2 rounded-lg border px-2 py-1.5 text-left",
                  p.id === pluginId ? "border-brand-500 bg-brand-50 dark:bg-brand-500/10" : "border-zinc-200 hover:border-zinc-300 dark:border-zinc-800 dark:hover:border-zinc-700")}>
                <ConnectorIcon pluginId={p.id} name={p.name} className="h-4 w-4" />
                <span className="truncate">{p.name}</span>
              </button>
            ))}
          </div>
        )}
        {plugin && (
          <>
            {!editing && <p className="text-xs text-zinc-500">{plugin.description}</p>}
            {plugin.setup_help && <div className="rounded-lg bg-zinc-100 p-2 text-xs text-zinc-600 dark:bg-zinc-800 dark:text-zinc-300">{plugin.setup_help}</div>}
            <div className="grid gap-3 md:grid-cols-2">
              <Field label="Name" hint={editing ? "Names don't change: links and solutions refer to it." : "Lowercase; also the default tool prefix where it's linked."}>
                <input className={cx(inputClass, "font-mono")} value={name} disabled={editing} onChange={(e) => setName(e.target.value)} placeholder="team_slack" />
              </Field>
              <Field label="Description (optional)" hint="Helps people pick the right one.">
                <input className={inputClass} value={description} onChange={(e) => setDescription(e.target.value)} placeholder="Ops alerts workspace" />
              </Field>
              {plugin.settings.filter((s) => !(plugin.id === "http-api" && s.key === "endpoints")).map((s) => (
                <Field key={s.key} label={`${s.label}${s.required && !(editing && s.secret) ? " *" : ""}${s.secret ? " 🔒" : ""}`}
                  hint={s.secret && editing ? (connector.secret_keys.includes(s.key) ? "Set. Leave empty to keep it; type a new value to rotate it." : "Not set.") : s.description ?? undefined}>
                  {s.options ? (
                    <select className={inputClass} value={values[s.key] ?? s.default_value ?? ""} onChange={(e) => setValues({ ...values, [s.key]: e.target.value })}>
                      {s.options.map((o) => <option key={o} value={o}>{o}</option>)}
                    </select>
                  ) : (
                    <input className={inputClass} type={s.secret ? "password" : "text"} autoComplete="off" value={values[s.key] ?? ""}
                      placeholder={s.secret && editing ? "••••••••" : s.placeholder ?? s.default_value ?? ""}
                      onChange={(e) => setValues({ ...values, [s.key]: e.target.value })} />
                  )}
                </Field>
              ))}
            </div>
            {plugin.id === "http-api" && (
              <Field label="Endpoints">
                <EndpointsEditor value={parseEndpoints(values.endpoints)} writesAllowed={(values.allow_writes ?? "false") === "true"}
                  onChange={(next) => setValues((v) => ({ ...v, endpoints: serializeEndpoints(next) }))}
                  onBaseUrl={(url) => setValues((v) => (v.base_url ? v : { ...v, base_url: url }))} />
              </Field>
            )}
            <Toggle checked={usableInTasks} onChange={setUsableInTasks} label="Members can use it in their tasks"
              description="Off: only admins link it, from workspaces and solutions. On: anyone can attach it to a chat, using these credentials (never seeing them)." />
          </>
        )}
      </div>
    </Modal>
  );
}
