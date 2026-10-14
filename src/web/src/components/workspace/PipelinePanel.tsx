"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { Button, ErrorBanner, Field, Modal, ago, cx, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { apiErrorMessage, applyPipelineEdits, getPipelineHistory, getPipelineRun, proposePipelineChange, restorePipelineVersion, savePipeline, savePipelineLayout } from "@/lib/api";
import { kindOf, type PipelineDefinition, type PipelineEditOp, type PipelineProposal, type PipelineRunView, type PipelineStage, type StageKind, type StagePatch, type StagePosition } from "@/lib/pipelineTypes";
import { MentionTextarea, useModelChoices, useModelMentionables, useWorkspaceMentionables } from "@/components/ui/MentionTextarea";
import type { WorkspaceSnapshot } from "@/lib/workspaceTypes";
import { PipelineCanvas, type AgentPart, type CanvasEditing } from "./PipelineCanvas";
import { NEW_STAGE, SCRIPT_TEMPLATES, StageEditor } from "./StageEditor";
import { TriggerIcon } from "./StageKindIcon";

const EXAMPLES = ["Add a fact checker after Research", "Add a reviewer before the last stage", "Remove the reviewer"];

/** Where a stage being added goes: on a connection or at an end, or where it was dropped on the canvas. */
type Insertion = { after?: string; before?: string; position?: StagePosition; inputs?: string[]; kind?: StageKind };

const PARTS_KEY = "aktor:pipeline-parts";

/** A blank stage of a kind, for the "add a stage" form. */
function newStage(kind: StageKind = "Agent"): StagePatch & { name: string } {
  if (kind === "Agent") return NEW_STAGE;
  return {
    ...NEW_STAGE,
    kind,
    capabilities: [],
    retries: kind === "Action" ? 0 : 1,
    arguments: kind === "Action" ? "{\n  \n}" : undefined,
    language: kind === "Script" ? "python" : undefined,
    code: kind === "Script" ? SCRIPT_TEMPLATES.python : undefined,
  };
}

function parse(json: string | null | undefined): unknown {
  if (json == null) return null;
  try { return JSON.parse(json); } catch { return json; }
}

/** The data a stage would see in `run`: what a script can be tried on before it's saved. */
function sampleFor(stage: Pick<PipelineStage, "inputs">, run: PipelineRunView) {
  const stages: Record<string, unknown> = {};
  for (const s of run.stages) if (s.status === "Completed") stages[s.stage_id] = s.output_json ? parse(s.output_json) : { summary: s.summary };
  return {
    input: run.input_data ? parse(run.input_data) : run.input,
    stages,
    previous: stage.inputs.length === 1 ? stages[stage.inputs[0]] ?? null : null,
    run: { id: run.run_id, number: run.number, source: run.source, trigger: run.trigger_name },
  };
}

/** JSON shown readably, or the text as it is. */
function Pretty({ json }: { json: string }) {
  const value = parse(json);
  return (
    <pre className="max-h-72 overflow-auto rounded-lg bg-zinc-50 p-2.5 font-mono text-[11px] text-zinc-800 dark:bg-zinc-900 dark:text-zinc-200">
      {typeof value === "string" ? value : JSON.stringify(value, null, 2)}
    </pre>
  );
}

/**
 * The workspace's pipeline: describe a change in plain language (previewed on the canvas before
 * it's applied; @mention stages and models to be precise), or edit on the canvas directly: drag
 * stages around, draw connections, drop in new agents. Every applied change is a new version, so any
 * change can be undone from the history. With a run selected, the canvas shows that run instead.
 */
export function PipelinePanel({ workspace, run, onCloseRun, onChanged, onSelectAgent }: {
  workspace: WorkspaceSnapshot;
  run: PipelineRunView | null;
  onCloseRun: () => void;
  onChanged: () => void;
  onSelectAgent: (agentId: string) => void;
}) {
  const pipeline = workspace.pipeline!;
  const workspaceId = workspace.workspace_id;
  const archived = workspace.status === "Archived";

  const [request, setRequest] = useState("");
  const [proposal, setProposal] = useState<PipelineProposal | null>(null);
  const [busy, setBusy] = useState<"propose" | "apply" | "edit" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [inserting, setInserting] = useState<Insertion | null>(null);
  const [history, setHistory] = useState<PipelineDefinition[] | null>(null);
  const [settings, setSettings] = useState<Pick<PipelineDefinition, "max_run_minutes" | "max_concurrent_runs" | "result_urgency"> | null>(null);
  const [focus, setFocus] = useState<AgentPart | null>(null);
  const [selectedTrigger, setSelectedTrigger] = useState<string | null>(null);
  const [showParts, setShowParts] = useState(() => {
    try { return typeof window === "undefined" || window.localStorage.getItem(PARTS_KEY) !== "off"; } catch { return true; }
  });
  const toggleParts = () => setShowParts((v) => {
    try { window.localStorage.setItem(PARTS_KEY, v ? "off" : "on"); } catch { /* per-browser convenience only */ }
    return !v;
  });
  const modelChoices = useModelChoices();
  const connections = useMemo(() => workspace.connections ?? [], [workspace.connections]);

  // The latest run, for trying scripts out on real data.
  const latestRunId = workspace.runs.find((r) => r.status === "Completed" || r.status === "Failed")?.run_id ?? null;
  const [latestRun, setLatestRun] = useState<PipelineRunView | null>(null);
  useEffect(() => {
    if (!latestRunId) return;
    let live = true;
    getPipelineRun(workspaceId, latestRunId).then((r) => { if (live) setLatestRun(r); }).catch(() => {});
    return () => { live = false; };
  }, [workspaceId, latestRunId]);

  const editable = !archived && !proposal && !run;
  const shown = proposal?.preview ?? pipeline;
  const models = useModelMentionables();
  const workspaceMentions = useWorkspaceMentionables(workspace);
  const mentionables = useMemo(() => [...workspaceMentions, ...models], [workspaceMentions, models]);
  const nameOf = (id: string) => pipeline.stages.find((s) => s.stage_id === id)?.name ?? id;

  /** Applies edits to the version on screen; a conflict means someone else changed it first. */
  const apply = useCallback(async (ops: PipelineEditOp[], baseVersion: number, note?: string) => {
    setError(null);
    try {
      await applyPipelineEdits(workspaceId, ops, baseVersion, note);
      onChanged();
      return true;
    } catch (e) {
      setError(apiErrorMessage(e));
      onChanged();
      return false;
    }
  }, [workspaceId, onChanged]);

  async function propose(text: string) {
    if (!text.trim()) return;
    setBusy("propose");
    setError(null);
    setSelected(null);
    try {
      const result = await proposePipelineChange(workspaceId, text.trim());
      if (result.valid) setProposal(result);
      else setError(result.errors.join(" ") || "The editor couldn't work out a change. Try saying it differently.");
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  async function applyProposal() {
    if (!proposal) return;
    setBusy("apply");
    if (await apply(proposal.ops, proposal.base_version, proposal.summary || request.trim())) {
      setProposal(null);
      setRequest("");
    }
    setBusy(null);
  }

  const onInsert = useCallback((where: { after?: string; before?: string }) => setInserting(where), []);
  const onRemove = useCallback((stageId: string) => {
    const name = pipeline.stages.find((s) => s.stage_id === stageId)?.name ?? stageId;
    if (pipeline.stages.length <= 1) { setError("A pipeline needs at least one stage."); return; }
    if (!confirm(`Remove '${name}'? The stages around it are joined up.`)) return;
    setSelected(null);
    void apply([{ op: "remove_stage", stage_id: stageId }], pipeline.version);
  }, [pipeline, apply]);

  async function addStage(stage: StagePatch & { name: string }) {
    if (!inserting) return;
    const { inputs, after, before, position } = inserting;
    setBusy("edit");
    if (await apply([{ op: "add_stage", stage: inputs ? { ...stage, inputs } : stage, after, before, position }], pipeline.version)) setInserting(null);
    setBusy(null);
  }

  const editing = useMemo<CanvasEditing | null>(() => !editable ? null : {
    onInsert,
    onRemove,
    onConnect: (from, to) => void apply([{ op: "connect", from, to }], pipeline.version),
    onDisconnect: (from, to) => void apply([{ op: "disconnect", from, to }], pipeline.version),
    onAddAt: (position, inputs, kind) => setInserting({ position, inputs, kind }),
    onMove: (layout) => {
      savePipelineLayout(workspaceId, layout).then(onChanged).catch((e) => setError(apiErrorMessage(e)));
    },
  }, [editable, onInsert, onRemove, apply, pipeline.version, workspaceId, onChanged]);

  async function updateStage(stageId: string, stage: StagePatch & { name: string }) {
    setBusy("edit");
    await apply([{ op: "update_stage", stage_id: stageId, stage }], pipeline.version);
    setBusy(null);
  }

  async function openHistory() {
    try {
      setHistory(await getPipelineHistory(workspaceId));
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  async function saveSettings() {
    if (!settings) return;
    setError(null);
    try {
      await savePipeline(workspaceId, { ...pipeline, ...settings }, pipeline.version, "Changed run settings");
      setSettings(null);
      onChanged();
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  async function restore(version: number) {
    setError(null);
    try {
      await restorePipelineVersion(workspaceId, version);
      setHistory(null);
      onChanged();
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  const selectedStage = selected ? shown.stages.find((s) => s.stage_id === selected) : undefined;
  const trigger = selectedTrigger === "manual" ? null : workspace.triggers.find((t) => t.trigger_id === selectedTrigger);
  const stageIds = pipeline.stages.map((s) => s.stage_id);
  const formUrl = (triggerId: string) => `/forms?workspace=${encodeURIComponent(workspaceId)}&trigger=${encodeURIComponent(triggerId)}`;
  const selectedRun = selected && run ? run.stages.find((s) => s.stage_id === selected) : undefined;

  return (
    <div className="flex h-full flex-col">
      {/* Toolbar: what the canvas shows, and the plain-language editor. */}
      <div className="space-y-2 border-b border-zinc-200 px-3 py-2 dark:border-zinc-800">
        {run ? (
          <div className="flex items-center gap-2 text-xs">
            <span className="font-medium text-zinc-900 dark:text-zinc-100">Run #{run.number}</span>
            <span className="text-zinc-500">on pipeline v{run.pipeline_version} · click a stage for its result</span>
            <Button size="sm" variant="ghost" className="ml-auto" icon={<Icons.ChevronRight className="h-3.5 w-3.5 rotate-180" />} onClick={() => { setSelected(null); onCloseRun(); }}>
              Back to the pipeline
            </Button>
          </div>
        ) : proposal ? (
          <div className="flex flex-wrap items-start gap-2 text-xs">
            <Icons.Sparkles className="mt-0.5 h-4 w-4 shrink-0 text-brand-500" />
            <div className="min-w-0 flex-1">
              <div className="font-medium text-zinc-900 dark:text-zinc-100">{proposal.summary || "Proposed changes"}</div>
              <ul className="mt-0.5 list-inside list-disc text-zinc-600 dark:text-zinc-400">
                {proposal.changes.map((c) => <li key={c}>{c}</li>)}
              </ul>
              <div className="mt-1 text-zinc-400">Previewed on the canvas: green is new, amber is changed, struck through is removed.</div>
            </div>
            <div className="flex shrink-0 gap-2">
              <Button size="sm" variant="ghost" onClick={() => setProposal(null)}>Discard</Button>
              <Button size="sm" variant="primary" icon={<Icons.Check className="h-3.5 w-3.5" />} onClick={applyProposal} disabled={busy === "apply"}>
                {busy === "apply" ? "Applying…" : "Apply"}
              </Button>
            </div>
          </div>
        ) : (
          <form onSubmit={(e) => { e.preventDefault(); void propose(request); }} className="flex items-center gap-2">
            <div className="relative min-w-0 flex-1">
              <Icons.Sparkles className="pointer-events-none absolute left-2.5 top-2 z-10 h-4 w-4 text-brand-500" />
              <MentionTextarea
                singleLine
                value={request}
                onValueChange={setRequest}
                mentionables={mentionables}
                disabled={archived || busy === "propose"}
                placeholder="Describe a change: add a security reviewer after @backend, use @default-model for @research, have @write follow @skill:… (@ to mention)"
                className={cx(inputClass, "py-1.5 pl-8 text-xs")}
              />
            </div>
            <Button type="submit" size="sm" variant="primary" disabled={!request.trim() || busy === "propose" || archived}>
              {busy === "propose" ? "Thinking…" : "Preview"}
            </Button>
            <span className="hidden text-[11px] text-zinc-400 lg:inline">v{pipeline.version}</span>
            <Button size="sm" variant="ghost" icon={<Icons.Replay className="h-3.5 w-3.5" />} onClick={openHistory} title="Earlier versions">History</Button>
            {!archived && (
              <Button size="sm" variant="ghost" icon={<Icons.Sliders className="h-3.5 w-3.5" />} title="Run settings"
                onClick={() => setSettings({ max_run_minutes: pipeline.max_run_minutes, max_concurrent_runs: pipeline.max_concurrent_runs, result_urgency: pipeline.result_urgency })}>
                <span className="sr-only">Run settings</span>
              </Button>
            )}
          </form>
        )}
        {!run && !proposal && !request && (
          <div className="flex flex-wrap gap-1.5">
            {EXAMPLES.map((ex) => (
              <button key={ex} onClick={() => setRequest(ex)} className="rounded-full border border-zinc-200 px-2 py-0.5 text-[11px] text-zinc-500 hover:border-zinc-300 hover:text-zinc-700 dark:border-zinc-800 dark:hover:text-zinc-300">
                {ex}
              </button>
            ))}
          </div>
        )}
      </div>
      {error && <div className="px-3 pt-2"><ErrorBanner error={error} onClose={() => setError(null)} /></div>}

      <div className="relative min-h-0 flex-1">
        <PipelineCanvas
          pipeline={shown}
          baseline={proposal ? pipeline : null}
          runStages={run?.stages ?? null}
          editing={editing}
          selectedStageId={selected}
          onSelectStage={(id) => { setSelected(id); setFocus(null); setSelectedTrigger(null); }}
          showParts={showParts}
          onToggleParts={toggleParts}
          models={modelChoices}
          connections={connections}
          triggers={run || proposal ? undefined : workspace.triggers}
          onSelectPart={(stageId, part) => { setSelected(stageId); setFocus(part); setSelectedTrigger(null); }}
          onSelectTrigger={(id) => { setSelectedTrigger(id); setSelected(null); }}
        />

        {/* A trigger: what it is, and for a form, where to fill it in. */}
        {selectedTrigger && !selectedStage && (
          <div className="absolute left-2 top-14 z-10 w-[min(320px,calc(100%-1rem))] rounded-xl border border-zinc-200 bg-white p-3 text-xs shadow-xl dark:border-zinc-800 dark:bg-zinc-950">
            <div className="flex items-center gap-2">
              <TriggerIcon kind={trigger?.kind ?? "Manual"} className="h-4 w-4 text-amber-600" />
              <span className="flex-1 truncate text-sm font-semibold">{trigger?.name ?? "You"}</span>
              <button onClick={() => setSelectedTrigger(null)} className="rounded p-1 text-zinc-400 hover:bg-zinc-100 dark:hover:bg-zinc-800" aria-label="Close"><Icons.X className="h-4 w-4" /></button>
            </div>
            {!trigger ? (
              <p className="mt-2 text-zinc-600 dark:text-zinc-400">A message in the workspace chat, or <b>Run</b> with an input, starts a run. Add forms, webhooks, schedules and watches on the Triggers tab.</p>
            ) : (
              <div className="mt-2 space-y-2 text-zinc-600 dark:text-zinc-400">
                {trigger.instruction && <p>{trigger.instruction}</p>}
                {trigger.kind === "Form" && trigger.form && (
                  <>
                    <p>{trigger.form.fields.length} field{trigger.form.fields.length === 1 ? "" : "s"}: {trigger.form.fields.map((f) => f.label).join(", ")}. Answers reach stages as <code>input.&lt;key&gt;</code>.</p>
                    <div className="flex gap-2">
                      <a href={formUrl(trigger.trigger_id)} target="_blank" rel="noreferrer"
                        className="inline-flex items-center gap-1 rounded-lg bg-brand-600 px-2.5 py-1.5 font-medium text-white hover:bg-brand-700">Open form</a>
                      <Button size="sm" variant="ghost" onClick={() => void navigator.clipboard?.writeText(new URL(formUrl(trigger.trigger_id), window.location.origin).toString())}>Copy link</Button>
                    </div>
                  </>
                )}
                {trigger.kind === "Webhook" && <p>Each delivery starts a run; a JSON body reaches stages as <code>input</code>.</p>}
                {trigger.kind === "Watch" && trigger.watch_summary && <p>{trigger.watch_summary}. Matching items reach stages as <code>input.matches</code>.</p>}
                <p className="text-zinc-400">{trigger.fire_count} run{trigger.fire_count === 1 ? "" : "s"} started{trigger.last_fired_at ? `, last ${ago(trigger.last_fired_at)}` : ""}.</p>
              </div>
            )}
          </div>
        )}

        {/* The selected stage: its settings, or its result in the run on screen. */}
        {selectedStage && (
          <div className="absolute inset-y-2 right-2 z-10 flex w-[min(380px,calc(100%-1rem))] flex-col overflow-hidden rounded-xl border border-zinc-200 bg-white shadow-xl dark:border-zinc-800 dark:bg-zinc-950">
            <div className="flex items-center justify-between border-b border-zinc-200 px-4 py-2.5 dark:border-zinc-800">
              <span className="truncate text-sm font-semibold">{selectedStage.name}</span>
              <button onClick={() => setSelected(null)} className="rounded p-1 text-zinc-400 hover:bg-zinc-100 dark:hover:bg-zinc-800" aria-label="Close"><Icons.X className="h-4 w-4" /></button>
            </div>
            <div className="min-h-0 flex-1 overflow-y-auto p-4">
              {run ? (
                <div className="space-y-3 text-xs">
                  {selectedRun ? (
                    <>
                      <div className="flex flex-wrap gap-x-3 gap-y-1 text-zinc-500">
                        <span>Status: <span className="font-medium text-zinc-800 dark:text-zinc-200">{selectedRun.status}{selectedRun.outcome === "partial" ? " (partial)" : ""}</span></span>
                        {selectedRun.attempts > 1 && <span>{selectedRun.attempts} attempts</span>}
                        {selectedRun.completed_at && <span>finished {ago(selectedRun.completed_at)}</span>}
                      </div>
                      {selectedRun.error && <div className="rounded-lg bg-rose-50 p-2 text-rose-800 dark:bg-rose-950/50 dark:text-rose-200">{selectedRun.error}</div>}
                      {kindOf(selectedStage) === "Agent"
                        ? selectedRun.summary && <div className="whitespace-pre-wrap rounded-lg bg-zinc-50 p-3 text-zinc-800 dark:bg-zinc-900 dark:text-zinc-200">{selectedRun.summary}</div>
                        : selectedRun.output_json && (
                          <div>
                            <div className="mb-1 font-medium text-zinc-700 dark:text-zinc-300">Output</div>
                            <Pretty json={selectedRun.output_json} />
                          </div>
                        )}
                      {selectedRun.logs && (
                        <div>
                          <div className="mb-1 font-medium text-zinc-700 dark:text-zinc-300">Printed</div>
                          <pre className="max-h-40 overflow-auto rounded-lg bg-zinc-100 p-2.5 font-mono text-[11px] text-zinc-700 dark:bg-zinc-900 dark:text-zinc-300">{selectedRun.logs}</pre>
                        </div>
                      )}
                      {selectedRun.artifacts.length > 0 && <div className="text-zinc-500">Files: {selectedRun.artifacts.join(", ")}</div>}
                      {kindOf(selectedStage) === "Agent" && selectedRun.output_json && (
                        <details>
                          <summary className="cursor-pointer select-none text-zinc-500">Output as data (for later stages)</summary>
                          <div className="mt-1"><Pretty json={selectedRun.output_json} /></div>
                        </details>
                      )}
                      {selectedRun.agent_id && (
                        <Button size="sm" onClick={() => onSelectAgent(selectedRun.agent_id!)} icon={<Icons.Graph className="h-3.5 w-3.5" />}>Agent details & trace</Button>
                      )}
                    </>
                  ) : (
                    <p className="text-zinc-500">This stage wasn&apos;t in the pipeline when the run started.</p>
                  )}
                </div>
              ) : (
                <StageEditor
                  key={`${selectedStage.stage_id}:${pipeline.version}:${focus ?? ""}`}
                  stage={selectedStage}
                  stageNames={selectedStage.inputs.map(nameOf)}
                  mentionables={mentionables}
                  workspaceId={workspaceId}
                  connections={connections}
                  stageIds={stageIds.filter((id) => id !== selectedStage.stage_id)}
                  sampleData={latestRun ? sampleFor(selectedStage, latestRun) : undefined}
                  focus={focus === "model" || focus === "tools" ? focus : null}
                  submitLabel="Save stage"
                  busy={busy === "edit"}
                  onSubmit={(patch) => updateStage(selectedStage.stage_id, patch)}
                  onCancel={() => setSelected(null)}
                  onRemove={editable ? () => onRemove(selectedStage.stage_id) : undefined}
                />
              )}
            </div>
          </div>
        )}
      </div>

      <Modal
        open={inserting !== null}
        onClose={() => setInserting(null)}
        title="Add a stage"
        description={inserting
          ? inserting.after && inserting.before ? `Between ${nameOf(inserting.after)} and ${nameOf(inserting.before)}.`
            : inserting.after ? `After ${nameOf(inserting.after)}.`
            : inserting.before ? `Before ${nameOf(inserting.before)}.`
            : inserting.inputs?.length ? `Takes ${inserting.inputs.map(nameOf).join(", ")}'s result. Connect it onward by dragging from its right dot.`
            : "Where you dropped it. Connect it by dragging between the stages' dots."
          : undefined}
      >
        {inserting && (
          <StageEditor stage={newStage(inserting.kind)} submitLabel="Add stage" busy={busy === "edit"} onSubmit={addStage} onCancel={() => setInserting(null)}
            mentionables={mentionables} workspaceId={workspaceId} connections={connections} stageIds={stageIds}
            sampleData={latestRun ? sampleFor({ inputs: inserting.inputs ?? (inserting.after ? [inserting.after] : []) }, latestRun) : undefined} />
        )}
      </Modal>

      <Modal open={settings !== null} onClose={() => setSettings(null)} title="Run settings" description="Apply to every run of this pipeline."
        footer={<><Button variant="ghost" onClick={() => setSettings(null)}>Cancel</Button><Button variant="primary" onClick={saveSettings}>Save</Button></>}>
        {settings && (
          <div className="space-y-3 text-sm">
            <div className="grid grid-cols-2 gap-3">
              <Field label="Time limit per run (minutes)" hint="Stages still running then are stopped.">
                <input type="number" min={1} max={240} className={inputClass} value={settings.max_run_minutes}
                  onChange={(e) => setSettings({ ...settings, max_run_minutes: Math.max(1, Math.min(240, Number(e.target.value) || 1)) })} />
              </Field>
              <Field label="Runs at once" hint="More wait in a queue.">
                <input type="number" min={1} max={5} className={inputClass} value={settings.max_concurrent_runs}
                  onChange={(e) => setSettings({ ...settings, max_concurrent_runs: Math.max(1, Math.min(5, Number(e.target.value) || 1)) })} />
              </Field>
            </div>
            <Field label="Result urgency" hint="Connected channels (SMS, Slack, email) forward results at or above their level. Failures are at least warnings.">
              <select className={inputClass} value={settings.result_urgency} onChange={(e) => setSettings({ ...settings, result_urgency: e.target.value as PipelineDefinition["result_urgency"] })}>
                <option value="info">Info: the chat only, unless a channel forwards everything</option>
                <option value="warning">Warning</option>
                <option value="urgent">Urgent: alert the on-call person</option>
              </select>
            </Field>
          </div>
        )}
      </Modal>

      <Modal open={history !== null} onClose={() => setHistory(null)} title="Pipeline history" description={`Now at version ${pipeline.version}. Restoring makes an earlier version current again, as a new version.`}>
        <ul className="max-h-[60vh] divide-y divide-zinc-200 overflow-y-auto text-sm dark:divide-zinc-800">
          <li className="flex items-center gap-3 py-2">
            <span className="w-10 font-mono text-xs text-zinc-400">v{pipeline.version}</span>
            <span className="min-w-0 flex-1"><span className="block truncate">{pipeline.note}</span><span className="text-xs text-zinc-400">{ago(pipeline.updated_at)} · current</span></span>
          </li>
          {(history ?? []).map((p) => (
            <li key={p.version} className="flex items-center gap-3 py-2">
              <span className="w-10 font-mono text-xs text-zinc-400">v{p.version}</span>
              <span className="min-w-0 flex-1">
                <span className="block truncate">{p.note}</span>
                <span className="text-xs text-zinc-400">{ago(p.updated_at)} · {p.stages.length} stage{p.stages.length === 1 ? "" : "s"}</span>
              </span>
              {!archived && <Button size="sm" onClick={() => restore(p.version)}>Restore</Button>}
            </li>
          ))}
          {history?.length === 0 && <li className="py-3 text-xs text-zinc-500">No earlier versions yet.</li>}
        </ul>
      </Modal>
    </div>
  );
}
