"use client";

import { useMemo, useState } from "react";
import { Button, Field, Toggle, cx, inputClass } from "@/components/ui";
import { CAPABILITIES, kindOf, type PipelineStage, type ScriptLanguage, type StageKind, type StagePatch } from "@/lib/pipelineTypes";
import type { ConnectionView } from "@/lib/workspaceTypes";
import { MentionTextarea, useModelChoices, type Mentionable } from "@/components/ui/MentionTextarea";
import { apiErrorMessage, testScript, type ScriptTestResult } from "@/lib/api";
import { StageKindIcon } from "./StageKindIcon";
import { STAGE_COLORS, type StageColor } from "./stageColors";
import { ConnectorIcon } from "./ConnectorIcon";

/** A blank stage for the "add a stage" form. */
export const NEW_STAGE: StagePatch & { name: string } = {
  name: "",
  role: "",
  instructions: "",
  capabilities: ["research"],
  max_helpers: 0,
  may_message_stages: true,
  retries: 1,
  on_failure: "FailRun",
  max_cost_usd: null,
  kind: "Agent",
};

export const SCRIPT_TEMPLATES: Record<ScriptLanguage, string> = {
  python: `# data has: input (the run's input), stages (earlier stages' outputs by id),
# previous (the output of the stage before this one), run and vars (the workspace's variables).
# What you return is this stage's output.
rows = (data.get("previous") or {}).get("rows", [])
return [
    {"name": r.get("Name"), "email": (r.get("Email") or "").strip().lower()}
    for r in rows
    if r.get("Email")
]`,
  javascript: `// data has: input (the run's input), stages (earlier stages' outputs by id),
// previous (the output of the stage before this one), run and vars (the workspace's variables).
// What you return is this stage's output.
const rows = data.previous?.rows ?? [];
return rows
  .filter((r) => r.Email)
  .map((r) => ({ name: r.Name, email: r.Email.trim().toLowerCase() }));`,
};

const KINDS: { kind: StageKind; label: string; hint: string }[] = [
  { kind: "Agent", label: "Agent", hint: "An AI agent works on it with tools." },
  { kind: "Action", label: "Action", hint: "Call a connected service. No model." },
  { kind: "Script", label: "Script", hint: "Python or JavaScript turns data into data." },
];

const SIDE_EFFECTS: Record<string, { label: string; className: string }> = {
  ReadOnly: { label: "reads", className: "bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300" },
  Idempotent: { label: "writes (safe to repeat)", className: "bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-300" },
  NonIdempotent: { label: "writes", className: "bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-300" },
};

const EXAMPLE_ARGS = '{ "path": "/users/{{ item }}" }';

/** What's wrong with the arguments, in words that say how to fix it; null when they're fine. */
function jsonProblem(text: string): string | null {
  const t = text.trim();
  if (!t) return null;
  try {
    const parsed = JSON.parse(t);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed)
      ? null
      : `The arguments must be a JSON object: field names and values in { }, e.g. ${EXAMPLE_ARGS}.`;
  } catch (e) {
    // An expression on its own, or not in quotes: the commonest slip.
    if (/^\{\{[^}]*\}\}$/.test(t)) {
      return `An expression on its own isn't arguments. Put it in quotes as a field's value inside { }, e.g. ${EXAMPLE_ARGS}.`;
    }
    if (/[:,{[]\s*\{\{/.test(t) || t.startsWith("{{")) {
      return `Put each {{ … }} in quotes as a value: "field": "{{ item }}". For example ${EXAMPLE_ARGS}.`;
    }
    if (/\{\s*[A-Za-z_]/.test(t) || /,\s*[A-Za-z_][\w-]*\s*:/.test(t)) {
      return `Field names need quotes: "path": … rather than path: …. For example ${EXAMPLE_ARGS}.`;
    }
    return `Not valid JSON (${(e as Error).message}). Arguments look like ${EXAMPLE_ARGS}.`;
  }
}

/**
 * A stage's settings. An agent stage: who the agent is, what it does, its tools, and how much
 * freedom it has. An action: which connected service's tool it calls and with what. A script: its
 * code, which can be tried on sample data before saving. The runtime checks every value when saved.
 */
export function StageEditor({ stage, stageNames, submitLabel, onSubmit, onCancel, onRemove, busy, mentionables = [], workspaceId, connections = [], stageIds = [], variables = [], sampleData, focus }: {
  stage: StagePatch & { name: string };
  /** Names of the stages this one takes input from, to show where it sits. */
  stageNames?: string[];
  submitLabel: string;
  onSubmit: (patch: StagePatch & { name: string }) => void;
  onCancel?: () => void;
  onRemove?: () => void;
  busy?: boolean;
  /** What the instructions can @mention (the pipeline's stages, models). */
  mentionables?: Mentionable[];
  /** For trying scripts out. */
  workspaceId?: string;
  /** The workspace's connections, whose tools an action can call. */
  connections?: ConnectionView[];
  /** The pipeline's stage ids, for expressions like stages.<id>. */
  stageIds?: string[];
  /** The workspace's variable names, for expressions like vars.<name>. */
  variables?: string[];
  /** Data to try a script on (the latest run's, when there is one). */
  sampleData?: unknown;
  /** Which part of the stage to scroll to (an agent's model or tools, from the canvas). */
  focus?: "model" | "tools" | null;
}) {
  const [draft, setDraft] = useState(stage);
  const models = useModelChoices();
  const set = <K extends keyof PipelineStage>(key: K, value: PipelineStage[K]) => setDraft((d) => ({ ...d, [key]: value }));
  const kind = kindOf(draft);
  const caps = new Set(draft.capabilities ?? []);
  const toggleCap = (id: string) => {
    const next = new Set(caps);
    if (next.has(id)) next.delete(id); else next.add(id);
    set("capabilities", [...next]);
  };

  // The API lists tools as agents see them ("sheets__read_rows"); older responses had the plugin's own name.
  const tools = useMemo(() => connections.flatMap((c) => c.tools.filter((t) => t.enabled).map((t) => {
    const exposed = t.name.includes("__") ? t.name : `${c.name}__${t.name}`;
    return { ...t, name: exposed.slice(exposed.indexOf("__") + 2), connection: c.name, pluginId: c.plugin_id, exposed };
  })), [connections]);
  const chosenTool = tools.find((t) => t.exposed === draft.tool);
  const argsProblem = kind === "Action" ? jsonProblem(draft.arguments ?? "") : null;
  const valid = draft.name.trim().length > 0 && (
    kind === "Agent" ? (draft.instructions ?? "").trim().length > 0
      : kind === "Action" ? !!draft.tool && !argsProblem
      : (draft.code ?? "").trim().length > 0 && !!draft.language);

  function chooseKind(next: StageKind) {
    setDraft((d) => ({
      ...d,
      kind: next,
      language: next === "Script" ? d.language ?? "python" : d.language,
      code: next === "Script" && !d.code ? SCRIPT_TEMPLATES[(d.language ?? "python") as ScriptLanguage] : d.code,
      arguments: next === "Action" && !d.arguments ? "{\n  \n}" : d.arguments,
    }));
  }

  function submit() {
    const name = draft.name.trim();
    onSubmit({
      ...draft,
      name,
      role: kind === "Agent" ? draft.role?.trim() || name : draft.role ?? "",
      instructions: draft.instructions ?? "",
      arguments: kind === "Action" && !(draft.arguments ?? "").trim() ? "{}" : draft.arguments,
    });
  }

  return (
    <form onSubmit={(e) => { e.preventDefault(); if (valid) submit(); }} className="space-y-3 text-sm">
      <div className="grid grid-cols-3 gap-1.5" role="radiogroup" aria-label="What does this stage's work">
        {KINDS.map((k) => (
          <button type="button" key={k.kind} role="radio" aria-checked={kind === k.kind} onClick={() => chooseKind(k.kind)} title={k.hint}
            className={cx("flex flex-col items-start gap-0.5 rounded-lg border px-2 py-1.5 text-left transition",
              kind === k.kind ? "border-brand-400 bg-brand-50 dark:border-brand-600 dark:bg-brand-500/10" : "border-zinc-200 hover:border-zinc-300 dark:border-zinc-700")}>
            <span className="flex items-center gap-1.5 text-xs font-medium"><StageKindIcon kind={k.kind} className="h-3.5 w-3.5" />{k.label}</span>
            <span className="text-[10px] leading-tight text-zinc-500">{k.hint}</span>
          </button>
        ))}
      </div>

      <ColorPicker value={draft.color ?? null} onChange={(c) => set("color", c)} />

      {stageNames && stageNames.length > 0 && (
        <p className="text-xs text-zinc-500">Takes input from: {stageNames.join(", ")}</p>
      )}

      {kind === "Agent" ? (
        <>
          <div className="grid grid-cols-2 gap-2">
            <Field label="Name">
              <input className={inputClass} value={draft.name} onChange={(e) => set("name", e.target.value)} placeholder="e.g. Security review" autoFocus />
            </Field>
            <Field label="Role">
              <input className={inputClass} value={draft.role ?? ""} onChange={(e) => set("role", e.target.value)} placeholder="e.g. Security reviewer" />
            </Field>
          </div>
          <Field label="Instructions" hint={`What the agent does with the run's input and earlier stages' results, for whom, to what standard.${
            variables.length > 0 ? ` {{ vars.name }} is filled in from the workspace's variables (${variables.join(", ")}).` : ""}`}>
            <MentionTextarea className={cx(inputClass, "min-h-28 resize-y")} value={draft.instructions ?? ""} onValueChange={(v) => set("instructions", v)}
              mentionables={mentionables.filter((m) => m.kind === "agent" || m.kind === "skill")}
              placeholder="Review @backend's changes for security issues (auth, injection, secrets) and list what must be fixed before release. Type @ to mention a stage or a skill: the agent loads a mentioned skill first." />
          </Field>
          <div ref={(el) => { if (el && focus === "model") el.scrollIntoView({ block: "center" }); }}>
            <Field label="Model" hint="A stronger model for hard stages, a cheaper one for simple ones.">
              <select className={inputClass} value={draft.model_profile_id ?? ""}
                onChange={(e) => set("model_profile_id", e.target.value)}>
                <option value="">The workspace&apos;s model</option>
                {models.map((m) => <option key={m.id} value={m.id}>{m.name} ({m.provider} · {m.model})</option>)}
              </select>
            </Field>
          </div>
          <div ref={(el) => { if (el && focus === "tools") el.scrollIntoView({ block: "center" }); }}>
            <Field label="Tools">
              <div className="flex flex-wrap gap-1.5">
                {CAPABILITIES.filter((c) => c.id !== "filesystem").map((c) => (
                  <button type="button" key={c.id} onClick={() => toggleCap(c.id)}
                    className={cx("rounded-full border px-2 py-0.5 text-xs transition",
                      caps.has(c.id) ? "border-brand-400 bg-brand-50 text-brand-700 dark:border-brand-600 dark:bg-brand-500/10 dark:text-brand-300"
                        : "border-zinc-200 text-zinc-600 hover:border-zinc-300 dark:border-zinc-700 dark:text-zinc-400")}>
                    {c.label}
                  </button>
                ))}
              </div>
              <p className="mt-1 text-[11px] text-zinc-400">
                Every stage can read and write the run&apos;s files and use the workspace&apos;s connections
                {connections.length > 0 ? ` (${connections.map((c) => c.name).join(", ")})` : ""}.
              </p>
            </Field>
          </div>
          <div className="grid grid-cols-3 gap-2">
            <Field label="Helpers" hint="Agents it may start for big parallel work.">
              <input type="number" min={0} max={5} className={inputClass} value={draft.max_helpers ?? 0}
                onChange={(e) => set("max_helpers", Math.max(0, Math.min(5, Number(e.target.value) || 0)))} />
            </Field>
            <Field label="Retries" hint="Extra attempts if it fails.">
              <input type="number" min={0} max={3} className={inputClass} value={draft.retries ?? 1}
                onChange={(e) => set("retries", Math.max(0, Math.min(3, Number(e.target.value) || 0)))} />
            </Field>
            <Field label="Max cost ($)" hint="Per run; empty: the workspace's default.">
              <input type="number" min={0} step={0.05} className={inputClass} value={draft.max_cost_usd ?? ""}
                onChange={(e) => set("max_cost_usd", e.target.value === "" ? null : Math.max(0, Number(e.target.value)))} />
            </Field>
          </div>
          <Toggle checked={draft.may_message_stages ?? true} onChange={(v) => set("may_message_stages", v)}
            label="May message other stages" description="Ask a quick question of a stage working at the same time." />
        </>
      ) : (
        <>
          <Field label="Name">
            <input className={inputClass} value={draft.name} onChange={(e) => set("name", e.target.value)}
              placeholder={kind === "Action" ? "e.g. Add to the leads sheet" : "e.g. Clean up the rows"} autoFocus />
          </Field>
          {kind === "Action" ? (
            <ActionFields draft={draft} set={set} tools={tools} chosen={chosenTool} argsProblem={argsProblem} stageIds={stageIds} variables={variables} />
          ) : (
            <ScriptFields draft={draft} set={set} workspaceId={workspaceId} sampleData={sampleData} />
          )}
          <Field label="Retries" hint={kind === "Action" ? "Extra attempts if the call fails. Writes that may already have happened aren't repeated." : "Extra attempts if the script fails."}>
            <input type="number" min={0} max={3} className={inputClass} value={draft.retries ?? 1}
              onChange={(e) => set("retries", Math.max(0, Math.min(3, Number(e.target.value) || 0)))} />
          </Field>
        </>
      )}

      <Toggle checked={(draft.on_failure ?? "FailRun") === "Continue"} onChange={(v) => set("on_failure", v ? "Continue" : "FailRun")}
        label="Keep going if it fails" description="Later stages still run, told it failed. Off: the run stops." />
      <div className="flex items-center gap-2 pt-1">
        <Button type="submit" variant="primary" size="sm" disabled={!valid || busy}>{busy ? "Saving…" : submitLabel}</Button>
        {onCancel && <Button type="button" variant="ghost" size="sm" onClick={onCancel}>Cancel</Button>}
        {onRemove && (
          <button type="button" onClick={onRemove} className="ml-auto text-xs text-rose-600 hover:underline dark:text-rose-400">Remove stage</button>
        )}
      </div>
    </form>
  );
}

/** Optional color for the stage on the canvas, to group or highlight stages. */
function ColorPicker({ value, onChange }: { value: string | null; onChange: (color: string) => void }) {
  return (
    <div className="flex flex-wrap items-center gap-1" role="radiogroup" aria-label="Color on the canvas">
      <span className="mr-1 text-xs text-zinc-500">Color</span>
      <button type="button" role="radio" aria-checked={!value} onClick={() => onChange("")} title="No color"
        className={cx("flex h-4.5 w-4.5 items-center justify-center rounded-full border text-[9px] text-zinc-400",
          !value ? "border-brand-500 ring-2 ring-brand-500/30" : "border-zinc-300 dark:border-zinc-600")}>
        ∅
      </button>
      {(Object.keys(STAGE_COLORS) as StageColor[]).map((c) => (
        <button key={c} type="button" role="radio" aria-checked={value === c} onClick={() => onChange(c)} title={STAGE_COLORS[c].label}
          className={cx("h-4.5 w-4.5 rounded-full transition", STAGE_COLORS[c].swatch,
            value === c ? "ring-2 ring-offset-2 ring-zinc-500 dark:ring-offset-zinc-950" : "hover:scale-110")}>
          <span className="sr-only">{STAGE_COLORS[c].label}</span>
        </button>
      ))}
    </div>
  );
}

type Draft = StagePatch & { name: string };
type Setter = <K extends keyof PipelineStage>(key: K, value: PipelineStage[K]) => void;
type ToolChoice = { name: string; description: string; side_effects: string; connection: string; pluginId: string; exposed: string };

function ActionFields({ draft, set, tools, chosen, argsProblem, stageIds, variables }: {
  draft: Draft; set: Setter; tools: ToolChoice[]; chosen?: ToolChoice; argsProblem: string | null; stageIds: string[]; variables: string[];
}) {
  const byConnection = useMemo(() => {
    const groups = new Map<string, ToolChoice[]>();
    for (const t of tools) groups.set(t.connection, [...(groups.get(t.connection) ?? []), t]);
    return [...groups.entries()];
  }, [tools]);
  const effects = chosen ? SIDE_EFFECTS[chosen.side_effects] : null;

  return (
    <>
      <Field label="Call" hint="A tool of one of the workspace's connections (Integrations tab).">
        {tools.length === 0 ? (
          <p className="rounded-lg border border-dashed border-zinc-300 p-2 text-xs text-zinc-500 dark:border-zinc-700">
            No connection tools yet. Add a connection (Slack, Google Sheets, Airtable, BigQuery, Zoho CRM, any REST API…) on the Integrations tab first.
          </p>
        ) : (
          <div className="flex items-center gap-2">
            {chosen && <ConnectorIcon pluginId={chosen.pluginId} name={chosen.connection} className="h-5 w-5" />}
            <select className={inputClass} value={draft.tool ?? ""} onChange={(e) => set("tool", e.target.value)}>
              <option value="" disabled>Choose a tool…</option>
              {draft.tool && !chosen && <option value={draft.tool}>{draft.tool} (not available now)</option>}
              {byConnection.map(([connection, list]) => (
                <optgroup key={connection} label={connection}>
                  {list.map((t) => <option key={t.exposed} value={t.exposed}>{t.name}</option>)}
                </optgroup>
              ))}
            </select>
          </div>
        )}
        {chosen && (
          <p className="mt-1 text-[11px] text-zinc-500">
            {effects && <span className={cx("mr-1.5 rounded-full px-1.5 py-0.5 font-medium", effects.className)}>{effects.label}</span>}
            {chosen.description}
          </p>
        )}
      </Field>
      <Field label="Arguments" hint="JSON. Fill values in from the run's data with {{ expressions }}.">
        <textarea className={cx(inputClass, "min-h-32 resize-y font-mono text-xs")} spellCheck={false} value={draft.arguments ?? ""}
          onChange={(e) => set("arguments", e.target.value)} placeholder={'{\n  "range": "Leads",\n  "rows": "{{ previous }}"\n}'} />
        {argsProblem && <p className="mt-1 text-[11px] text-rose-600 dark:text-rose-400">{argsProblem}</p>}
      </Field>
      <Field label="For each (optional)" hint="A list in the run's data: the tool is called once per item, with the item as {{ item }}.">
        <input className={cx(inputClass, "font-mono text-xs")} value={draft.for_each ?? ""} onChange={(e) => set("for_each", e.target.value)}
          placeholder="e.g. previous.rows or input.contacts" />
      </Field>
      <ExpressionHelp stageIds={stageIds} variables={variables} />
    </>
  );
}

function ExpressionHelp({ stageIds, variables }: { stageIds: string[]; variables: string[] }) {
  return (
    <details className="rounded-lg bg-zinc-50 p-2 text-[11px] text-zinc-600 dark:bg-zinc-900 dark:text-zinc-400">
      <summary className="cursor-pointer select-none font-medium">What can {"{{ expressions }}"} use?</summary>
      <ul className="mt-1.5 space-y-0.5 font-mono">
        <li><span className="text-brand-600 dark:text-brand-400">input</span>: the run&apos;s input (a form&apos;s answers: input.email)</li>
        <li><span className="text-brand-600 dark:text-brand-400">previous</span>: the output of the stage before this one</li>
        <li><span className="text-brand-600 dark:text-brand-400">stages.&lt;id&gt;</span>: any earlier stage&apos;s output</li>
        <li><span className="text-brand-600 dark:text-brand-400">item</span>, <span className="text-brand-600 dark:text-brand-400">index</span>: with For each</li>
        <li><span className="text-brand-600 dark:text-brand-400">run</span>: id, number, source, trigger</li>
        <li><span className="text-brand-600 dark:text-brand-400">vars</span>: the workspace&apos;s variables (vars.email_recipient), set on the Variables tab</li>
      </ul>
      <p className="mt-1.5">
        Paths: <code>.name</code>, <code>[0]</code>, <code>[&apos;Full name&apos;]</code>, <code>[*]</code>. A value that is only an expression keeps its type
        (a list stays a list); inside other text it&apos;s written in.
      </p>
      {stageIds.length > 0 && <p className="mt-1">Stage ids: {stageIds.map((id) => <code key={id} className="mr-1">{id}</code>)}</p>}
      {variables.length > 0 && <p className="mt-1">Variables: {variables.map((v) => <code key={v} className="mr-1">vars.{v}</code>)}</p>}
    </details>
  );
}

function ScriptFields({ draft, set, workspaceId, sampleData }: { draft: Draft; set: Setter; workspaceId?: string; sampleData?: unknown }) {
  const language = (draft.language ?? "python") as ScriptLanguage;
  const [sample, setSample] = useState(() => JSON.stringify(sampleData ?? { input: {}, stages: {}, previous: null, run: {}, vars: {} }, null, 2));
  const [testing, setTesting] = useState(false);
  const [result, setResult] = useState<ScriptTestResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  function changeLanguage(next: ScriptLanguage) {
    // Swap in the other language's template while the code is still the untouched template.
    const untouched = !draft.code || draft.code === SCRIPT_TEMPLATES[language];
    set("language", next);
    if (untouched) set("code", SCRIPT_TEMPLATES[next]);
  }

  async function runTest() {
    if (!workspaceId) return;
    setError(null);
    setResult(null);
    let data: unknown;
    try {
      data = JSON.parse(sample || "{}");
    } catch (e) {
      setError(`The test data isn't valid JSON: ${(e as Error).message}`);
      return;
    }
    setTesting(true);
    try {
      setResult(await testScript(workspaceId, { language, code: draft.code ?? "", data, timeout_seconds: draft.timeout_seconds }));
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setTesting(false);
    }
  }

  return (
    <>
      <div className="grid grid-cols-2 gap-2">
        <Field label="Language">
          <select className={inputClass} value={language} onChange={(e) => changeLanguage(e.target.value as ScriptLanguage)}>
            <option value="python">Python 3.12</option>
            <option value="javascript">JavaScript (Node 22)</option>
          </select>
        </Field>
        <Field label="Time limit (s)" hint="Empty: the server's default.">
          <input type="number" min={1} max={300} className={inputClass} value={draft.timeout_seconds ?? ""}
            onChange={(e) => set("timeout_seconds", e.target.value === "" ? null : Math.max(1, Math.min(300, Number(e.target.value) || 1)))} />
        </Field>
      </div>
      <Field label="Code" hint="The body of a function given data; return the output (JSON: lists, objects, text, numbers). Runs with no network.">
        <CodeArea value={draft.code ?? ""} onChange={(v) => set("code", v)} />
      </Field>
      {workspaceId && (
        <details className="rounded-lg border border-zinc-200 p-2 dark:border-zinc-800" open={!!result || !!error}>
          <summary className="cursor-pointer select-none text-xs font-medium">Try it on sample data</summary>
          <textarea className={cx(inputClass, "mt-2 min-h-24 resize-y font-mono text-[11px]")} spellCheck={false} value={sample} onChange={(e) => setSample(e.target.value)} />
          <div className="mt-2 flex items-center gap-2">
            <Button type="button" size="sm" onClick={runTest} disabled={testing || !(draft.code ?? "").trim()}>{testing ? "Running…" : "Run test"}</Button>
            {result && <span className="text-[11px] text-zinc-500">{result.duration_ms.toLocaleString()} ms</span>}
          </div>
          {error && <p className="mt-2 text-[11px] text-rose-600 dark:text-rose-400">{error}</p>}
          {result && (
            <div className="mt-2 space-y-1.5 text-[11px]">
              {result.ok
                ? <pre className="max-h-48 overflow-auto rounded bg-emerald-50 p-2 font-mono text-emerald-900 dark:bg-emerald-950/40 dark:text-emerald-200">{JSON.stringify(result.output, null, 2)}</pre>
                : <p className="rounded bg-rose-50 p-2 text-rose-800 dark:bg-rose-950/50 dark:text-rose-200">{result.error}</p>}
              {result.logs && <pre className="max-h-32 overflow-auto rounded bg-zinc-100 p-2 font-mono text-zinc-700 dark:bg-zinc-900 dark:text-zinc-300">{result.logs}</pre>}
            </div>
          )}
        </details>
      )}
    </>
  );
}

/** A plain code box: monospace, Tab indents instead of leaving the field. */
function CodeArea({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  return (
    <textarea
      className={cx(inputClass, "min-h-48 resize-y whitespace-pre font-mono text-xs leading-relaxed")}
      spellCheck={false}
      wrap="off"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      onKeyDown={(e) => {
        if (e.key !== "Tab" || e.shiftKey) return;
        e.preventDefault();
        const el = e.currentTarget;
        const { selectionStart: start, selectionEnd: end } = el;
        onChange(value.slice(0, start) + "    " + value.slice(end));
        requestAnimationFrame(() => { el.selectionStart = el.selectionEnd = start + 4; });
      }}
    />
  );
}
