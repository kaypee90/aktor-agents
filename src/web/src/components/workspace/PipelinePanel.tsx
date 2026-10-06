"use client";

import { useCallback, useMemo, useState } from "react";
import { Button, ErrorBanner, Field, Modal, ago, cx, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { apiErrorMessage, applyPipelineEdits, getPipelineHistory, proposePipelineChange, restorePipelineVersion, savePipeline, savePipelineLayout } from "@/lib/api";
import type { PipelineDefinition, PipelineEditOp, PipelineProposal, PipelineRunView, StagePatch, StagePosition } from "@/lib/pipelineTypes";
import { MentionTextarea, useModelMentionables, useWorkspaceMentionables } from "@/components/ui/MentionTextarea";
import type { WorkspaceSnapshot } from "@/lib/workspaceTypes";
import { PipelineCanvas, type CanvasEditing } from "./PipelineCanvas";
import { NEW_STAGE, StageEditor } from "./StageEditor";

const EXAMPLES = ["Add a fact checker after Research", "Add a reviewer before the last stage", "Remove the reviewer"];

/** Where a stage being added goes: on a connection or at an end, or where it was dropped on the canvas. */
type Insertion = { after?: string; before?: string; position?: StagePosition; inputs?: string[] };

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
    const { inputs, ...where } = inserting;
    setBusy("edit");
    if (await apply([{ op: "add_stage", stage: inputs ? { ...stage, inputs } : stage, ...where }], pipeline.version)) setInserting(null);
    setBusy(null);
  }

  const editing = useMemo<CanvasEditing | null>(() => !editable ? null : {
    onInsert,
    onRemove,
    onConnect: (from, to) => void apply([{ op: "connect", from, to }], pipeline.version),
    onDisconnect: (from, to) => void apply([{ op: "disconnect", from, to }], pipeline.version),
    onAddAt: (position, inputs) => setInserting({ position, inputs }),
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
          onSelectStage={setSelected}
        />

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
                      {selectedRun.summary && <div className="whitespace-pre-wrap rounded-lg bg-zinc-50 p-3 text-zinc-800 dark:bg-zinc-900 dark:text-zinc-200">{selectedRun.summary}</div>}
                      {selectedRun.artifacts.length > 0 && <div className="text-zinc-500">Files: {selectedRun.artifacts.join(", ")}</div>}
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
                  key={`${selectedStage.stage_id}:${pipeline.version}`}
                  stage={selectedStage}
                  stageNames={selectedStage.inputs.map(nameOf)}
                  mentionables={mentionables}
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
          <StageEditor stage={NEW_STAGE} submitLabel="Add stage" busy={busy === "edit"} onSubmit={addStage} onCancel={() => setInserting(null)} mentionables={mentionables} />
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
