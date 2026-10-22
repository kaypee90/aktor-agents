"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useEffect, useState } from "react";
import { apiErrorMessage, getSolution, publishInstalledSolution, uninstallSolution, type SolutionView } from "@/lib/api";
import { atLeast, type Role } from "@/lib/platformTypes";
import { Badge, Button, Card, CardHeader, CopyField, ErrorBanner, PageHeader, Stat, Tabs, ago } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { AssistantTab } from "./tabs/AssistantTab";
import { DataTab } from "./tabs/DataTab";
import { EvalsTab } from "./tabs/EvalsTab";
import { HistoryTab } from "./tabs/HistoryTab";
import { IntegrateTab } from "./tabs/IntegrateTab";
import { UsageTab } from "./tabs/UsageTab";

const TABS = ["overview", "integrate", "data", "assistant", "usage", "evals", "history", "settings"] as const;
type Tab = (typeof TABS)[number];

/** One installed solution. The tab is kept in the URL, so a link (or a reload) opens the same view. */
export function SolutionDetail({ id, role, onUpgrade }: { id: string; role: Role; onUpgrade: (s: SolutionView) => void }) {
  const params = useSearchParams();
  const router = useRouter();
  const [solution, setSolution] = useState<SolutionView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const requested = params.get("tab");
  const tab: Tab = (TABS as readonly string[]).includes(requested ?? "") ? (requested as Tab) : "overview";
  const show = (t: Tab) => router.replace(`/solutions?id=${encodeURIComponent(id)}${t === "overview" ? "" : `&tab=${t}`}`, { scroll: false });

  useEffect(() => {
    getSolution(id).then(setSolution).catch((e) => setError(apiErrorMessage(e)));
  }, [id]);

  if (!solution) {
    return (
      <div className="mx-auto max-w-6xl space-y-4 px-6 py-6">
        <ErrorBanner error={error} />
        {!error && <div className="text-sm text-zinc-500">Loading…</div>}
      </div>
    );
  }

  const active = solution.status === "Active";
  const admin = atLeast(role, "Admin") && active;
  const member = atLeast(role, "Member") && active;
  const records = solution.entities.reduce((n, e) => n + e.records, 0);

  return (
    <>
      <PageHeader
        title={
          <span className="flex flex-wrap items-center gap-2">
            {solution.title}
            <Badge tone={active ? "green" : "amber"}>{solution.status}</Badge>
            <Badge className="font-mono">v{solution.version}</Badge>
          </span>
        }
        description={solution.description ?? undefined}
        actions={admin && (
          <>
            {tab !== "integrate" && <Button icon={<Icons.Key className="h-3.5 w-3.5" />} onClick={() => show("integrate")}>Connect your app</Button>}
            <Link href={`/solutions/build?from=${encodeURIComponent(solution.install_id)}`}
              className="inline-flex h-9 items-center gap-1.5 rounded-lg bg-brand-500 px-3.5 text-sm font-medium text-white shadow-sm hover:bg-brand-600">
              <Icons.Sliders className="h-3.5 w-3.5" /> Edit in builder
            </Link>
          </>
        )}
      >
        <nav className="mt-2 flex items-center gap-1 text-xs text-zinc-500" aria-label="Breadcrumb">
          <Link href="/solutions" className="hover:text-zinc-800 dark:hover:text-zinc-200">Solutions</Link>
          <Icons.ChevronRight className="h-3 w-3" />
          <span className="font-mono">{solution.name}</span>
        </nav>
      </PageHeader>

      <div className="mx-auto max-w-6xl space-y-5 px-6 py-6">
        <ErrorBanner error={error} onClose={() => setError(null)} />
        {!active && (
          <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:border-amber-900/60 dark:bg-amber-950/30 dark:text-amber-300">
            This solution is uninstalled. Its records can still be read and exported; nothing runs and nothing can change them.
          </div>
        )}
        <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
          <Stat label="Workflows" value={solution.workspaces.length} />
          <Stat label="Records" value={records.toLocaleString()} hint={`${solution.entities.length} record type${solution.entities.length === 1 ? "" : "s"}`} />
          <Stat label="Assistant" value={solution.assistant ? solution.assistant.name : "—"} hint={solution.assistant ? "for your customers" : "none"} />
          <Stat label="Updated" value={ago(solution.updated_at)} hint={`installed ${ago(solution.installed_at)}`} />
        </div>

        <Tabs<Tab>
          value={tab}
          onChange={show}
          tabs={[
            { id: "overview", label: "Overview" },
            { id: "integrate", label: "Integrate" },
            { id: "data", label: "Data", count: solution.entities.length },
            { id: "assistant", label: "Assistant" },
            { id: "usage", label: "Usage" },
            { id: "evals", label: "Evals", count: solution.evals.length },
            { id: "history", label: "History", count: solution.history.length },
            ...(admin ? [{ id: "settings" as const, label: "Settings" }] : []),
          ]}
        />

        {tab === "overview" && <Overview solution={solution} canIntegrate={admin} onShow={show} />}
        {tab === "integrate" && <IntegrateTab solution={solution} canManage={admin} />}
        {tab === "data" && <DataTab solution={solution} canEdit={member} />}
        {tab === "assistant" && <AssistantTab solution={solution} canIssue={admin} />}
        {tab === "usage" && <UsageTab solution={solution} />}
        {tab === "evals" && <EvalsTab solution={solution} canRun={member} />}
        {tab === "history" && <HistoryTab solution={solution} />}
        {tab === "settings" && admin && <SettingsTab solution={solution} onUpgrade={() => onUpgrade(solution)} onError={setError} />}
      </div>
    </>
  );
}

function Overview({ solution, canIntegrate, onShow }: { solution: SolutionView; canIntegrate: boolean; onShow: (t: Tab) => void }) {
  const a = solution.assistant;
  return (
    <div className="space-y-5">
      {canIntegrate && (
        <button onClick={() => onShow("integrate")}
          className="flex w-full items-center gap-3 rounded-xl border border-brand-200 bg-brand-50/60 px-4 py-3 text-left transition hover:border-brand-300 dark:border-brand-900 dark:bg-brand-500/5 dark:hover:border-brand-700">
          <Icons.Key className="h-4 w-4 shrink-0 text-brand-600 dark:text-brand-400" />
          <span className="min-w-0 flex-1 text-sm">
            <span className="font-medium text-zinc-900 dark:text-zinc-100">Connect it to your app.</span>{" "}
            <span className="text-zinc-600 dark:text-zinc-400">Create an app key, copy the code for your stack and work through the go-live checklist.</span>
          </span>
          <Icons.ChevronRight className="h-4 w-4 text-zinc-400" />
        </button>
      )}

      <div className="grid grid-cols-1 gap-5 lg:grid-cols-2">
        <Card>
          <CardHeader title="Workflows" description="Each runs its own pipeline, triggers and safety policy. Your app can start any of them by key." />
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
            {solution.workspaces.length === 0 && <li className="px-5 py-3 text-sm text-zinc-500">No workflows: this solution is only an assistant.</li>}
          </ul>
        </Card>

        <Card>
          <CardHeader title="Data model" description="The records its agents, its assistant and your backend read and write." />
          <ul className="divide-y divide-zinc-100 dark:divide-zinc-800">
            {solution.entities.map((e) => (
              <li key={e.name}>
                <button onClick={() => onShow("data")} className="flex w-full items-center gap-3 px-5 py-3 text-left hover:bg-zinc-50 dark:hover:bg-zinc-800/40">
                  <div className="min-w-0 flex-1">
                    <div className="text-sm font-medium text-zinc-900 dark:text-zinc-100">{e.name}</div>
                    <div className="truncate text-[11px] text-zinc-500">{e.description ?? `${Object.keys(e.fields).length} fields`}</div>
                  </div>
                  <span className="text-xs tabular-nums text-zinc-500">{e.records.toLocaleString()} records</span>
                </button>
              </li>
            ))}
            {solution.entities.length === 0 && <li className="px-5 py-3 text-sm text-zinc-500">No data model.</li>}
          </ul>
        </Card>

        <Card>
          <CardHeader title="Assistant" description="What your customers talk to, each reaching only their own data." />
          <div className="space-y-3 p-5 text-sm">
            {!a ? <p className="text-zinc-500">No assistant. Add one in the builder under Assistant.</p> : (
              <>
                <div className="font-medium text-zinc-900 dark:text-zinc-100">{a.name}</div>
                {a.greeting && <p className="rounded-lg bg-zinc-50 px-3 py-2 text-zinc-700 dark:bg-zinc-950 dark:text-zinc-300">“{a.greeting}”</p>}
                <div className="flex flex-wrap gap-1.5">
                  {Object.entries(a.data).map(([e, access]) => <Badge key={e} tone={access === "write" ? "amber" : "neutral"}>{e}: {access}</Badge>)}
                </div>
                <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs">
                  <dt className="text-zinc-500">Allowed sites</dt>
                  <dd className="truncate text-zinc-800 dark:text-zinc-200">{a.allowed_origins?.length ? a.allowed_origins.join(", ") : <span className="text-amber-600 dark:text-amber-400">any site</span>}</dd>
                  <dt className="text-zinc-500">Cap per customer</dt>
                  <dd className="text-zinc-800 dark:text-zinc-200">{a.monthly_cost_limit_usd_per_user != null ? `$${a.monthly_cost_limit_usd_per_user.toFixed(2)} a month` : <span className="text-amber-600 dark:text-amber-400">none</span>}</dd>
                  <dt className="text-zinc-500">Sign-in</dt>
                  <dd className="text-zinc-800 dark:text-zinc-200">{solution.own_sign_in ? "your identity provider's tokens" : "tokens your backend issues"}</dd>
                </dl>
              </>
            )}
          </div>
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
                {solution.webhooks.map((h) => <div key={h.url} className="break-all font-mono text-xs text-zinc-700 dark:text-zinc-300">{h.events.join(", ")} → {h.url}</div>)}
                {solution.webhook_secret && <CopyField label="Signing secret (X-Aktor-Signature: sha256=HMAC of the body)" value={solution.webhook_secret} />}
              </div>
            )}
          </div>
        </Card>
      </div>
    </div>
  );
}

/** Rare and risky actions, kept off the header: upgrading from a folder, publishing, uninstalling. */
function SettingsTab({ solution, onUpgrade, onError }: { solution: SolutionView; onUpgrade: () => void; onError: (e: string) => void }) {
  const router = useRouter();
  const [visibility, setVisibility] = useState<"organization" | "public">("organization");
  const [published, setPublished] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function publish() {
    setBusy(true);
    try {
      const p = await publishInstalledSolution(solution.install_id, visibility);
      setPublished(`Published ${p.package} (${p.visibility === "public" ? "public" : "your organization only"}).`);
    } catch (e) {
      onError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function uninstall(purge: boolean) {
    const what = purge
      ? `Uninstall ${solution.title} and permanently delete all of its records? This can't be undone.`
      : `Uninstall ${solution.title}? Its workflows are archived and its records are kept, read-only.`;
    if (!window.confirm(what)) return;
    try {
      await uninstallSolution(solution.install_id, purge);
      router.push("/solutions");
    } catch (e) {
      onError(apiErrorMessage(e));
    }
  }

  return (
    <div className="grid grid-cols-1 gap-5 lg:grid-cols-2">
      <Card>
        <CardHeader title="Change it" description="The builder opens this version as a draft; deploying it upgrades this install. Or upgrade from a package folder made with the CLI." />
        <div className="flex flex-wrap gap-2 p-5">
          <Link href={`/solutions/build?from=${encodeURIComponent(solution.install_id)}`}
            className="inline-flex h-9 items-center gap-1.5 rounded-lg bg-brand-500 px-3.5 text-sm font-medium text-white shadow-sm hover:bg-brand-600">
            Edit in builder
          </Link>
          <Button icon={<Icons.Upload className="h-3.5 w-3.5" />} onClick={onUpgrade}>Upgrade from a folder</Button>
        </div>
      </Card>

      <Card>
        <CardHeader title="Publish to the registry" description="Share this exact version as a signed package that can be installed by name. A published version never changes." />
        <div className="space-y-3 p-5 text-sm">
          {(["organization", "public"] as const).map((v) => (
            <label key={v} className="flex cursor-pointer items-start gap-2">
              <input type="radio" name="visibility" className="mt-1" checked={visibility === v} onChange={() => setVisibility(v)} />
              <span>
                <span className="font-medium text-zinc-900 dark:text-zinc-100">{v === "organization" ? "Your organization" : "Public"}</span>
                <span className="block text-xs text-zinc-500">{v === "organization" ? "Only your organization can find and install it." : "Every organization on this server can install it."}</span>
              </span>
            </label>
          ))}
          <div className="flex items-center gap-3">
            <Button variant="primary" disabled={busy} onClick={publish}>{busy ? "Publishing…" : `Publish ${solution.version}`}</Button>
            {published && <span className="text-xs text-emerald-600 dark:text-emerald-400">{published}</span>}
          </div>
        </div>
      </Card>

      <section className="rounded-xl border border-rose-200 bg-white shadow-sm lg:col-span-2 dark:border-rose-900/60 dark:bg-zinc-900/60">
        <div className="border-b border-rose-100 px-5 py-4 dark:border-rose-900/40">
          <h3 className="text-sm font-semibold text-rose-700 dark:text-rose-400">Danger zone</h3>
          <p className="mt-0.5 text-xs text-zinc-500 dark:text-zinc-400">Uninstalling archives its workflows and stops its assistant. App keys and end-user tokens stop working.</p>
        </div>
        <div className="flex flex-wrap items-center gap-3 p-5">
          <Button variant="danger" onClick={() => uninstall(false)}>Uninstall, keep records</Button>
          <Button variant="danger" onClick={() => uninstall(true)}>Uninstall and delete records</Button>
        </div>
      </section>
    </div>
  );
}
