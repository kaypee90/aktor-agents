"use client";

import { useState } from "react";
import { apiErrorMessage, startStudyExperiment, type ExperimentSpec, type ModelChoice, type StudyDetail } from "@/lib/api";
import { Button, ErrorBanner, Field, Modal, Toggle, cx, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { EvidenceChip } from "./StudyEvidence";
import { ModelField } from "./ModelField";

type Segment = { name: string; share: string; description: string; attributes: string; ranges: string };
type Condition = { name: string; scenario: string };
type Fact = { fact: string; evidence_id?: string };

/** The form's state: what run_simulation takes, as editable text. */
interface Draft {
  name: string;
  size: string;
  rounds: string;
  replications: string;
  wordOfMouth: boolean;
  seed: string;
  model: string;
  question: string;
  options: string;
  valueName: string;
  valueMin: string;
  valueMax: string;
  conditions: Condition[];
  segments: Segment[];
  facts: Fact[];
  evidence: string[];
  calibrationOption: string;
  calibrationRate: string;
  calibrationEvidence?: string;
}

const lines = (text: string) => text.split("\n").map((l) => l.trim()).filter(Boolean);

function pairs(text: string) {
  const out: Record<string, string> = {};
  for (const line of lines(text)) {
    const at = line.indexOf(":");
    if (at > 0) out[line.slice(0, at).trim()] = line.slice(at + 1).trim();
  }
  return out;
}

function ranges(text: string) {
  const out: Record<string, [number, number]> = {};
  for (const [key, value] of Object.entries(pairs(text))) {
    const m = value.match(/^(-?[\d.]+)\s*(?:-|–|to|,)\s*(-?[\d.]+)$/);
    if (m && Number(m[2]) >= Number(m[1])) out[key] = [Number(m[1]), Number(m[2])];
  }
  return out;
}

const blankSegment = (): Segment => ({ name: "", share: "1", description: "", attributes: "", ranges: "" });

function draftOf(spec: ExperimentSpec | null, study: StudyDetail): Draft {
  if (!spec) {
    return {
      name: "", size: String(Math.min(20, study.experiment_limits.max_participants)), rounds: "1", replications: "1", wordOfMouth: false, seed: "42",
      model: study.model_profile_id ?? "", question: "", options: "", valueName: "", valueMin: "", valueMax: "",
      conditions: [{ name: "control", scenario: "" }, { name: "treatment", scenario: "" }],
      segments: [blankSegment()], facts: [], evidence: [], calibrationOption: "", calibrationRate: "",
    };
  }
  return {
    name: `${spec.name}-v2`,
    size: String(spec.population.size ?? 20),
    rounds: String(spec.rounds ?? 1),
    replications: String(spec.replications ?? 1),
    wordOfMouth: !!spec.word_of_mouth,
    seed: String(spec.seed ?? 42),
    model: study.model_profile_id ?? "",
    question: spec.decision.question,
    options: spec.decision.options.join("\n"),
    valueName: spec.decision.value?.name ?? "",
    valueMin: spec.decision.value ? String(spec.decision.value.min) : "",
    valueMax: spec.decision.value ? String(spec.decision.value.max) : "",
    conditions: spec.conditions.map((c, i) => ({ name: c.name ?? (i === 0 ? "control" : `treatment ${i}`), scenario: c.scenario })),
    segments: spec.population.segments.map((s) => ({
      name: s.name, share: String(s.share), description: s.description,
      attributes: Object.entries(s.attributes ?? {}).map(([k, v]) => `${k}: ${v}`).join("\n"),
      ranges: Object.entries(s.ranges ?? {}).map(([k, [lo, hi]]) => `${k}: ${lo}-${hi}`).join("\n"),
    })),
    facts: spec.facts ?? [],
    evidence: spec.population.evidence ?? [],
    calibrationOption: spec.calibration?.option ?? "",
    calibrationRate: spec.calibration ? String(Math.round((spec.calibration.rate > 1 ? spec.calibration.rate : spec.calibration.rate * 100) * 10) / 10) : "",
    calibrationEvidence: spec.calibration?.evidence_id,
  };
}

function specOf(d: Draft): ExperimentSpec {
  const options = lines(d.options);
  const spec: ExperimentSpec = {
    name: d.name.trim(),
    population: {
      size: Number(d.size),
      evidence: d.evidence,
      segments: d.segments.map((s) => ({
        name: s.name.trim(), share: Number(s.share) || 0, description: s.description.trim(), attributes: pairs(s.attributes), ranges: ranges(s.ranges),
      })),
    },
    conditions: d.conditions.filter((c) => c.scenario.trim()).map((c) => ({ name: c.name.trim() || undefined, scenario: c.scenario.trim() })),
    decision: { question: d.question.trim(), options },
    facts: d.facts.filter((f) => f.fact.trim()),
    rounds: Number(d.rounds),
    word_of_mouth: d.wordOfMouth && Number(d.rounds) > 1,
    replications: Number(d.replications),
    seed: Number(d.seed) || 42,
  };
  if (d.valueName.trim() && d.valueMin !== "" && d.valueMax !== "") {
    spec.decision.value = { name: d.valueName.trim(), min: Number(d.valueMin), max: Number(d.valueMax) };
  }
  if (d.calibrationOption && d.calibrationRate !== "") {
    spec.calibration = { option: d.calibrationOption, rate: Number(d.calibrationRate) / 100, evidence_id: d.calibrationEvidence };
  }
  return spec;
}

/** Why the draft can't run yet; null when it can. */
function problem(d: Draft, limits: StudyDetail["experiment_limits"]): string | null {
  if (!d.name.trim()) return "Give the experiment a name.";
  if (!d.question.trim() || lines(d.options).length < 2) return "The decision needs a question and at least two options.";
  if (!d.conditions.some((c) => c.scenario.trim())) return "Describe at least the control scenario.";
  if (d.segments.length === 0 || d.segments.some((s) => !s.description.trim())) return "Describe every segment of the population.";
  if (d.segments.reduce((n, s) => n + (Number(s.share) || 0), 0) <= 0) return "Segment shares must add up to more than 0.";
  const n = Number(d.size);
  if (!Number.isInteger(n) || n < 1 || n > limits.max_participants) return `Participants: 1 to ${limits.max_participants}.`;
  return null;
}

/**
 * Sets up and starts an experiment by hand: the same experiment agents run with run_simulation,
 * from scratch or from an earlier one with its population, conditions and decision filled in.
 */
export function ExperimentForm({ study, models, from, onClose, onStarted }: {
  study: StudyDetail;
  models: ModelChoice[];
  /** The experiment to start from; null for a blank one. */
  from: ExperimentSpec | null;
  onClose: () => void;
  onStarted: () => void;
}) {
  const limits = study.experiment_limits;
  const [d, setD] = useState<Draft>(() => draftOf(from, study));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const set = (patch: Partial<Draft>) => setD((x) => ({ ...x, ...patch }));
  const setCondition = (i: number, patch: Partial<Condition>) => set({ conditions: d.conditions.map((c, k) => (k === i ? { ...c, ...patch } : c)) });
  const setSegment = (i: number, patch: Partial<Segment>) => set({ segments: d.segments.map((s, k) => (k === i ? { ...s, ...patch } : s)) });
  const setFact = (i: number, fact: string) => set({ facts: d.facts.map((f, k) => (k === i ? { fact, evidence_id: f.fact === fact ? f.evidence_id : undefined } : f)) });

  const options = lines(d.options);
  const conditions = Math.max(1, d.conditions.filter((c) => c.scenario.trim()).length);
  const planned = (Number(d.size) || 0) * conditions * (Number(d.rounds) || 1) * (Number(d.replications) || 1);
  const totalShare = d.segments.reduce((n, s) => n + (Number(s.share) || 0), 0);
  const blocked = problem(d, limits);

  async function start() {
    if (blocked) return;
    setBusy(true);
    setError(null);
    try {
      await startStudyExperiment(study.study_id, specOf(d), d.model || null);
      onStarted();
    } catch (e) {
      setError(apiErrorMessage(e));
      setBusy(false);
    }
  }

  const numberInput = (value: string, onChange: (v: string) => void, min: number, max?: number) => (
    <input type="number" className={inputClass} value={value} min={min} max={max} onChange={(e) => onChange(e.target.value)} />
  );

  return (
    <Modal open onClose={onClose} wide title={from ? "Run the experiment again" : "New experiment"}
      description="A simulated population reacts to each scenario and makes the decision. Every decision is saved as a dataset of the study, and the results show under Experiments."
      footer={<>
        <span className="mr-auto text-xs text-zinc-500">
          Up to <span className="font-medium tabular-nums text-zinc-700 dark:text-zinc-300">{planned}</span> decisions · stops at ${limits.max_cost_usd.toFixed(2)}
        </span>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="primary" icon={<Icons.Play className="h-3.5 w-3.5" />} disabled={busy || !!blocked} title={blocked ?? undefined} onClick={start}>
          {busy ? "Starting…" : "Start experiment"}
        </Button>
      </>}>
      <div className="space-y-6">
        <ErrorBanner error={error} onClose={() => setError(null)} />

        <Section title="Setup">
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Name" hint="Also names the dataset of its decisions.">
              <input className={inputClass} value={d.name} maxLength={80} placeholder="rent-increase-8pct" onChange={(e) => set({ name: e.target.value })} />
            </Field>
            <ModelField models={models} value={d.model} onChange={(model) => set({ model })} hint="The model the participants decide with (this experiment only)." />
          </div>
          <div className="grid gap-4 sm:grid-cols-4">
            <Field label="Participants" hint={`Up to ${limits.max_participants}`}>{numberInput(d.size, (size) => set({ size }), 1, limits.max_participants)}</Field>
            <Field label="Rounds" hint={`Up to ${limits.max_rounds}`}>{numberInput(d.rounds, (rounds) => set({ rounds }), 1, limits.max_rounds)}</Field>
            <Field label="Repeated runs" hint={`Up to ${limits.max_replications}, new people each`}>{numberInput(d.replications, (replications) => set({ replications }), 1, limits.max_replications)}</Field>
            <Field label="Seed" hint="Same seed, same people">{numberInput(d.seed, (seed) => set({ seed }), 0)}</Field>
          </div>
          {Number(d.rounds) > 1 && (
            <Toggle checked={d.wordOfMouth} onChange={(wordOfMouth) => set({ wordOfMouth })} label="Word of mouth"
              description="From round 2, every participant hears what a few others chose and why." />
          )}
        </Section>

        <Section title="Decision">
          <Field label="Question"><input className={inputClass} value={d.question} placeholder="Do you renew your lease?" onChange={(e) => set({ question: e.target.value })} /></Field>
          <Field label="Options" hint="One per line, at least two (up to 8).">
            <textarea className={inputClass} rows={3} value={d.options} placeholder={"renew\nleave"} onChange={(e) => set({ options: e.target.value })} />
          </Field>
          <div className="grid gap-4 sm:grid-cols-3">
            <Field label="Number to give (optional)" hint="E.g. the most rent they'd accept">
              <input className={inputClass} value={d.valueName} placeholder="max_rent" onChange={(e) => set({ valueName: e.target.value })} />
            </Field>
            <Field label="Minimum"><input type="number" className={inputClass} value={d.valueMin} disabled={!d.valueName.trim()} onChange={(e) => set({ valueMin: e.target.value })} /></Field>
            <Field label="Maximum"><input type="number" className={inputClass} value={d.valueMax} disabled={!d.valueName.trim()} onChange={(e) => set({ valueMax: e.target.value })} /></Field>
          </div>
        </Section>

        <Section title="Conditions" hint={`The first is the control; up to ${limits.max_conditions}.`}
          action={d.conditions.length < limits.max_conditions && (
            <Button size="sm" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => set({ conditions: [...d.conditions, { name: `treatment ${d.conditions.length}`, scenario: "" }] })}>Condition</Button>
          )}>
          {d.conditions.map((c, i) => (
            <div key={i} className="rounded-lg border border-zinc-200 p-3 dark:border-zinc-800">
              <div className="mb-2 flex items-center gap-2">
                <input className={cx(inputClass, "py-1 text-xs font-medium")} value={c.name} placeholder={i === 0 ? "control" : `treatment ${i}`} onChange={(e) => setCondition(i, { name: e.target.value })} />
                {i === 0 && <span className="shrink-0 text-[11px] text-zinc-500">control</span>}
                {d.conditions.length > 1 && <RemoveButton label="Remove this condition" onClick={() => set({ conditions: d.conditions.filter((_, k) => k !== i) })} />}
              </div>
              <textarea className={inputClass} rows={2} value={c.scenario} placeholder={i === 0 ? "Your rent stays the same at renewal." : "Your rent rises 8% at renewal."}
                onChange={(e) => setCondition(i, { scenario: e.target.value })} />
            </div>
          ))}
        </Section>

        <Section title="Population" hint={`Segments are drawn by share (${totalShare > 0 ? "shares add up to " + Math.round(totalShare * 100) / 100 : "add shares"}).`}
          action={<Button size="sm" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => set({ segments: [...d.segments, blankSegment()] })}>Segment</Button>}>
          {d.evidence.length > 0 ? (
            <div className="flex flex-wrap items-center gap-1.5 text-xs text-zinc-500">Built from {d.evidence.map((e) => <EvidenceChip key={e} id={e} />)}</div>
          ) : (
            <p className="text-xs text-zinc-500">Not cited from the study&apos;s evidence: the experiment is recorded as set up by you.</p>
          )}
          {d.segments.map((s, i) => (
            <div key={i} className="space-y-2 rounded-lg border border-zinc-200 p-3 dark:border-zinc-800">
              <div className="flex items-center gap-2">
                <input className={cx(inputClass, "py-1 text-xs font-medium")} value={s.name} placeholder={`segment ${i + 1}`} onChange={(e) => setSegment(i, { name: e.target.value })} />
                <label className="flex shrink-0 items-center gap-1.5 text-[11px] text-zinc-500">
                  Share
                  <input type="number" step="0.05" min={0} className={cx(inputClass, "w-20 py-1 text-xs")} value={s.share} onChange={(e) => setSegment(i, { share: e.target.value })} />
                </label>
                {d.segments.length > 1 && <RemoveButton label="Remove this segment" onClick={() => set({ segments: d.segments.filter((_, k) => k !== i) })} />}
              </div>
              <textarea className={inputClass} rows={2} value={s.description} placeholder="You rent a studio downtown and have lived there for two years…"
                onChange={(e) => setSegment(i, { description: e.target.value })} />
              <div className="grid gap-2 sm:grid-cols-2">
                <Field label="Attributes" hint="key: value, one per line">
                  <textarea className={cx(inputClass, "font-mono text-xs")} rows={2} value={s.attributes} placeholder="unit: studio" onChange={(e) => setSegment(i, { attributes: e.target.value })} />
                </Field>
                <Field label="Drawn per participant" hint="key: min-max, one per line">
                  <textarea className={cx(inputClass, "font-mono text-xs")} rows={2} value={s.ranges} placeholder="income_k: 30-60" onChange={(e) => setSegment(i, { ranges: e.target.value })} />
                </Field>
              </div>
            </div>
          ))}
        </Section>

        <Section title="Facts (optional)" hint="What's true for everyone, e.g. market rents."
          action={<Button size="sm" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => set({ facts: [...d.facts, { fact: "" }] })}>Fact</Button>}>
          {d.facts.map((f, i) => (
            <div key={i} className="flex items-center gap-2">
              <input className={inputClass} value={f.fact} onChange={(e) => setFact(i, e.target.value)} />
              {f.evidence_id && <EvidenceChip id={f.evidence_id} />}
              <RemoveButton label="Remove this fact" onClick={() => set({ facts: d.facts.filter((_, k) => k !== i) })} />
            </div>
          ))}
        </Section>

        <Section title="Calibration (optional)" hint="The real share choosing one option under the control; the results report the gap.">
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Option">
              <select className={inputClass} value={options.includes(d.calibrationOption) ? d.calibrationOption : ""} onChange={(e) => set({ calibrationOption: e.target.value })}>
                <option value="">None</option>
                {options.map((o) => <option key={o} value={o}>{o}</option>)}
              </select>
            </Field>
            <Field label="Real rate (%)">
              <input type="number" min={0} max={100} step="0.1" className={inputClass} value={d.calibrationRate} disabled={!options.includes(d.calibrationOption)}
                onChange={(e) => set({ calibrationRate: e.target.value })} />
            </Field>
          </div>
        </Section>
        {blocked && <p className="text-xs text-amber-700 dark:text-amber-400">{blocked}</p>}
      </div>
    </Modal>
  );
}

function Section({ title, hint, action, children }: { title: string; hint?: string; action?: React.ReactNode; children: React.ReactNode }) {
  return (
    <section className="space-y-3">
      <div className="flex items-end justify-between gap-3 border-b border-zinc-100 pb-1.5 dark:border-zinc-800">
        <div>
          <h3 className="text-sm font-semibold text-zinc-900 dark:text-zinc-100">{title}</h3>
          {hint && <p className="text-[11px] text-zinc-500">{hint}</p>}
        </div>
        {action}
      </div>
      {children}
    </section>
  );
}

function RemoveButton({ label, onClick }: { label: string; onClick: () => void }) {
  return (
    <button type="button" aria-label={label} title={label} onClick={onClick}
      className="shrink-0 rounded-md p-1.5 text-zinc-400 hover:bg-zinc-100 hover:text-zinc-700 dark:hover:bg-zinc-800 dark:hover:text-zinc-200">
      <Icons.Trash className="h-3.5 w-3.5" />
    </button>
  );
}
