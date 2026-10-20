"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "@/components/platform/AuthProvider";
import {
  API_BASE,
  apiErrorMessage,
  createEndUserToken,
  createSolutionRecord,
  deleteSolutionRecord,
  getSolutionUsage,
  publishInstalledSolution,
  searchRegistry,
  solutionUsageCsvUrl,
  updateSolutionRecord,
  getSolution,
  listSolutionRecords,
  listSolutions,
  runSolutionEvals,
  uninstallSolution,
  type EvalReport,
  type RegistryPackage,
  type SolutionUsage,
  type SolutionFieldDef,
  type SolutionRecord,
  type SolutionSummary,
  type SolutionView,
} from "@/lib/api";
import { atLeast, type Role } from "@/lib/platformTypes";
import { Badge, Button, Card, CardHeader, CopyField, EmptyState, ErrorBanner, Field, Modal, PageHeader, Stat, Tabs, Toggle, ago, cx, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { DeployDialog } from "@/components/solutions/DeployDialog";

/**
 * Solutions (docs/solutions.md): business apps built on the platform and installed as one unit,
 * each with its workspaces, its data model and records, the assistant its own customers talk to,
 * and its evals. Builders usually deploy them with the aktor CLI; this page shows what's installed
 * and can deploy a package folder too.
 */
export default function SolutionsPage() {
  return <Suspense><Solutions /></Suspense>;
}

function Solutions() {
  const params = useSearchParams();
  const id = params.get("id");
  const { me } = useAuth();
  const role = (me?.role ?? "Viewer") as Role;
  const [deploying, setDeploying] = useState<{ installId?: string; inputs?: Record<string, string>; packageRef?: string } | null>(null);
  const [refresh, setRefresh] = useState(0);
  const router = useRouter();

  return (
    <div>
      {id ? (
        <SolutionDetail key={`${id}-${refresh}`} id={id} role={role} onUpgrade={(s) => setDeploying({ installId: s.install_id, inputs: s.inputs })} />
      ) : (
        <SolutionList key={refresh} role={role} onDeploy={(packageRef, installId) => setDeploying({ packageRef, installId })} />
      )}
      {deploying && (
        <DeployDialog
          installId={deploying.installId}
          packageRef={deploying.packageRef}
          currentInputs={deploying.inputs}
          onClose={() => setDeploying(null)}
          onDone={(installId) => {
            setDeploying(null);
            setRefresh((n) => n + 1);
            router.push(`/solutions?id=${encodeURIComponent(installId)}`);
          }}
        />
      )}
    </div>
  );
}

// ---------------------------------------------------------------- list

function SolutionList({ role, onDeploy }: { role: Role; onDeploy: (packageRef?: string, installId?: string) => void }) {
  const [solutions, setSolutions] = useState<SolutionSummary[] | null>(null);
  const [packages, setPackages] = useState<RegistryPackage[] | null>(null);
  const [query, setQuery] = useState("");
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    listSolutions().then(setSolutions).catch((e) => setError(apiErrorMessage(e)));
  }, []);
  useEffect(() => {
    const t = setTimeout(() => searchRegistry(query.trim() || undefined).then(setPackages).catch((e) => setError(apiErrorMessage(e))), 250);
    return () => clearTimeout(t);
  }, [query]);

  return (
    <>
      <PageHeader
        title="Solutions"
        description="Business apps built on the platform and installed as one unit: their workspaces, their own data model, the assistant their customers talk to, and the evals that prove they work. Upgrades change only what the new version changed."
        actions={atLeast(role, "Member") && (
          <>
            {atLeast(role, "Admin") && <Button icon={<Icons.Upload className="h-3.5 w-3.5" />} onClick={() => onDeploy()}>Deploy a package folder</Button>}
            <Link href="/solutions/build" className="inline-flex h-9 items-center gap-1.5 rounded-lg bg-brand-500 px-3.5 text-sm font-medium text-white shadow-sm hover:bg-brand-600">
              <Icons.Plus className="h-3.5 w-3.5" /> New solution
            </Link>
          </>
        )}
      />
      <div className="mx-auto max-w-6xl space-y-6 px-6 py-6">
        <ErrorBanner error={error} onClose={() => setError(null)} />
        {solutions && solutions.length === 0 && (
          <EmptyState
            icon={<Icons.Solutions className="h-5 w-5" />}
            title="No solutions installed"
            description="Build one here with forms (data, workflows, an assistant for your customers) and deploy it in a click, or deploy a package folder made with the CLI."
            action={atLeast(role, "Member") ? <Link href="/solutions/build" className="inline-flex h-9 items-center rounded-lg bg-brand-500 px-3.5 text-sm font-medium text-white hover:bg-brand-600">Build a solution</Link> : undefined}
          />
        )}
        {solutions && solutions.length > 0 && (
          <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
            {solutions.map((s) => (
              <Link key={s.install_id} href={`/solutions?id=${encodeURIComponent(s.install_id)}`}>
                <Card className="h-full p-5 transition hover:border-brand-300 dark:hover:border-brand-700">
                  <div className="mb-3 flex items-center gap-3">
                    <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-brand-50 text-brand-600 dark:bg-brand-950/60 dark:text-brand-400">
                      <Icons.Solutions className="h-5 w-5" />
                    </div>
                    <div className="min-w-0">
                      <div className="truncate font-semibold text-zinc-900 dark:text-zinc-100">{s.title}</div>
                      <div className="font-mono text-[11px] text-zinc-500">{s.name}</div>
                    </div>
                    <Badge tone={s.status === "Active" ? "green" : "amber"} className="ml-auto">{s.status}</Badge>
                  </div>
                  <div className="flex items-center justify-between text-xs text-zinc-500">
                    <span>Version <span className="font-mono text-zinc-700 dark:text-zinc-300">{s.version}</span></span>
                    <span>Updated {ago(s.updated_at)}</span>
                  </div>
                </Card>
              </Link>
            ))}
          </div>
        )}
        {solutions === null && !error && <div className="text-sm text-zinc-500">Loading…</div>}

        <Card>
          <CardHeader
            title="Registry"
            description="Packages published on this server: your organization's, and other organizations' public ones. Every version is signed when it's published and checked when it's installed."
            actions={<input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search packages" className={cx(inputClass, "h-8 w-56 py-1 text-xs")} aria-label="Search the registry" />}
          />
          <ul className="divide-y divide-zinc-100 dark:divide-zinc-800">
            {packages?.map((p) => {
              const installed = solutions?.find((s) => s.name === p.name && s.status === "Active");
              return (
                <li key={p.package} className="flex items-center gap-4 px-5 py-3">
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="font-medium text-zinc-900 dark:text-zinc-100">{p.title}</span>
                      <span className="font-mono text-[11px] text-zinc-500">{p.name}@{p.version}</span>
                      <Badge tone={p.visibility === "public" ? "green" : "neutral"}>{p.visibility === "public" ? "Public" : "Your organization"}</Badge>
                    </div>
                    <div className="mt-0.5 truncate text-xs text-zinc-500">{p.description || "—"} · by {p.publisher} · {p.installs} install{p.installs === 1 ? "" : "s"}</div>
                  </div>
                  {atLeast(role, "Admin") && (
                    installed && installed.version === p.version
                      ? <Badge tone="green">Installed</Badge>
                      : <Button size="sm" onClick={() => onDeploy(`${p.name}@${p.version}`, installed?.install_id)}>{installed ? `Upgrade from ${installed.version}` : "Install"}</Button>
                  )}
                </li>
              );
            })}
            {packages && packages.length === 0 && (
              <li className="px-5 py-4 text-sm text-zinc-500">{query ? "No packages match." : "Nothing published yet. Publish an installed solution from its page, or run aktor publish."}</li>
            )}
          </ul>
        </Card>

        <Card>
          <CardHeader title="Prefer the terminal?" description="Everything the builder does, the aktor CLI does too: scaffold, validate, deploy, test. Give it an Admin API key from Settings → API keys." />
          <div className="space-y-3 p-5">
            <CopyField multiline value={`export AKTOR_URL=${API_BASE} AKTOR_API_KEY=ak_...
npx aktor init my-app && cd my-app
npx aktor validate     # check it against this server
npx aktor deploy       # install, or upgrade the installed version
npx aktor dev          # redeploy on every change while you work
npx aktor test         # run its evals for real`} />
            <p className="text-xs text-zinc-500">
              See <span className="font-mono">samples/solutions/property-mgmt</span> for a complete example: records, a record trigger,
              a compiled watch, skills, knowledge, a resident assistant and an eval.
            </p>
          </div>
        </Card>
      </div>
    </>
  );
}

// ---------------------------------------------------------------- detail

type Tab = "overview" | "data" | "endusers" | "usage" | "evals" | "history";

function SolutionDetail({ id, role, onUpgrade }: { id: string; role: Role; onUpgrade: (s: SolutionView) => void }) {
  const [solution, setSolution] = useState<SolutionView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tab, setTab] = useState<Tab>("overview");
  const router = useRouter();

  useEffect(() => {
    getSolution(id).then(setSolution).catch((e) => setError(apiErrorMessage(e)));
  }, [id]);

  const [notice, setNotice] = useState<string | null>(null);
  async function publish() {
    if (!solution) return;
    const visibility = window.confirm("Make it public, so every organization on this server can install it?\n\nOK: public · Cancel: your organization only") ? "public" : "organization";
    try {
      const p = await publishInstalledSolution(solution.install_id, visibility);
      setNotice(`Published ${p.package} (${p.visibility === "public" ? "public" : "your organization only"}).`);
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  async function uninstall(purge: boolean) {
    if (!solution) return;
    const what = purge ? "Uninstall and permanently delete all of its records?" : "Uninstall? Its workspaces are archived; its records are kept.";
    if (!window.confirm(what)) return;
    try {
      await uninstallSolution(solution.install_id, purge);
      router.push("/solutions");
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  if (!solution) {
    return (
      <div className="mx-auto max-w-6xl space-y-4 px-6 py-6">
        <ErrorBanner error={error} />
        {!error && <div className="text-sm text-zinc-500">Loading…</div>}
      </div>
    );
  }

  const records = solution.entities.reduce((n, e) => n + e.records, 0);
  const admin = atLeast(role, "Admin") && solution.status === "Active";
  return (
    <>
      <PageHeader
        title={<span className="flex items-center gap-2">{solution.title} <Badge tone={solution.status === "Active" ? "green" : "amber"}>{solution.status}</Badge></span>}
        description={solution.description ?? undefined}
        actions={admin && (
          <>
            <Link href={`/solutions/build?from=${encodeURIComponent(solution.install_id)}`}
              className="inline-flex h-9 items-center gap-1.5 rounded-lg bg-brand-500 px-3.5 text-sm font-medium text-white shadow-sm hover:bg-brand-600">
              Edit in builder
            </Link>
            <Button icon={<Icons.Upload className="h-3.5 w-3.5" />} onClick={() => onUpgrade(solution)}>Upgrade from a folder</Button>
            <Button onClick={() => publish()}>Publish to registry</Button>
            <Button variant="danger" onClick={() => uninstall(false)}>Uninstall</Button>
          </>
        )}
      >
        <div className="mt-2 text-xs text-zinc-500">
          <Link href="/solutions" className="hover:text-zinc-800 dark:hover:text-zinc-200">Solutions</Link> / <span className="font-mono">{solution.name}</span>
        </div>
      </PageHeader>
      <div className="mx-auto max-w-6xl space-y-5 px-6 py-6">
        <ErrorBanner error={error} onClose={() => setError(null)} />
        {notice && <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700 dark:border-emerald-900/60 dark:bg-emerald-950/40 dark:text-emerald-300">{notice}</div>}
        <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
          <Stat label="Version" value={<span className="font-mono">{solution.version}</span>} hint={`updated ${ago(solution.updated_at)}`} />
          <Stat label="Workspaces" value={solution.workspaces.length} />
          <Stat label="Records" value={records} hint={`${solution.entities.length} entities`} />
          <Stat label="Assistant" value={solution.assistant ? solution.assistant.name : "—"} hint={solution.assistant ? "for end users" : "none"} />
        </div>

        <Tabs<Tab>
          value={tab}
          onChange={setTab}
          tabs={[
            { id: "overview", label: "Overview" },
            { id: "data", label: "Data", count: solution.entities.length },
            { id: "endusers", label: "End users" },
            { id: "usage", label: "Usage" },
            { id: "evals", label: "Evals", count: solution.evals.length },
            { id: "history", label: "History", count: solution.history.length },
          ]}
        />

        {tab === "overview" && <Overview solution={solution} />}
        {tab === "data" && <DataBrowser solution={solution} canEdit={atLeast(role, "Member") && solution.status === "Active"} />}
        {tab === "endusers" && <EndUsers solution={solution} canIssue={admin} />}
        {tab === "usage" && <UsageView solution={solution} />}
        {tab === "evals" && <Evals solution={solution} canRun={atLeast(role, "Member") && solution.status === "Active"} />}
        {tab === "history" && <History solution={solution} />}

        {admin && tab === "overview" && (
          <div className="flex justify-end">
            <button onClick={() => uninstall(true)} className="text-xs text-rose-600 hover:underline dark:text-rose-400">Uninstall and delete its records…</button>
          </div>
        )}
      </div>
    </>
  );
}

function Overview({ solution }: { solution: SolutionView }) {
  return (
    <div className="grid gap-5 lg:grid-cols-2">
      <Card>
        <CardHeader title="Workspaces" description="Each runs its own pipeline, triggers and safety policy. Open one to watch its runs and agents." />
        <ul className="divide-y divide-zinc-100 dark:divide-zinc-800">
          {solution.workspaces.map((w) => (
            <li key={w.key} className="flex items-center justify-between gap-3 px-5 py-3">
              <div className="min-w-0">
                <div className="truncate text-sm font-medium text-zinc-900 dark:text-zinc-100">{w.name ?? w.key}</div>
                <div className="font-mono text-[11px] text-zinc-500">{w.key}</div>
              </div>
              <Link href={`/workspaces?id=${encodeURIComponent(w.workspace_id)}`} className="inline-flex items-center gap-1 text-xs font-medium text-brand-600 hover:underline dark:text-brand-400">
                Open <Icons.ChevronRight className="h-3.5 w-3.5" />
              </Link>
            </li>
          ))}
          {solution.workspaces.length === 0 && <li className="px-5 py-3 text-sm text-zinc-500">No workspaces: this solution is only an assistant.</li>}
        </ul>
      </Card>

      <Card>
        <CardHeader title="Setup" description="Values it was installed with, and what it connects to." />
        <div className="space-y-4 p-5 text-sm">
          <div>
            <div className="mb-1.5 text-xs font-medium text-zinc-500">Inputs</div>
            {Object.keys(solution.inputs).length === 0 ? <div className="text-zinc-500">None</div> : (
              <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
                {Object.entries(solution.inputs).map(([k, v]) => (
                  <div key={k} className="contents"><dt className="font-mono text-xs text-zinc-500">{k}</dt><dd className="truncate text-zinc-800 dark:text-zinc-200">{v}</dd></div>
                ))}
              </dl>
            )}
          </div>
          <div>
            <div className="mb-1.5 text-xs font-medium text-zinc-500">Connectors</div>
            <div className="flex flex-wrap gap-1.5">
              <Badge tone="brand">data (its records)</Badge>
              {solution.connectors.map((c) => (
                <Badge key={c.name} tone={c.global ? "brand" : "neutral"}>{c.name} · {c.plugin}{c.global ? ` · Global: ${c.global}` : ""}</Badge>
              ))}
            </div>
          </div>
          {solution.webhooks.length > 0 && (
            <div className="space-y-2">
              <div className="text-xs font-medium text-zinc-500">Webhooks to your backend</div>
              {solution.webhooks.map((h) => <div key={h.url} className="font-mono text-xs text-zinc-700 dark:text-zinc-300">{h.events.join(", ")} → {h.url}</div>)}
              {solution.webhook_secret && <CopyField label="Signing secret (X-Aktor-Signature: sha256=HMAC of the body)" value={solution.webhook_secret} />}
            </div>
          )}
        </div>
      </Card>
    </div>
  );
}

function DataBrowser({ solution, canEdit }: { solution: SolutionView; canEdit: boolean }) {
  const [entity, setEntity] = useState(solution.entities[0]?.name ?? "");
  const [filter, setFilter] = useState("");
  const [applied, setApplied] = useState("");
  const [page, setPage] = useState<{ total: number; items: SolutionRecord[] } | null>(null);
  const [offset, setOffset] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<SolutionRecord | "new" | null>(null);
  const [reload, setReload] = useState(0);
  const def = solution.entities.find((e) => e.name === entity);
  const limit = 25;

  useEffect(() => {
    if (!entity) return;
    listSolutionRecords(solution.install_id, entity, { limit, offset, filters: applied ? applied.split(",").map((f) => f.trim()).filter(Boolean) : [] })
      .then((p) => { setPage(p); setError(null); })
      .catch((e) => setError(apiErrorMessage(e)));
  }, [solution.install_id, entity, offset, applied, reload]);

  if (!def) return <EmptyState title="No data model" description="This solution doesn't declare any entities." />;
  const columns = Object.keys(def.fields).filter((f) => def.fields[f].type !== "json").slice(0, 6);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        {solution.entities.map((e) => (
          <button key={e.name} onClick={() => { setEntity(e.name); setOffset(0); }}
            className={cx("rounded-full border px-3 py-1 text-xs transition",
              e.name === entity ? "border-brand-400 bg-brand-50 text-brand-700 dark:border-brand-600 dark:bg-brand-500/10 dark:text-brand-300"
                : "border-zinc-200 text-zinc-600 hover:border-zinc-300 dark:border-zinc-700 dark:text-zinc-400")}>
            {e.name} <span className="ml-1 text-zinc-400">{e.records}</span>
          </button>
        ))}
        <form className="ml-auto flex items-center gap-2" onSubmit={(ev) => { ev.preventDefault(); setOffset(0); setApplied(filter); }}>
          <input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Filter, e.g. status==open"
            className={cx(inputClass, "h-8 w-64 py-1 text-xs")} aria-label="Filter records, e.g. status==open or ends_on<=today+30d" title="Comma-separated: status==open, ends_on<=today+30d, issue~leak" />
          <Button size="sm" type="submit">Filter</Button>
          {canEdit && <Button type="button" size="sm" variant="primary" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => setEditing("new")}>New {entity}</Button>}
        </form>
      </div>

      <Card>
        <CardHeader
          title={entity}
          description={<>
            <span className="block">{def.description ?? "Records agents and apps read and write; every write is checked against these fields."}</span>
            <span className="mt-1 block font-mono text-[11px] text-zinc-400">{Object.entries(def.fields).map(([k, f]) => `${k}: ${f.type}${f.required ? "*" : ""}`).join("  ·  ")}</span>
          </>}
        />
        <ErrorBanner error={error} />
        <div className="overflow-x-auto">
          <table className="w-full text-left text-xs">
            <thead className="border-b border-zinc-200 text-[11px] uppercase tracking-wide text-zinc-500 dark:border-zinc-800">
              <tr>
                <th className="px-4 py-2 font-medium">id</th>
                {columns.map((c) => <th key={c} className="px-4 py-2 font-medium">{c}</th>)}
                <th className="px-4 py-2 font-medium">updated</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-zinc-100 dark:divide-zinc-800">
              {page?.items.map((r) => (
                <tr key={r.id} className={cx("align-top", canEdit && "cursor-pointer hover:bg-zinc-50 dark:hover:bg-zinc-800/40")}
                  onClick={canEdit ? () => setEditing(r) : undefined} title={canEdit ? "Edit" : undefined}>
                  <td className="whitespace-nowrap px-4 py-2 font-mono text-zinc-500">{r.id}</td>
                  {columns.map((c) => (
                    <td key={c} className="max-w-[18rem] truncate px-4 py-2 text-zinc-800 dark:text-zinc-200" title={display(r[c])}>
                      {def.fields[c].type === "enum" && r[c] != null ? <Badge>{display(r[c])}</Badge> : display(r[c])}
                    </td>
                  ))}
                  <td className="whitespace-nowrap px-4 py-2 text-zinc-500">{ago(r.updated_at)} <span className="text-zinc-400">v{r.version}</span></td>
                </tr>
              ))}
              {page && page.items.length === 0 && (
                <tr><td colSpan={columns.length + 2} className="px-4 py-6 text-center text-zinc-500">No {entity} records{applied ? " match" : " yet"}.</td></tr>
              )}
            </tbody>
          </table>
        </div>
        {page && page.total > limit && (
          <div className="flex items-center justify-between border-t border-zinc-200 px-4 py-2 text-xs text-zinc-500 dark:border-zinc-800">
            <span>{offset + 1}–{Math.min(offset + limit, page.total)} of {page.total}</span>
            <div className="flex gap-2">
              <Button size="sm" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - limit))}>Previous</Button>
              <Button size="sm" disabled={offset + limit >= page.total} onClick={() => setOffset(offset + limit)}>Next</Button>
            </div>
          </div>
        )}
      </Card>
      {editing && (
        <RecordEditor
          installId={solution.install_id}
          entity={entity}
          fields={def.fields}
          record={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); setReload((n) => n + 1); }}
        />
      )}
    </div>
  );
}

/** A form for one record, by field type. Updates send only what changed, with the version read,
 * so someone else's change in between is refused instead of overwritten. */
function RecordEditor({ installId, entity, fields, record, onClose, onSaved }: {
  installId: string; entity: string; fields: Record<string, SolutionFieldDef>; record: SolutionRecord | null; onClose: () => void; onSaved: () => void;
}) {
  const initial = Object.fromEntries(Object.keys(fields).map((k) => [k, toInput(fields[k], record?.[k])]));
  const [values, setValues] = useState<Record<string, string>>(initial);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const out: Record<string, unknown> = {};
      for (const [k, def] of Object.entries(fields)) {
        if (def.type === "end_user" && record) continue; // fixed once set
        if (record && values[k] === initial[k]) continue;
        const v = fromInput(def, values[k]);
        if (v === undefined) { if (record) out[k] = null; continue; }
        out[k] = v;
      }
      if (record) await updateSolutionRecord(installId, entity, record.id, out, record.version);
      else await createSolutionRecord(installId, entity, out);
      onSaved();
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    if (!record || !window.confirm(`Delete this ${entity}?`)) return;
    setBusy(true);
    try {
      await deleteSolutionRecord(installId, entity, record.id);
      onSaved();
    } catch (e) {
      setError(apiErrorMessage(e));
      setBusy(false);
    }
  }

  return (
    <Modal open wide onClose={onClose} title={record ? `${entity} ${record.id}` : `New ${entity}`}
      description={record ? `Version ${record.version}. Changes start any record triggers, as a change from your backend would.` : "Checked against the data model when you save."}
      footer={
        <>
          {record && <Button variant="danger" className="mr-auto" disabled={busy} onClick={remove}>Delete</Button>}
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={busy} onClick={save}>{busy ? "Saving…" : "Save"}</Button>
        </>
      }>
      <div className="space-y-3">
        <ErrorBanner error={error} onClose={() => setError(null)} />
        <div className="grid gap-3 md:grid-cols-2">
          {Object.entries(fields).map(([k, def]) => {
            const set = (v: string) => setValues({ ...values, [k]: v });
            const label = `${k}${def.required ? " *" : ""}`;
            const hint = def.type === "ref" ? `The id of a ${def.entity} record` : def.type === "end_user" ? (record ? "Set when created; it can't change" : "Your id for the end user it belongs to") : def.description ?? def.type;
            const wide = def.type === "text" || def.type === "json";
            return (
              <Field key={k} label={label} hint={hint} className={wide ? "md:col-span-2" : undefined}>
                {def.type === "enum" ? (
                  <select className={inputClass} value={values[k]} onChange={(e) => set(e.target.value)}>
                    <option value="">—</option>
                    {(def.values ?? []).map((v) => <option key={v} value={v}>{v}</option>)}
                  </select>
                ) : def.type === "boolean" ? (
                  <select className={inputClass} value={values[k]} onChange={(e) => set(e.target.value)}>
                    <option value="">—</option><option value="true">true</option><option value="false">false</option>
                  </select>
                ) : wide ? (
                  <textarea className={cx(inputClass, "min-h-24 font-mono text-xs")} value={values[k]} onChange={(e) => set(e.target.value)} />
                ) : (
                  <input className={inputClass} value={values[k]} onChange={(e) => set(e.target.value)} disabled={def.type === "end_user" && !!record}
                    type={def.type === "integer" || def.type === "number" ? "number" : def.type === "date" ? "date" : "text"}
                    step={def.type === "number" ? "any" : undefined}
                    placeholder={def.type === "datetime" ? "2026-10-17T09:00:00Z" : undefined} />
                )}
              </Field>
            );
          })}
        </div>
      </div>
    </Modal>
  );
}

function toInput(def: SolutionFieldDef, v: unknown): string {
  if (v === null || v === undefined) return "";
  if (def.type === "json") return JSON.stringify(v, null, 2);
  return String(v);
}

function fromInput(def: SolutionFieldDef, v: string): unknown {
  if (v.trim() === "") return undefined;
  switch (def.type) {
    case "integer": case "number": return Number(v);
    case "boolean": return v === "true";
    case "json": try { return JSON.parse(v); } catch { return v; }
    default: return v;
  }
}

function display(v: unknown) {
  if (v === null || v === undefined || v === "") return "—";
  return typeof v === "string" ? v : JSON.stringify(v);
}

type AktorChatApi = { mount: (o: Record<string, unknown>) => { destroy: () => void } };

function EndUsers({ solution, canIssue }: { solution: SolutionView; canIssue: boolean }) {
  const [userId, setUserId] = useState("");
  const [name, setName] = useState("");
  const [token, setToken] = useState<{ token: string; expires_at: string; user: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState(false);
  const container = useRef<HTMLDivElement>(null);

  const mountPreview = useCallback(() => {
    if (!token || !container.current) return;
    const el = container.current;
    let widget: { destroy: () => void } | null = null;
    const start = () => {
      const chat = (window as unknown as { AktorChat?: AktorChatApi }).AktorChat;
      if (chat) widget = chat.mount({ install: solution.install_id, token: token.token, apiBase: API_BASE, container: el });
    };
    if ((window as unknown as { AktorChat?: AktorChatApi }).AktorChat) start();
    else {
      const s = document.createElement("script");
      s.src = `${API_BASE}/embed.js`;
      s.onload = start;
      document.body.appendChild(s);
    }
    return () => widget?.destroy();
  }, [token, solution.install_id]);

  useEffect(() => (preview ? mountPreview() : undefined), [preview, mountPreview]);

  if (!solution.assistant) {
    return <EmptyState title="No assistant" description="This solution doesn't give its end users an assistant. Add an assistant section to its aktor.yaml to have one." />;
  }

  async function issue() {
    setError(null);
    setPreview(false);
    try {
      const t = await createEndUserToken(solution.install_id, userId.trim(), name.trim());
      setToken({ ...t, user: userId.trim() });
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  const snippet = `<script src="${API_BASE}/embed.js"
        data-install="${solution.install_id}"
        data-token-url="/api/aktor-token"
        async></script>`;

  return (
    <div className="grid gap-5 lg:grid-cols-2">
      <Card>
        <CardHeader title={solution.assistant.name} description="What your customers talk to. Each end user's conversations and records are theirs alone, enforced by the runtime." />
        <div className="space-y-4 p-5 text-sm">
          {solution.assistant.greeting && <p className="rounded-lg bg-zinc-50 px-3 py-2 text-zinc-700 dark:bg-zinc-950 dark:text-zinc-300">“{solution.assistant.greeting}”</p>}
          <div className="flex flex-wrap gap-1.5">
            {Object.entries(solution.assistant.data).map(([e, a]) => <Badge key={e} tone={a === "write" ? "amber" : "neutral"}>{e}: {a}</Badge>)}
          </div>
          <CopyField multiline label="Embed it on your site" value={snippet} />
          <p className="text-xs text-zinc-500">
            <span className="font-mono">/api/aktor-token</span> is your backend: after your own sign-in it calls
            <span className="font-mono"> POST /api/v1/solutions/{solution.install_id}/end-user-tokens</span> with an API key and returns <span className="font-mono">{"{ token }"}</span>.
          </p>
        </div>
      </Card>

      <Card>
        <CardHeader title="Try it as an end user" description="Issue a short-lived token for a user id of yours and talk to the assistant as them." />
        <div className="space-y-3 p-5">
          <ErrorBanner error={error} onClose={() => setError(null)} />
          {canIssue ? (
            <form className="grid grid-cols-2 gap-3" onSubmit={(e) => { e.preventDefault(); issue(); }}>
              <Field label="User id"><input className={inputClass} value={userId} onChange={(e) => setUserId(e.target.value)} placeholder="customer-42" required /></Field>
              <Field label="Name (optional)"><input className={inputClass} value={name} onChange={(e) => setName(e.target.value)} placeholder="Sam" /></Field>
              <div className="col-span-2 flex justify-end"><Button type="submit" variant="primary" disabled={!userId.trim()}>Issue token</Button></div>
            </form>
          ) : (
            <p className="text-sm text-zinc-500">Admins can issue end-user tokens.</p>
          )}
          {token && (
            <div className="space-y-3">
              <CopyField label={`Token for ${token.user} (expires ${new Date(token.expires_at).toLocaleTimeString()})`} value={token.token} />
              <Toggle checked={preview} onChange={setPreview} label="Open the widget here" description="The same widget your site embeds, signed in as this user." />
              {preview && <div ref={container} className="h-[440px] overflow-hidden rounded-xl border border-zinc-200 dark:border-zinc-800" />}
            </div>
          )}
        </div>
      </Card>
    </div>
  );
}

function Evals({ solution, canRun }: { solution: SolutionView; canRun: boolean }) {
  const [report, setReport] = useState<EvalReport | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function run() {
    setRunning(true);
    setError(null);
    try {
      setReport(await runSolutionEvals(solution.install_id));
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setRunning(false);
    }
  }

  if (solution.evals.length === 0) {
    return <EmptyState title="No evals" description="Evals are expected behaviours (a run's status, its summary, the records it writes) checked by running the workspace for real. Add them under evals in aktor.yaml." />;
  }

  return (
    <Card>
      <CardHeader
        title="Evals"
        description="Each one starts a real run with its input and checks the outcome. They take as long as the runs do and spend like any run."
        actions={canRun && <Button variant="primary" icon={<Icons.Play className="h-3.5 w-3.5" />} disabled={running} onClick={run}>{running ? "Running…" : "Run all"}</Button>}
      />
      <ErrorBanner error={error} />
      <ul className="divide-y divide-zinc-100 dark:divide-zinc-800">
        {solution.evals.map((e) => {
          const r = report?.results.find((x) => x.name === e.name);
          return (
            <li key={e.name} className="px-5 py-3">
              <div className="flex items-center gap-3">
                {r ? (r.passed ? <Icons.Check className="h-4 w-4 text-emerald-500" /> : <Icons.X className="h-4 w-4 text-rose-500" />) : <span className="h-4 w-4 rounded-full border border-zinc-300 dark:border-zinc-700" />}
                <span className="text-sm font-medium text-zinc-900 dark:text-zinc-100">{e.name}</span>
                <span className="font-mono text-[11px] text-zinc-500">{e.workspace}</span>
                {r?.duration_ms != null && <span className="ml-auto text-xs text-zinc-500">{(r.duration_ms / 1000).toFixed(1)}s</span>}
                {r?.run_id && <Link href={`/?task=${encodeURIComponent(r.run_id)}`} className="text-xs text-brand-600 hover:underline dark:text-brand-400">run</Link>}
              </div>
              {r && !r.passed && <ul className="ml-7 mt-1 list-disc space-y-0.5 pl-4 text-xs text-rose-600 dark:text-rose-400">{r.failures.map((f) => <li key={f}>{f}</li>)}</ul>}
            </li>
          );
        })}
      </ul>
      {report && (
        <div className="border-t border-zinc-200 px-5 py-3 text-sm dark:border-zinc-800">
          {report.failed === 0 ? <span className="text-emerald-600 dark:text-emerald-400">All {report.passed} passed.</span> : <span className="text-rose-600 dark:text-rose-400">{report.failed} failed, {report.passed} passed.</span>}
        </div>
      )}
    </Card>
  );
}

function UsageView({ solution }: { solution: SolutionView }) {
  const [by, setBy] = useState<SolutionUsage["by"]>("end_user");
  const [usage, setUsage] = useState<SolutionUsage | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    getSolutionUsage(solution.install_id, by).then((u) => { setUsage(u); setError(null); }).catch((e) => setError(apiErrorMessage(e)));
  }, [solution.install_id, by]);

  return (
    <div className="space-y-4">
      <ErrorBanner error={error} />
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <Stat label="Cost this month" value={usage ? `$${usage.cost_usd.toFixed(4)}` : "—"} />
        <Stat label="Tokens" value={usage ? usage.tokens.toLocaleString() : "—"} />
        <Stat label="Conversations" value={usage?.conversations ?? "—"} hint="end users' chats" />
        <Stat label="Runs" value={usage?.runs ?? "—"} hint="workspace pipelines" />
      </div>
      <Card>
        <CardHeader
          title="Usage"
          description="What the solution cost, from the runtime's own metering, so you can pass it on to your customers. Conversations count against the end user who had them; workspace runs against no one."
          actions={
            <>
              <select className={cx(inputClass, "h-8 w-40 py-1 text-xs")} value={by} onChange={(e) => setBy(e.target.value as SolutionUsage["by"])} aria-label="Group by">
                <option value="end_user">By end user</option>
                <option value="workspace">By workspace</option>
                <option value="day">By day</option>
              </select>
              <a href={solutionUsageCsvUrl(solution.install_id, by)} className="inline-flex h-8 items-center gap-1.5 rounded-lg border border-zinc-200 px-2.5 text-xs font-medium text-zinc-700 hover:bg-zinc-50 dark:border-zinc-700 dark:text-zinc-200 dark:hover:bg-zinc-800">
                <Icons.Download className="h-3.5 w-3.5" /> CSV
              </a>
            </>
          }
        />
        <table className="w-full text-left text-xs">
          <thead className="border-b border-zinc-200 text-[11px] uppercase tracking-wide text-zinc-500 dark:border-zinc-800">
            <tr><th className="px-4 py-2 font-medium">{by.replace("_", " ")}</th><th className="px-4 py-2 font-medium">conversations</th><th className="px-4 py-2 font-medium">runs</th><th className="px-4 py-2 font-medium">tokens</th><th className="px-4 py-2 text-right font-medium">cost</th></tr>
          </thead>
          <tbody className="divide-y divide-zinc-100 dark:divide-zinc-800">
            {usage?.lines.map((l) => (
              <tr key={l.key}>
                <td className="px-4 py-2 font-mono text-zinc-800 dark:text-zinc-200">{l.key}</td>
                <td className="px-4 py-2 tabular-nums">{l.conversations}</td>
                <td className="px-4 py-2 tabular-nums">{l.runs}</td>
                <td className="px-4 py-2 tabular-nums">{l.tokens.toLocaleString()}</td>
                <td className="px-4 py-2 text-right tabular-nums">${l.cost_usd.toFixed(4)}</td>
              </tr>
            ))}
            {usage && usage.lines.length === 0 && <tr><td colSpan={5} className="px-4 py-6 text-center text-zinc-500">No usage in this period yet.</td></tr>}
          </tbody>
        </table>
      </Card>
    </div>
  );
}

function History({ solution }: { solution: SolutionView }) {
  return (
    <Card>
      <CardHeader title="History" description="Every install, upgrade and redeploy of this solution." />
      <ol className="divide-y divide-zinc-100 dark:divide-zinc-800">
        {[...solution.history].reverse().map((h, i) => (
          <li key={i} className="flex items-center gap-3 px-5 py-3 text-sm">
            <Badge tone={h.action === "install" ? "green" : h.action.startsWith("uninstall") ? "red" : "blue"}>{h.action}</Badge>
            <span className="font-mono text-zinc-800 dark:text-zinc-200">{h.from ? `${h.from} → ${h.version}` : h.version}</span>
            <span className="ml-auto text-xs text-zinc-500">{h.by ? `${h.by} · ` : ""}{new Date(h.at).toLocaleString()}</span>
          </li>
        ))}
      </ol>
    </Card>
  );
}
