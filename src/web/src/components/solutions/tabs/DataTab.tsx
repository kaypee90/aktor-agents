"use client";

import { useEffect, useState } from "react";
import {
  apiErrorMessage,
  createSolutionRecord,
  deleteSolutionRecord,
  listSolutionRecords,
  updateSolutionRecord,
  type SolutionFieldDef,
  type SolutionRecord,
  type SolutionView,
} from "@/lib/api";
import { Badge, Button, Card, CardHeader, EmptyState, ErrorBanner, Field, Modal, ago, cx, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

/** The Data tab: browse, filter and edit a solution's records, one entity at a time. */
export function DataTab({ solution, canEdit }: { solution: SolutionView; canEdit: boolean }) {
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

  if (!def) return <EmptyState title="No data model" description="This solution has no record types. Add them in the builder under Data." />;
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
