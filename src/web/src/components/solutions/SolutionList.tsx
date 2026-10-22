"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useEffect, useMemo, useState } from "react";
import {
  API_BASE,
  apiErrorMessage,
  listSolutionTemplates,
  listSolutions,
  searchRegistry,
  type RegistryPackage,
  type SolutionSummary,
  type SolutionTemplate,
} from "@/lib/api";
import { atLeast, type Role } from "@/lib/platformTypes";
import { Badge, Button, Card, CardHeader, CopyField, EmptyState, ErrorBanner, PageHeader, Tabs, ago, cx, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

type View = "installed" | "templates" | "registry";
type StatusFilter = "active" | "all";
const PAGE = 20;

/**
 * The Solutions page: what's installed (searchable, filtered and paged, so it stays usable with
 * hundreds), the templates to start from, and the registry. A new organization lands on a short
 * "how it works" path and the templates instead of an empty table.
 */
export function SolutionList({ role, onDeploy }: { role: Role; onDeploy: (packageRef?: string, installId?: string) => void }) {
  const params = useSearchParams();
  const router = useRouter();
  const [solutions, setSolutions] = useState<SolutionSummary[] | null>(null);
  const [templates, setTemplates] = useState<SolutionTemplate[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<StatusFilter>("active");
  const requested = params.get("view") as View | null;

  useEffect(() => {
    listSolutions(status === "all").then(setSolutions).catch((e) => setError(apiErrorMessage(e)));
  }, [status]);
  useEffect(() => {
    listSolutionTemplates().then(setTemplates).catch(() => setTemplates([]));
  }, []);

  const member = atLeast(role, "Member");
  const admin = atLeast(role, "Admin");
  const empty = solutions !== null && solutions.length === 0 && status === "active";
  const view: View = requested ?? (empty ? "templates" : "installed");
  const show = (v: View) => router.replace(v === "installed" ? "/solutions" : `/solutions?view=${v}`, { scroll: false });

  return (
    <>
      <PageHeader
        title="Solutions"
        description="Business apps built from workflows, your own data and an assistant for your customers, deployed as one unit and connected to your product with an app key."
        actions={member && (
          <>
            {admin && <Button icon={<Icons.Upload className="h-3.5 w-3.5" />} onClick={() => onDeploy()}>Deploy a folder</Button>}
            <Link href="/solutions/build" className="inline-flex h-9 items-center gap-1.5 rounded-lg bg-brand-500 px-3.5 text-sm font-medium text-white shadow-sm hover:bg-brand-600">
              <Icons.Plus className="h-3.5 w-3.5" /> New solution
            </Link>
          </>
        )}
      />
      <div className="mx-auto max-w-6xl space-y-5 px-6 py-6">
        <ErrorBanner error={error} onClose={() => setError(null)} />
        {empty && <GettingStarted canBuild={member} />}
        <Tabs<View>
          value={view}
          onChange={show}
          tabs={[
            { id: "installed", label: "Installed", count: solutions?.length },
            { id: "templates", label: "Templates", count: templates?.length },
            { id: "registry", label: "Registry" },
          ]}
        />
        {view === "installed" && <Installed solutions={solutions} status={status} onStatus={setStatus} canBuild={member} onTemplates={() => show("templates")} />}
        {view === "templates" && <Templates templates={templates} canBuild={member} />}
        {view === "registry" && <Registry installed={solutions} canInstall={admin} onDeploy={onDeploy} onError={setError} />}
      </div>
    </>
  );
}

/** The path a first-time builder follows, in four steps. */
function GettingStarted({ canBuild }: { canBuild: boolean }) {
  const steps = [
    { icon: Icons.Solutions, title: "Start from a template", body: "Or describe your app to Design with AI in the builder." },
    { icon: Icons.Sliders, title: "Make it yours", body: "Data, workflows and the assistant, in forms checked as you type." },
    { icon: Icons.Upload, title: "Deploy", body: "One click. Upgrades later change only what changed." },
    { icon: Icons.Key, title: "Connect your app", body: "An app key and copy-ready code for your stack." },
  ];
  return (
    <Card className="overflow-hidden">
      <div className="border-b border-zinc-200 bg-gradient-to-r from-brand-50 to-transparent px-5 py-4 dark:border-zinc-800 dark:from-brand-500/10">
        <div className="text-sm font-semibold text-zinc-900 dark:text-zinc-100">Ship your first solution</div>
        <p className="mt-0.5 text-xs text-zinc-500">
          Most people go from a template to an assistant in their own app in under an hour.{" "}
          <Link href="/docs#solutions-guide" className="text-brand-600 hover:underline dark:text-brand-400">Read the step-by-step guide</Link>
        </p>
      </div>
      <ol className="grid gap-px bg-zinc-200 sm:grid-cols-2 lg:grid-cols-4 dark:bg-zinc-800">
        {steps.map((s, i) => (
          <li key={s.title} className="flex gap-3 bg-white p-4 dark:bg-zinc-900">
            <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-brand-500 text-xs font-semibold text-white">{i + 1}</span>
            <div className="min-w-0">
              <div className="flex items-center gap-1.5 text-sm font-medium text-zinc-900 dark:text-zinc-100"><s.icon className="h-3.5 w-3.5 text-zinc-400" />{s.title}</div>
              <p className="mt-0.5 text-xs text-zinc-500">{s.body}</p>
            </div>
          </li>
        ))}
      </ol>
      {!canBuild && <p className="px-5 py-3 text-xs text-zinc-500">Ask a Member or Admin of your organization to build or deploy one.</p>}
    </Card>
  );
}

function Installed({ solutions, status, onStatus, canBuild, onTemplates }: {
  solutions: SolutionSummary[] | null; status: StatusFilter; onStatus: (s: StatusFilter) => void; canBuild: boolean; onTemplates: () => void;
}) {
  const [query, setQuery] = useState("");
  const [page, setPage] = useState(0);
  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (solutions ?? [])
      .filter((s) => !q || s.title.toLowerCase().includes(q) || s.name.toLowerCase().includes(q))
      .sort((a, b) => b.updated_at.localeCompare(a.updated_at));
  }, [solutions, query]);
  const pages = Math.max(1, Math.ceil(filtered.length / PAGE));
  const current = Math.min(page, pages - 1);
  const rows = filtered.slice(current * PAGE, current * PAGE + PAGE);

  if (solutions === null) return <div className="text-sm text-zinc-500">Loading…</div>;
  if (solutions.length === 0 && status === "active") {
    return (
      <EmptyState
        icon={<Icons.Solutions className="h-5 w-5" />}
        title="No solutions installed yet"
        description="Start from a template, describe one to Design with AI, or build it with forms. Once deployed, its Integrate tab connects it to your app."
        action={canBuild ? <Button variant="primary" onClick={onTemplates}>Browse templates</Button> : undefined}
      />
    );
  }

  return (
    <Card>
      <div className="flex flex-wrap items-center gap-2 border-b border-zinc-200 px-4 py-3 dark:border-zinc-800">
        <div className="relative min-w-[12rem] flex-1">
          <Icons.Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-zinc-400" />
          <input value={query} onChange={(e) => { setQuery(e.target.value); setPage(0); }} placeholder="Search by title or name"
            className={cx(inputClass, "h-8 py-1 pl-8 text-sm")} aria-label="Search installed solutions" />
        </div>
        <select value={status} onChange={(e) => { onStatus(e.target.value as StatusFilter); setPage(0); }}
          className={cx(inputClass, "h-8 w-auto py-1 text-sm")} aria-label="Status">
          <option value="active">Active</option>
          <option value="all">Including uninstalled</option>
        </select>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full text-left text-sm">
          <thead className="border-b border-zinc-200 text-[11px] uppercase tracking-wide text-zinc-500 dark:border-zinc-800">
            <tr>
              <th className="px-4 py-2 font-medium">Solution</th>
              <th className="hidden px-4 py-2 font-medium sm:table-cell">Version</th>
              <th className="px-4 py-2 font-medium">Status</th>
              <th className="hidden px-4 py-2 font-medium md:table-cell">Updated</th>
              <th className="w-8" />
            </tr>
          </thead>
          <tbody className="divide-y divide-zinc-100 dark:divide-zinc-800">
            {rows.map((s) => (
              <tr key={s.install_id} className="group hover:bg-zinc-50 dark:hover:bg-zinc-800/40">
                <td className="px-4 py-3">
                  <Link href={`/solutions?id=${encodeURIComponent(s.install_id)}`} className="flex items-center gap-3">
                    <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-brand-50 text-brand-600 dark:bg-brand-500/10 dark:text-brand-400">
                      <Icons.Solutions className="h-4 w-4" />
                    </span>
                    <span className="min-w-0">
                      <span className="block truncate font-medium text-zinc-900 group-hover:text-brand-700 dark:text-zinc-100 dark:group-hover:text-brand-300">{s.title}</span>
                      <span className="block truncate font-mono text-[11px] text-zinc-500">{s.name}</span>
                    </span>
                  </Link>
                </td>
                <td className="hidden px-4 py-3 font-mono text-xs text-zinc-600 sm:table-cell dark:text-zinc-300">{s.version}</td>
                <td className="px-4 py-3"><Badge tone={s.status === "Active" ? "green" : "amber"}>{s.status}</Badge></td>
                <td className="hidden whitespace-nowrap px-4 py-3 text-xs text-zinc-500 md:table-cell">{ago(s.updated_at)}</td>
                <td className="px-2 py-3 text-zinc-400"><Icons.ChevronRight className="h-4 w-4" /></td>
              </tr>
            ))}
            {rows.length === 0 && <tr><td colSpan={5} className="px-4 py-8 text-center text-sm text-zinc-500">No solutions match “{query}”.</td></tr>}
          </tbody>
        </table>
      </div>
      {pages > 1 && (
        <div className="flex items-center justify-between border-t border-zinc-200 px-4 py-2 text-xs text-zinc-500 dark:border-zinc-800">
          <span>{current * PAGE + 1}–{Math.min(filtered.length, current * PAGE + PAGE)} of {filtered.length}</span>
          <div className="flex gap-2">
            <Button size="sm" disabled={current === 0} onClick={() => setPage(current - 1)}>Previous</Button>
            <Button size="sm" disabled={current >= pages - 1} onClick={() => setPage(current + 1)}>Next</Button>
          </div>
        </div>
      )}
    </Card>
  );
}

function Templates({ templates, canBuild }: { templates: SolutionTemplate[] | null; canBuild: boolean }) {
  if (templates === null) return <div className="text-sm text-zinc-500">Loading…</div>;
  return (
    <div className="space-y-5">
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
        {templates.map((t) => (
          <Card key={t.name} className="flex flex-col p-5">
            <div className="flex items-start gap-3">
              <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-brand-50 text-brand-600 dark:bg-brand-500/10 dark:text-brand-400">
                <Icons.Solutions className="h-5 w-5" />
              </span>
              <div className="min-w-0">
                <div className="font-semibold text-zinc-900 dark:text-zinc-100">{t.title}</div>
                <div className="font-mono text-[11px] text-zinc-500">{t.name} · {t.version}</div>
              </div>
            </div>
            {t.description && <p className="mt-3 flex-1 text-sm leading-relaxed text-zinc-600 dark:text-zinc-400">{t.description}</p>}
            <div className="mt-3 flex flex-wrap gap-1.5">
              <Badge>{t.workflows} workflow{t.workflows === 1 ? "" : "s"}</Badge>
              <Badge>{t.entities} record type{t.entities === 1 ? "" : "s"}</Badge>
              {t.assistant && <Badge tone="brand">Customer assistant</Badge>}
              {t.evals > 0 && <Badge tone="green">{t.evals} eval{t.evals === 1 ? "" : "s"}</Badge>}
            </div>
            {canBuild && (
              <Link href={`/solutions/build?template=${encodeURIComponent(t.name)}`}
                className="mt-4 inline-flex h-9 items-center justify-center gap-1.5 rounded-lg border border-zinc-200 text-sm font-medium text-zinc-800 hover:border-brand-300 hover:text-brand-700 dark:border-zinc-700 dark:text-zinc-200 dark:hover:border-brand-700 dark:hover:text-brand-300">
                Use this template <Icons.ChevronRight className="h-4 w-4" />
              </Link>
            )}
          </Card>
        ))}
        {templates.length === 0 && <EmptyState title="No templates on this server" description="Templates are the packages under samples/solutions, built into the API." />}
      </div>
      <Card>
        <CardHeader title="Prefer the terminal?" description="The aktor CLI does everything the builder does. Give it an Admin API key from Settings → API keys." />
        <div className="p-5">
          <CopyField multiline value={`export AKTOR_URL=${API_BASE} AKTOR_API_KEY=ak_...
npx aktor templates                              # what you can start from
npx aktor init my-app --template helpdesk && cd my-app
npx aktor deploy                                 # install, or upgrade the installed version
npx aktor app-keys helpdesk --create "Backend"   # the key your backend uses`} />
        </div>
      </Card>
    </div>
  );
}

function Registry({ installed, canInstall, onDeploy, onError }: {
  installed: SolutionSummary[] | null; canInstall: boolean; onDeploy: (packageRef?: string, installId?: string) => void; onError: (e: string) => void;
}) {
  const [packages, setPackages] = useState<RegistryPackage[] | null>(null);
  const [query, setQuery] = useState("");
  useEffect(() => {
    const t = setTimeout(() => searchRegistry(query.trim() || undefined).then(setPackages).catch((e) => onError(apiErrorMessage(e))), 250);
    return () => clearTimeout(t);
  }, [query, onError]);

  return (
    <Card>
      <CardHeader
        title="Registry"
        description="Packages published on this server: your organization's, and other organizations' public ones. Each version is signed when published and checked when installed."
        actions={<input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search packages" className={cx(inputClass, "h-8 w-56 py-1 text-xs")} aria-label="Search the registry" />}
      />
      <ul className="divide-y divide-zinc-100 dark:divide-zinc-800">
        {packages?.map((p) => {
          const current = installed?.find((s) => s.name === p.name && s.status === "Active");
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
              {canInstall && (
                current && current.version === p.version
                  ? <Badge tone="green">Installed</Badge>
                  : <Button size="sm" onClick={() => onDeploy(`${p.name}@${p.version}`, current?.install_id)}>{current ? `Upgrade from ${current.version}` : "Install"}</Button>
              )}
            </li>
          );
        })}
        {packages === null && <li className="px-5 py-4 text-sm text-zinc-500">Loading…</li>}
        {packages && packages.length === 0 && (
          <li className="px-5 py-4 text-sm text-zinc-500">{query ? "No packages match." : "Nothing published yet. Publish an installed solution from its Settings tab, or run aktor publish."}</li>
        )}
      </ul>
    </Card>
  );
}
