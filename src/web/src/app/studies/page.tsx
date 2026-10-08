"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useCallback, useEffect, useState } from "react";
import {
  apiErrorMessage,
  createStudy,
  deleteStudy,
  getLlmSettings,
  getStudy,
  listStudies,
  listStudyEvidence,
  modelChoices,
  startStudyRun,
  studyNotebookUrl,
  updateStudy,
  type ModelChoice,
  type StudyDetail,
  type StudyEvidence,
  type StudySummary,
} from "@/lib/api";
import { useAuth } from "@/components/platform/AuthProvider";
import { atLeast, type Role } from "@/lib/platformTypes";
import { Badge, Button, Card, CardHeader, EmptyState, ErrorBanner, Field, Modal, PageHeader, StatusBadge, Tabs, ago, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { StudySources } from "@/components/studies/StudySources";
import { StudyExperiments, StudyModels, StudyReport } from "@/components/studies/StudyFindings";
import { EvidenceChip, EvidenceProvider, KIND_TONE, ROLE_LABEL } from "@/components/studies/StudyEvidence";

/**
 * Studies (docs/studies.md): research projects with their own data, documents and connections.
 * Agent teams analyze, model and simulate with real code, review each other, and write a report
 * whose every finding cites its evidence.
 */
export default function StudiesPage() {
  return <Suspense><Studies /></Suspense>;
}

function Studies() {
  const params = useSearchParams();
  const id = params.get("id");
  return id ? <StudyView id={id} /> : <StudyList />;
}

function StudyList() {
  const router = useRouter();
  const { me } = useAuth();
  const canEdit = atLeast((me?.role ?? "Viewer") as Role, "Member");
  const [studies, setStudies] = useState<StudySummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState<{ name: string; question: string } | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    listStudies().then(setStudies).catch((e) => setError(apiErrorMessage(e)));
  }, []);

  async function create() {
    if (!creating?.name.trim()) return;
    setBusy(true);
    try {
      const study = await createStudy(creating.name.trim(), creating.question.trim());
      router.push(`/studies?id=${encodeURIComponent(study.study_id)}&tab=sources`);
    } catch (e) {
      setError(apiErrorMessage(e));
      setBusy(false);
    }
  }

  return (
    <div>
      <PageHeader
        title="Studies"
        description="Research a question with your own data. Give a study its datasets, documents and connections; agent teams explore the data, fit models, run simulated experiments, review each other's work and report findings that cite their evidence."
        actions={canEdit && <Button variant="primary" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => setCreating({ name: "", question: "" })}>New study</Button>}
      />
      <div className="mx-auto max-w-6xl space-y-4 px-6 py-6">
        <ErrorBanner error={error} onClose={() => setError(null)} />
        {studies === null ? (
          <div className="grid gap-3 md:grid-cols-2">{[0, 1, 2, 3].map((i) => <div key={i} className="h-28 animate-pulse rounded-xl bg-zinc-100 dark:bg-zinc-900" />)}</div>
        ) : studies.length === 0 ? (
          <EmptyState icon={<Icons.Simulation className="h-5 w-5" />} title="No studies yet"
            description="Start with a question, like “What happens to renewals if rents rise 8%?”, then add the data that can answer it."
            action={canEdit && <Button variant="primary" onClick={() => setCreating({ name: "", question: "" })}>New study</Button>} />
        ) : (
          <div className="grid gap-3 md:grid-cols-2">
            {studies.map((s) => (
              <Link key={s.study_id} href={`/studies?id=${encodeURIComponent(s.study_id)}`}
                className="block rounded-xl border border-zinc-200 bg-white p-4 shadow-sm transition hover:border-brand-300 dark:border-zinc-800 dark:bg-zinc-900/60 dark:hover:border-brand-800">
                <div className="flex items-start justify-between gap-3">
                  <span className="font-semibold text-zinc-900 dark:text-zinc-100">{s.name}</span>
                  <StatusBadge status={s.status} />
                </div>
                <p className="mt-1 line-clamp-2 text-sm text-zinc-600 dark:text-zinc-400">{s.question || "No question yet."}</p>
                <div className="mt-3 text-xs text-zinc-500">{s.datasets} dataset{s.datasets === 1 ? "" : "s"} · updated {ago(s.updated_at)}</div>
              </Link>
            ))}
          </div>
        )}
      </div>

      <Modal open={creating !== null} onClose={() => setCreating(null)} title="New study"
        description="A study has its own datasets, documents and connections; nothing is shared with other studies."
        footer={<>
          <Button onClick={() => setCreating(null)}>Cancel</Button>
          <Button variant="primary" disabled={busy || !creating?.name.trim()} onClick={create}>{busy ? "Creating…" : "Create"}</Button>
        </>}>
        {creating && (
          <div className="space-y-4">
            <Field label="Name"><input className={inputClass} autoFocus value={creating.name} maxLength={120} placeholder="Lease renewals" onChange={(e) => setCreating({ ...creating, name: e.target.value })} /></Field>
            <Field label="Question" hint="What the study should find out. You can refine it later.">
              <textarea className={inputClass} rows={3} value={creating.question} placeholder="What happens to renewals if rents rise 8% next year, and which tenants are most at risk?"
                onChange={(e) => setCreating({ ...creating, question: e.target.value })} />
            </Field>
          </div>
        )}
      </Modal>
    </div>
  );
}

type StudyTab = "overview" | "sources" | "models" | "experiments" | "evidence" | "report";

function StudyView({ id }: { id: string }) {
  const router = useRouter();
  const params = useSearchParams();
  const { me } = useAuth();
  const role = (me?.role ?? "Viewer") as Role;
  const canEdit = atLeast(role, "Member");
  const tab = (params.get("tab") as StudyTab | null) ?? "overview";
  const [study, setStudy] = useState<StudyDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [running, setRunning] = useState<{ instructions: string; model: string } | null>(null);
  const [models, setModels] = useState<ModelChoice[]>([]);
  const [busy, setBusy] = useState(false);
  const [editing, setEditing] = useState<{ name: string; question: string } | null>(null);

  const load = useCallback(() => {
    getStudy(id).then(setStudy).catch((e) => setError(apiErrorMessage(e)));
  }, [id]);

  useEffect(() => { load(); }, [load]);
  useEffect(() => {
    getLlmSettings().then((v) => setModels(modelChoices(v))).catch(() => { /* the default model is used */ });
  }, []);
  // While a run is in progress, its models, experiments and report fill in.
  useEffect(() => {
    if (study?.status !== "Running") return;
    const timer = setInterval(load, 4000);
    return () => clearInterval(timer);
  }, [study?.status, load]);

  function setTab(t: StudyTab) {
    router.replace(`/studies?id=${encodeURIComponent(id)}${t === "overview" ? "" : `&tab=${t}`}`, { scroll: false });
  }

  async function run() {
    if (!running) return;
    setBusy(true);
    try {
      await startStudyRun(id, running.instructions.trim(), running.model || null);
      setRunning(null);
      load();
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function saveEdit() {
    if (!editing) return;
    try {
      setStudy(await updateStudy(id, { name: editing.name, question: editing.question }));
      setEditing(null);
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  async function remove() {
    if (!study || !confirm(`Delete the study "${study.name}" with its datasets, documents, connections, evidence and reports? This can't be undone.`)) return;
    try {
      await deleteStudy(id);
      router.push("/studies");
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  if (!study) {
    return (
      <div className="mx-auto max-w-6xl px-6 py-6">
        {error ? <ErrorBanner error={error} /> : <div className="h-40 animate-pulse rounded-xl bg-zinc-100 dark:bg-zinc-900" />}
      </div>
    );
  }

  const lastRun = study.runs[0];
  const unassigned = study.data_use_plan.filter((p) => p.role === "unassigned").length;
  return (
    <EvidenceProvider studyId={id}>
      <PageHeader
        title={<span className="flex items-center gap-2"><Link href="/studies" className="text-zinc-400 hover:text-zinc-700 dark:hover:text-zinc-200">Studies</Link><span className="text-zinc-300">/</span>{study.name}</span>}
        description={study.question || "No question yet."}
        actions={<>
          <StatusBadge status={study.status} />
          {canEdit && <Button size="sm" onClick={() => setEditing({ name: study.name, question: study.question })}>Edit</Button>}
          <a href={studyNotebookUrl(id)}><Button size="sm" icon={<Icons.Download className="h-3.5 w-3.5" />}>Notebook</Button></a>
          {atLeast(role, "Admin") && <Button size="sm" variant="ghost" onClick={remove}>Delete</Button>}
          {canEdit && (
            <Button variant="primary" icon={<Icons.Play className="h-3.5 w-3.5" />} disabled={study.status === "Running"}
              onClick={() => setRunning({ instructions: "", model: "" })}>
              {study.status === "Running" ? "Running…" : lastRun ? "Run again" : "Run study"}
            </Button>
          )}
        </>}
      >
        <Tabs className="mt-4 border-0" value={tab} onChange={setTab} tabs={[
          { id: "overview", label: "Overview" },
          { id: "sources", label: "Sources", count: study.datasets.length + study.documents.files.length + study.connections.length },
          { id: "models", label: "Models", count: study.models.length },
          { id: "experiments", label: "Experiments", count: study.simulations.length },
          { id: "evidence", label: "Evidence", count: study.evidence_count },
          { id: "report", label: "Report" },
        ]} />
      </PageHeader>

      <div className="mx-auto max-w-6xl space-y-5 px-6 py-6">
        <ErrorBanner error={error} onClose={() => setError(null)} />
        {tab === "overview" && (
          <>
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
              <Stat label="Sources" value={study.data_use_plan.length} hint={unassigned ? `${unassigned} without a role yet` : "every source has a role"} />
              <Stat label="Models" value={study.models.length} hint={`${study.models.filter((m) => m.status === "accepted").length} accepted · ${study.models.filter((m) => m.holdout).length} holdout-scored`} />
              <Stat label="Experiments" value={study.simulations.length} hint={`${study.simulations.reduce((n, s) => n + s.decisions, 0)} simulated decisions`} />
              <Stat label="Evidence" value={study.evidence_count} hint="queries, fits, analyses, passages, API calls" />
            </div>

            {study.report && (
              <Card className="p-5">
                <div className="mb-1 text-xs font-medium uppercase tracking-wide text-zinc-500">Latest answer</div>
                <p className="line-clamp-4 text-sm text-zinc-800 dark:text-zinc-200">{study.report.content.summary}</p>
                <button onClick={() => setTab("report")} className="mt-2 text-xs font-medium text-brand-600 hover:underline dark:text-brand-400">Read the report →</button>
              </Card>
            )}

            <Card>
              <CardHeader title="Runs" description="Each run starts an agent team on the question; open one to watch its agents live." />
              {study.runs.length === 0 ? (
                <p className="px-5 py-4 text-sm text-zinc-500">
                  {study.data_use_plan.length === 0 ? <>Add a dataset, document or connection under <button className="text-brand-600 hover:underline dark:text-brand-400" onClick={() => setTab("sources")}>Sources</button>, then run the study.</> : "Not run yet."}
                </p>
              ) : (
                <ul className="divide-y divide-zinc-100 dark:divide-zinc-800">
                  {study.runs.map((r) => (
                    <li key={r.task_id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-sm">
                      <StatusBadge status={r.status} />
                      <span className="min-w-0 flex-1 truncate text-zinc-600 dark:text-zinc-400" title={r.summary ?? r.instructions}>{r.summary ?? r.instructions}</span>
                      {r.has_report && <Badge tone="green">report</Badge>}
                      <span className="text-xs text-zinc-500">{ago(r.created_at)}</span>
                      <Link href={`/?task=${encodeURIComponent(r.task_id)}`} className="text-xs font-medium text-brand-600 hover:underline dark:text-brand-400">Agents →</Link>
                    </li>
                  ))}
                </ul>
              )}
            </Card>

            <Card>
              <CardHeader title="Data-use plan" description="What each source is for, set by the agents; a run's report is refused until every source has a role." />
              {study.data_use_plan.length === 0 ? <p className="px-5 py-4 text-sm text-zinc-500">No sources yet.</p> : (
                <table className="w-full text-sm">
                  <tbody className="divide-y divide-zinc-100 dark:divide-zinc-800">
                    {study.data_use_plan.map((p) => (
                      <tr key={p.source}>
                        <td className="px-5 py-2 font-mono text-xs">{p.source}</td>
                        <td className="px-3 py-2"><Badge tone={p.role === "unassigned" ? "amber" : p.role === "not_relevant" ? "neutral" : "blue"}>{ROLE_LABEL[p.role] ?? p.role}</Badge></td>
                        <td className="px-3 py-2 text-xs text-zinc-500">{p.reason}</td>
                        <td className="px-5 py-2 text-right text-xs tabular-nums text-zinc-500">{p.evidence_count} evidence</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </Card>
          </>
        )}
        {tab === "sources" && <StudySources study={study} canEdit={canEdit} onChanged={load} />}
        {tab === "models" && <StudyModels study={study} />}
        {tab === "experiments" && <StudyExperiments study={study} />}
        {tab === "evidence" && <EvidenceList studyId={id} refreshKey={study.evidence_count} />}
        {tab === "report" && <StudyReport study={study} />}
      </div>

      <Modal open={running !== null} onClose={() => setRunning(null)} title={lastRun ? "Run the study again" : "Run the study"}
        description="An agent team works on the question with this study's sources. It reads the data, fits and reviews models, may run experiments, and finishes with a report."
        footer={<>
          <Button onClick={() => setRunning(null)}>Cancel</Button>
          <Button variant="primary" disabled={busy} onClick={run}>{busy ? "Starting…" : "Start"}</Button>
        </>}>
        {running && (
          <div className="space-y-4">
            <Field label="Instructions for this run (optional)" hint="E.g. “Focus on tenants with more than 3 years of tenure” or “Test a 5% and an 8% increase.”">
              <textarea className={inputClass} rows={3} value={running.instructions} onChange={(e) => setRunning({ ...running, instructions: e.target.value })} />
            </Field>
            {models.length > 1 && (
              <Field label="Model">
                <select className={inputClass} value={running.model} onChange={(e) => setRunning({ ...running, model: e.target.value })}>
                  <option value="">Organization default</option>
                  {models.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}
                </select>
              </Field>
            )}
          </div>
        )}
      </Modal>

      <Modal open={editing !== null} onClose={() => setEditing(null)} title="Edit study"
        footer={<><Button onClick={() => setEditing(null)}>Cancel</Button><Button variant="primary" onClick={saveEdit}>Save</Button></>}>
        {editing && (
          <div className="space-y-4">
            <Field label="Name"><input className={inputClass} value={editing.name} maxLength={120} onChange={(e) => setEditing({ ...editing, name: e.target.value })} /></Field>
            <Field label="Question"><textarea className={inputClass} rows={4} value={editing.question} onChange={(e) => setEditing({ ...editing, question: e.target.value })} /></Field>
          </div>
        )}
      </Modal>
    </EvidenceProvider>
  );
}

function Stat({ label, value, hint }: { label: string; value: React.ReactNode; hint: string }) {
  return (
    <Card className="p-4">
      <div className="text-[11px] font-medium uppercase tracking-wide text-zinc-500">{label}</div>
      <div className="mt-1 text-2xl font-semibold tabular-nums text-zinc-900 dark:text-zinc-50">{value}</div>
      <div className="mt-0.5 text-xs text-zinc-500">{hint}</div>
    </Card>
  );
}

/** Everything the runtime ran or fetched for the study, newest first. */
function EvidenceList({ studyId, refreshKey }: { studyId: string; refreshKey: number }) {
  const [items, setItems] = useState<StudyEvidence[] | null>(null);
  const [kind, setKind] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    listStudyEvidence(studyId).then(setItems).catch((e) => setError(apiErrorMessage(e)));
  }, [studyId, refreshKey]);

  if (error) return <ErrorBanner error={error} onClose={() => setError(null)} />;
  if (items === null) return <div className="h-40 animate-pulse rounded-xl bg-zinc-100 dark:bg-zinc-900" />;
  if (items.length === 0) {
    return <EmptyState icon={<Icons.Book className="h-5 w-5" />} title="No evidence yet"
      description="Every query, model fit, analysis, holdout score, document passage and API response the agents use is recorded here with an id their findings cite." />;
  }

  const kinds = Array.from(new Set(items.map((i) => i.kind)));
  const shown = kind ? items.filter((i) => i.kind === kind) : items;
  return (
    <Card>
      <CardHeader title="Evidence" description="Click an id to see what was run and what came back."
        actions={<select className={`${inputClass} w-auto py-1 text-xs`} value={kind} onChange={(e) => setKind(e.target.value)}>
          <option value="">Every kind</option>
          {kinds.map((k) => <option key={k} value={k}>{k}</option>)}
        </select>} />
      <ul className="divide-y divide-zinc-100 dark:divide-zinc-800">
        {shown.map((e) => (
          <li key={e.evidence_id} className="flex flex-wrap items-center gap-3 px-5 py-2.5 text-sm">
            <EvidenceChip id={e.evidence_id} />
            <Badge tone={KIND_TONE[e.kind] ?? "neutral"}>{e.kind}</Badge>
            <span className="min-w-0 flex-1 truncate text-zinc-700 dark:text-zinc-300" title={e.summary}>{e.summary}</span>
            <span className="font-mono text-[11px] text-zinc-400">{e.agent_id}</span>
            <span className="text-xs text-zinc-500">{ago(e.created_at)}</span>
          </li>
        ))}
      </ul>
    </Card>
  );
}
