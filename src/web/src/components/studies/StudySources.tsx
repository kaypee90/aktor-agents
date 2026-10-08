"use client";

import Link from "next/link";
import { useState } from "react";
import {
  addKnowledgeFiles,
  addStudyDatasets,
  apiErrorMessage,
  deleteStudyDataset,
  studyDatasetUrl,
  updateStudyDataset,
  type StudyColumn,
  type StudyDataset,
  type StudyDetail,
} from "@/lib/api";
import { AttachButton, DropZone } from "@/components/files/Attachments";
import { IntegrationsPanel } from "@/components/workspace/IntegrationsPanel";
import { Badge, Button, Card, CardHeader, ErrorBanner, Field, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { num } from "./StudyEvidence";

const size = (bytes: number) => (bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`);

/** What the study draws on: datasets, documents and connections, all its own. */
export function StudySources({ study, canEdit, onChanged }: { study: StudyDetail; canEdit: boolean; onChanged: () => void }) {
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notes, setNotes] = useState<string[]>([]);

  async function addDatasets(files: File[]) {
    if (files.length === 0) return;
    setBusy("datasets");
    setError(null);
    try {
      const results = await addStudyDatasets(study.study_id, files);
      setNotes(results.filter((r) => r.error).map((r) => `${r.file_name}: ${r.error}`));
      onChanged();
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  async function addDocuments(files: File[]) {
    if (files.length === 0) return;
    setBusy("documents");
    setError(null);
    try {
      const results = await addKnowledgeFiles(files, study.workspace_id);
      setNotes(results.filter((r) => r.error).map((r) => `${r.file_name}: ${r.error}`));
      onChanged();
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="space-y-5">
      <ErrorBanner error={error} onClose={() => setError(null)} />
      {notes.length > 0 && (
        <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-900 dark:bg-amber-950/40 dark:text-amber-200">
          {notes.map((n) => <div key={n}>{n}</div>)}
        </div>
      )}

      <Card>
        <CardHeader title="Datasets"
          description="Tables the agents query and model: CSV, Excel, Parquet or JSON. Each is profiled, and a share is sealed as holdout that no agent sees until a reviewed model is scored on it."
          actions={canEdit && (
            <span className="inline-flex items-center gap-1 rounded-lg border border-zinc-200 bg-white text-sm font-medium dark:border-zinc-700 dark:bg-zinc-900">
              <AttachButton onFiles={addDatasets} />
              <span className="pr-3">{busy === "datasets" ? "Profiling…" : "Add dataset"}</span>
            </span>
          )} />
        {study.datasets.length === 0 ? (
          canEdit ? (
            <DropZone onFiles={addDatasets} label="Drop data files to add them">
              <div className="m-5 flex flex-col items-center gap-2 rounded-xl border-2 border-dashed border-zinc-200 px-6 py-10 text-center text-sm text-zinc-500 dark:border-zinc-700">
                <Icons.Upload className="h-6 w-6 text-zinc-400" />
                {busy === "datasets" ? "Profiling…" : "Drop CSV, Excel, Parquet or JSON files here."}
              </div>
            </DropZone>
          ) : <p className="p-5 text-sm text-zinc-500">No datasets yet.</p>
        ) : (
          <div className="divide-y divide-zinc-100 dark:divide-zinc-800">
            {study.datasets.map((d) => <DatasetRow key={d.dataset_id} study={study} dataset={d} canEdit={canEdit} onChanged={onChanged} onError={setError} />)}
          </div>
        )}
      </Card>

      {study.simulated_datasets.length > 0 && (
        <Card>
          <CardHeader title="Simulated datasets" description="Decisions recorded by the study's experiments. Agents query and model them like any dataset; findings from them are marked as simulated." />
          <div className="divide-y divide-zinc-100 dark:divide-zinc-800">
            {study.simulated_datasets.map((d) => <DatasetRow key={d.dataset_id} study={study} dataset={d} canEdit={false} onChanged={onChanged} onError={setError} />)}
          </div>
        </Card>
      )}

      <Card>
        <CardHeader title="Documents"
          description="Reports, papers and policies the agents search; every passage they use is cited."
          actions={
            <div className="flex items-center gap-2">
              <Link href={`/knowledge?workspace=${encodeURIComponent(study.workspace_id)}`} className="text-xs text-brand-600 hover:underline dark:text-brand-400">Manage →</Link>
              {canEdit && (
                <span className="inline-flex items-center gap-1 rounded-lg border border-zinc-200 bg-white text-sm font-medium dark:border-zinc-700 dark:bg-zinc-900">
                  <AttachButton onFiles={addDocuments} />
                  <span className="pr-3">{busy === "documents" ? "Reading…" : "Add documents"}</span>
                </span>
              )}
            </div>
          } />
        {study.documents.files.length === 0 && study.documents.facts === 0 ? (
          <p className="p-5 text-sm text-zinc-500">No documents yet.</p>
        ) : (
          <ul className="flex flex-wrap gap-2 p-5">
            {study.documents.files.map((f) => (
              <li key={f} className="inline-flex items-center gap-1.5 rounded-lg border border-zinc-200 px-2.5 py-1 text-xs dark:border-zinc-800">
                <Icons.File className="h-3.5 w-3.5 text-zinc-400" />{f}
              </li>
            ))}
            {study.documents.facts > 0 && <li className="rounded-lg border border-zinc-200 px-2.5 py-1 text-xs text-zinc-600 dark:border-zinc-800">{study.documents.facts} typed fact{study.documents.facts === 1 ? "" : "s"}</li>}
          </ul>
        )}
      </Card>

      <Card>
        <CardHeader title="Connections"
          description="MCP servers and APIs for this study only, with their secrets in the vault. Every response the agents use is kept as evidence. Tools that would change something need your approval." />
        <div className="p-2">
          <IntegrationsPanel workspaceId={study.workspace_id} onChanged={onChanged} />
        </div>
      </Card>
    </div>
  );
}

function DatasetRow({ study, dataset: d, canEdit, onChanged, onError }: {
  study: StudyDetail; dataset: StudyDataset; canEdit: boolean; onChanged: () => void; onError: (e: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const [dictionary, setDictionary] = useState<Record<string, string>>(d.dictionary);
  const [timeColumn, setTimeColumn] = useState(d.time_column ?? "");
  const [holdout, setHoldout] = useState(String(Math.round(d.holdout_fraction * 100)));
  const [saving, setSaving] = useState(false);
  const columns: StudyColumn[] = d.profile.columns ?? [];
  const changed = JSON.stringify(dictionary) !== JSON.stringify(d.dictionary) || timeColumn !== (d.time_column ?? "")
    || Number(holdout) !== Math.round(d.holdout_fraction * 100);

  async function save() {
    setSaving(true);
    try {
      await updateStudyDataset(study.study_id, d.dataset_id, { dictionary, time_column: timeColumn, holdout_fraction: Number(holdout) / 100 });
      onChanged();
    } catch (e) {
      onError(apiErrorMessage(e));
    } finally {
      setSaving(false);
    }
  }

  async function remove() {
    if (!confirm(`Remove the dataset "${d.name}" (every version)? Evidence that used it stays.`)) return;
    try {
      await deleteStudyDataset(study.study_id, d.dataset_id);
      onChanged();
    } catch (e) {
      onError(apiErrorMessage(e));
    }
  }

  return (
    <div className="px-5 py-3">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
        <button onClick={() => setOpen(!open)} className="flex min-w-0 flex-1 items-center gap-2 text-left">
          <Icons.ChevronRight className={`h-4 w-4 shrink-0 text-zinc-400 transition-transform ${open ? "rotate-90" : ""}`} />
          <span className="font-mono text-sm font-semibold text-zinc-900 dark:text-zinc-100">{d.name}</span>
          <span className="truncate text-xs text-zinc-500">{d.file_name} · v{d.version} · {size(d.size_bytes)}</span>
        </button>
        <span className="text-xs tabular-nums text-zinc-600 dark:text-zinc-400">
          {d.rows.toLocaleString()} rows · {columns.length} columns
          {d.holdout_rows > 0 && <> · <span title="Rows no agent sees until a reviewed model is scored on them">{d.holdout_rows.toLocaleString()} sealed</span></>}
        </span>
        <div className="flex items-center gap-1">
          <a href={studyDatasetUrl(study.study_id, d.dataset_id)} title="Download the training rows (Parquet)"
            className="rounded-md p-1.5 text-zinc-400 hover:bg-zinc-100 hover:text-zinc-700 dark:hover:bg-zinc-800"><Icons.Download className="h-3.5 w-3.5" /></a>
          {canEdit && <button onClick={remove} title="Remove" className="rounded-md p-1.5 text-zinc-400 hover:bg-rose-50 hover:text-rose-600 dark:hover:bg-rose-950/40"><Icons.Trash className="h-3.5 w-3.5" /></button>}
        </div>
      </div>

      {open && (
        <div className="mt-3 space-y-3 pl-6">
          {(d.profile.notes ?? []).map((n) => <p key={n} className="text-xs text-amber-700 dark:text-amber-400">{n}</p>)}
          <div className="overflow-x-auto rounded-lg border border-zinc-200 dark:border-zinc-800">
            <table className="w-full min-w-[640px] text-xs">
              <thead className="bg-zinc-50 text-left text-[10px] uppercase tracking-wide text-zinc-500 dark:bg-zinc-900">
                <tr>
                  <th className="px-3 py-2 font-medium">Column</th>
                  <th className="px-3 py-2 font-medium">Kind</th>
                  <th className="px-3 py-2 text-right font-medium">Missing</th>
                  <th className="px-3 py-2 font-medium">Range / values</th>
                  <th className="w-[38%] px-3 py-2 font-medium">Meaning (data dictionary)</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-zinc-100 dark:divide-zinc-800">
                {columns.map((c) => (
                  <tr key={c.name}>
                    <td className="px-3 py-1.5">
                      <div className="font-mono font-medium text-zinc-900 dark:text-zinc-100">{c.name}</div>
                      {c.original !== c.name && <div className="text-[10px] text-zinc-400">{c.original}</div>}
                    </td>
                    <td className="px-3 py-1.5"><Badge>{c.kind}</Badge></td>
                    <td className="px-3 py-1.5 text-right tabular-nums">{c.missing ? c.missing.toLocaleString() : "—"}</td>
                    <td className="px-3 py-1.5 text-zinc-600 dark:text-zinc-400">
                      {c.kind === "numeric" ? <>{num(c.min)} – {num(c.max)} <span className="text-zinc-400">(mean {num(c.mean)})</span></>
                        : c.kind === "datetime" ? <>{String(c.min ?? "").slice(0, 10)} – {String(c.max ?? "").slice(0, 10)}</>
                        : c.top_values?.slice(0, 4).map((t) => t.value).join(", ") ?? `${c.distinct} distinct`}
                    </td>
                    <td className="px-3 py-1">
                      <input className={`${inputClass} py-1 text-xs`} value={dictionary[c.name] ?? ""} disabled={!canEdit || d.kind === "simulated"}
                        placeholder={d.kind === "simulated" ? "" : "What this column means, e.g. 1 = renewed"}
                        onChange={(e) => setDictionary({ ...dictionary, [c.name]: e.target.value })} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {canEdit && d.kind !== "simulated" && (
            <div className="flex flex-wrap items-end gap-3">
              <Field label="Time column" hint="Orders the rows; the holdout becomes the most recent ones." className="w-56">
                <select className={`${inputClass} py-1.5 text-xs`} value={timeColumn} onChange={(e) => setTimeColumn(e.target.value)}>
                  <option value="">None (random holdout)</option>
                  {columns.filter((c) => c.kind === "datetime" || c.kind === "numeric").map((c) => <option key={c.name} value={c.name}>{c.name}</option>)}
                </select>
              </Field>
              <Field label="Holdout %" hint="0–50; changing it re-splits as a new version." className="w-36">
                <input type="number" min={0} max={50} className={`${inputClass} py-1.5 text-xs`} value={holdout} onChange={(e) => setHoldout(e.target.value)} />
              </Field>
              <Button size="sm" variant="primary" disabled={!changed || saving} onClick={save}>{saving ? "Saving…" : "Save"}</Button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
