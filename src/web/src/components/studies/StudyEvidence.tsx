"use client";

import { createContext, useContext, useEffect, useState } from "react";
import { apiErrorMessage, getStudyEvidence, studyFileUrl, type StudyEvidence } from "@/lib/api";
import { Badge, ErrorBanner, Modal, ago, cx, type Tone } from "@/components/ui";

/** A number as people read statistics: 0.0123 → "0.0123", 1234.5 → "1,234.5". */
export function num(v: unknown, digits = 3): string {
  if (typeof v !== "number" || !Number.isFinite(v)) return "—";
  if (v !== 0 && Math.abs(v) < 0.001) return v.toExponential(2);
  return v.toLocaleString(undefined, { maximumFractionDigits: Math.abs(v) >= 100 ? 1 : digits });
}

export const pct = (v: number | null | undefined, digits = 0) => (typeof v === "number" ? `${(v * 100).toFixed(digits)}%` : "—");

export const KIND_TONE: Record<string, Tone> = {
  query: "blue", model: "brand", analysis: "blue", holdout: "green", passage: "neutral", connection: "neutral", simulation: "amber",
};

export const ROLE_LABEL: Record<string, string> = {
  model_input: "Model input", calibration: "Calibration", population: "Population", scenario: "Scenario facts",
  validation: "Validation", not_relevant: "Not relevant", unassigned: "No role yet",
};

/** Opens a piece of evidence from anywhere in the study view. */
const EvidenceContext = createContext<(id: string) => void>(() => {});

export function EvidenceProvider({ studyId, children }: { studyId: string; children: React.ReactNode }) {
  const [open, setOpen] = useState<string | null>(null);
  return (
    <EvidenceContext.Provider value={setOpen}>
      {children}
      {open && <EvidenceModal studyId={studyId} evidenceId={open} onClose={() => setOpen(null)} />}
    </EvidenceContext.Provider>
  );
}

/** A citation: click to see what was run and what came back. */
export function EvidenceChip({ id }: { id: string }) {
  const open = useContext(EvidenceContext);
  return (
    <button onClick={() => open(id)} title="Show this evidence"
      className="inline-flex items-center rounded-md bg-zinc-100 px-1.5 py-0.5 font-mono text-[11px] text-zinc-700 ring-1 ring-inset ring-zinc-200 hover:bg-brand-50 hover:text-brand-700 hover:ring-brand-200 dark:bg-zinc-800 dark:text-zinc-300 dark:ring-zinc-700 dark:hover:bg-brand-950/50 dark:hover:text-brand-300">
      {id}
    </button>
  );
}

function EvidenceModal({ studyId, evidenceId, onClose }: { studyId: string; evidenceId: string; onClose: () => void }) {
  const [evidence, setEvidence] = useState<{ id: string; value: StudyEvidence } | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    getStudyEvidence(studyId, evidenceId)
      .then((value) => { if (!cancelled) setEvidence({ id: evidenceId, value }); })
      .catch((e) => { if (!cancelled) setError(apiErrorMessage(e)); });
    return () => { cancelled = true; };
  }, [studyId, evidenceId]);

  const e = evidence?.id === evidenceId ? evidence.value : null;
  const detail = e?.detail ?? {};
  const charts = Array.isArray(detail.charts) ? (detail.charts as string[]) : [];
  return (
    <Modal open onClose={onClose} wide title={`Evidence ${evidenceId}`} description={e?.summary}>
      <ErrorBanner error={error} onClose={() => setError(null)} />
      {!e ? (
        !error && <div className="h-40 animate-pulse rounded-lg bg-zinc-100 dark:bg-zinc-900" />
      ) : (
        <div className="space-y-4 text-sm">
          <div className="flex flex-wrap items-center gap-2 text-xs text-zinc-500">
            <Badge tone={KIND_TONE[e.kind] ?? "neutral"}>{e.kind}</Badge>
            <span>by <span className="font-mono">{e.agent_id}</span></span>
            <span>{ago(e.created_at)}</span>
            {e.sources.map((s) => <Badge key={s}>{s}</Badge>)}
          </div>
          {typeof detail.sql === "string" && <Block title="Query">{detail.sql}</Block>}
          {typeof detail.code === "string" && <Block title="Code">{detail.code}</Block>}
          {typeof detail.stdout === "string" && detail.stdout && <Block title="Output">{detail.stdout}</Block>}
          {charts.length > 0 && (
            <div className="grid gap-3 sm:grid-cols-2">
              {charts.map((c) => (
                // eslint-disable-next-line @next/next/no-img-element -- served by the API with the session cookie
                <img key={c} src={studyFileUrl(studyId, c)} alt={`Chart from ${evidenceId}`} className="w-full rounded-lg border border-zinc-200 bg-white dark:border-zinc-800" />
              ))}
            </div>
          )}
          <Block title="Everything recorded">{JSON.stringify(detail, null, 2)}</Block>
        </div>
      )}
    </Modal>
  );
}

function Block({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div>
      <div className="mb-1 text-[11px] font-medium uppercase tracking-wide text-zinc-500">{title}</div>
      <pre className={cx("max-h-80 overflow-auto rounded-lg bg-zinc-50 p-3 font-mono text-[12px] leading-5 text-zinc-800 dark:bg-zinc-950 dark:text-zinc-200")}>{children}</pre>
    </div>
  );
}
