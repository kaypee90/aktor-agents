"use client";

import { useState } from "react";
import { Button, Field, Toggle, cx, inputClass } from "@/components/ui";
import { CAPABILITIES, type PipelineStage, type StagePatch } from "@/lib/pipelineTypes";
import { MentionTextarea, useModelChoices, type Mentionable } from "@/components/ui/MentionTextarea";

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
};

/**
 * A stage's settings: who the agent is, what it does, its tools, and how much freedom it has
 * (helpers, messaging other stages, retries). The runtime checks every value when it's saved.
 */
export function StageEditor({ stage, stageNames, submitLabel, onSubmit, onCancel, onRemove, busy, mentionables = [] }: {
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
}) {
  const [draft, setDraft] = useState(stage);
  const models = useModelChoices();
  const set = <K extends keyof PipelineStage>(key: K, value: PipelineStage[K]) => setDraft((d) => ({ ...d, [key]: value }));
  const caps = new Set(draft.capabilities ?? []);
  const toggleCap = (id: string) => {
    const next = new Set(caps);
    if (next.has(id)) next.delete(id); else next.add(id);
    set("capabilities", [...next]);
  };
  const valid = draft.name.trim().length > 0 && (draft.instructions ?? "").trim().length > 0;

  return (
    <form
      onSubmit={(e) => { e.preventDefault(); if (valid) onSubmit({ ...draft, name: draft.name.trim(), role: draft.role?.trim() || draft.name.trim() }); }}
      className="space-y-3 text-sm"
    >
      {stageNames && stageNames.length > 0 && (
        <p className="text-xs text-zinc-500">Takes input from: {stageNames.join(", ")}</p>
      )}
      <div className="grid grid-cols-2 gap-2">
        <Field label="Name">
          <input className={inputClass} value={draft.name} onChange={(e) => set("name", e.target.value)} placeholder="e.g. Security review" autoFocus />
        </Field>
        <Field label="Role">
          <input className={inputClass} value={draft.role ?? ""} onChange={(e) => set("role", e.target.value)} placeholder="e.g. Security reviewer" />
        </Field>
      </div>
      <Field label="Instructions" hint="What the agent does with the run's input and earlier stages' results, for whom, to what standard.">
        <MentionTextarea className={cx(inputClass, "min-h-28 resize-y")} value={draft.instructions ?? ""} onValueChange={(v) => set("instructions", v)}
          mentionables={mentionables.filter((m) => m.kind === "agent" || m.kind === "skill")}
          placeholder="Review @backend's changes for security issues (auth, injection, secrets) and list what must be fixed before release. Type @ to mention a stage or a skill: the agent loads a mentioned skill first." />
      </Field>
      {models.length > 1 && (
        <Field label="Model" hint="A stronger model for hard stages, a cheaper one for simple ones.">
          <select className={inputClass} value={draft.model_profile_id ?? ""}
            onChange={(e) => set("model_profile_id", e.target.value)}>
            <option value="">The workspace&apos;s model</option>
            {models.map((m) => <option key={m.id} value={m.id}>{m.name} ({m.provider} · {m.model})</option>)}
          </select>
        </Field>
      )}
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
        <p className="mt-1 text-[11px] text-zinc-400">Every stage can read and write the run&apos;s files and use the workspace&apos;s connections.</p>
      </Field>
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
