"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { apiErrorMessage, createTask, getLlmSettings, previewTask, type CreateTaskInput, type LlmSettingsView } from "@/lib/api";
import { ModelPicker } from "./ModelPicker";
import type { TaskPreview } from "@/lib/types";
import { TaskPreviewCard } from "@/components/TaskPreviewCard";
import { Button, Card, ErrorBanner, Field, Modal, Toggle, cx, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

const STARTER_KEY = "aktor:starterGoal";

const EXAMPLES = [
  "Research whether we should build an AI-powered property management SaaS. Produce a market, technical and business analysis.",
  "Add user authentication to this application: design, security review, backend, frontend and tests.",
  "Compare the top five open-source vector databases for a 50M-document search product and recommend one.",
];

type Options = {
  maxCost: string; maxTokens: string; minutes: string; maxChildren: string;
  maxAgents: string; fanOut: string; spawners: string; noDuplicates: boolean; goalType: string;
  callbackUrl: string; callbackSecret: string; correlationId: string;
};

const EMPTY: Options = {
  maxCost: "", maxTokens: "", minutes: "", maxChildren: "",
  maxAgents: "", fanOut: "", spawners: "", noDuplicates: true, goalType: "",
  callbackUrl: "", callbackSecret: "", correlationId: "",
};

const num = (s: string) => (s.trim() === "" || Number.isNaN(Number(s)) ? undefined : Number(s));

/** The options form as the API's request; empty fields are left to the server's defaults. */
function toInput(goal: string, o: Options, previewId?: string): CreateTaskInput {
  const budget = {
    max_cost_usd: num(o.maxCost),
    max_tokens: num(o.maxTokens),
    max_duration_seconds: num(o.minutes) !== undefined ? num(o.minutes)! * 60 : undefined,
    max_children: num(o.maxChildren),
  };
  const fanOut = o.fanOut.split(/[,\s]+/).map(Number).filter((n) => !Number.isNaN(n) && n >= 0 && o.fanOut.trim() !== "");
  const spawners = o.spawners.split(",").map((s) => s.trim()).filter(Boolean);
  const teamPolicy = {
    max_agents: num(o.maxAgents) ?? null,
    max_fan_out_by_depth: fanOut,
    spawner_roles: spawners,
    prevent_duplicate_roles: o.noDuplicates,
    goal_type: o.goalType.trim() || null,
  };
  const hasPolicy = teamPolicy.max_agents !== null || fanOut.length > 0 || spawners.length > 0 || !o.noDuplicates || teamPolicy.goal_type;
  return {
    goal,
    budget: Object.values(budget).some((v) => v !== undefined) ? budget : undefined,
    preview_id: previewId,
    callback_url: o.callbackUrl.trim() || undefined,
    callback_secret: o.callbackSecret.trim() || undefined,
    correlation_id: o.correlationId.trim() || undefined,
    team_policy: hasPolicy ? teamPolicy : undefined,
  };
}

/**
 * Starts a task. Every run is previewed first (team shape and cost range); under the server's
 * confirmation threshold it starts straight away, above it the estimate is shown for approval.
 * Advanced options cover the budget, team-shape rules and delivery (webhook, correlation id).
 */
export function TaskComposer({ onStarted }: { onStarted: (taskId: string, preview: TaskPreview | null) => void }) {
  // A starter picked on the Templates page arrives through session storage.
  const [goal, setGoal] = useState(() => {
    try {
      return typeof window === "undefined" ? "" : sessionStorage.getItem(STARTER_KEY) ?? "";
    } catch {
      return "";
    }
  });
  useEffect(() => {
    try {
      sessionStorage.removeItem(STARTER_KEY);
    } catch {
      // Nothing to clear.
    }
  }, []);
  // Which model the team will use, with a way to change it (docs/llm-settings.md).
  // Which model the team runs on: the organization's default unless one is picked (docs/llm-settings.md).
  const [models, setModels] = useState<LlmSettingsView | null>(null);
  const [model, setModel] = useState("");
  useEffect(() => {
    getLlmSettings().then(setModels).catch(() => { /* the picker is optional */ });
  }, []);
  const [options, setOptions] = useState<Options>(EMPTY);
  const [showOptions, setShowOptions] = useState(false);
  const [busy, setBusy] = useState<"estimating" | "starting" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [estimate, setEstimate] = useState<{ preview: TaskPreview; mustConfirm: boolean } | null>(null);

  const set = <K extends keyof Options>(k: K, v: Options[K]) => setOptions((o) => ({ ...o, [k]: v }));
  const budgetForPreview = () => toInput(goal, options).budget;

  async function start(preview: TaskPreview | null) {
    setBusy("starting");
    try {
      const { task_id } = await createTask({ ...toInput(goal.trim(), options, preview?.preview_id), model: model || null });
      setEstimate(null);
      setGoal("");
      onStarted(task_id, preview);
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  async function run(e?: React.FormEvent) {
    e?.preventDefault();
    if (!goal.trim() || busy) return;
    setError(null);
    setBusy("estimating");
    let preview: TaskPreview | null = null;
    try {
      preview = await previewTask(goal.trim(), budgetForPreview(), model || null);
    } catch {
      // The preview is advice: if it fails, start without one.
    }
    if (preview?.requires_confirmation) {
      setEstimate({ preview, mustConfirm: true });
      setBusy(null);
      return;
    }
    await start(preview);
  }

  async function estimateOnly() {
    if (!goal.trim() || busy) return;
    setError(null);
    setBusy("estimating");
    try {
      setEstimate({ preview: await previewTask(goal.trim(), budgetForPreview(), model || null), mustConfirm: false });
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  return (
    <Card className="overflow-hidden">
      <form onSubmit={run}>
        <textarea
          value={goal}
          onChange={(e) => setGoal(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) run(); }}
          rows={4}
          placeholder="Describe the outcome you want. The team plans the work, starts the specialists it needs and reports back."
          className="block w-full resize-none border-0 bg-transparent px-5 pt-4 text-[15px] text-zinc-900 outline-none placeholder:text-zinc-400 dark:text-zinc-100 dark:placeholder:text-zinc-600"
          disabled={busy !== null}
        />

        {!goal && (
          <div className="flex flex-wrap gap-1.5 px-5 pb-3">
            {EXAMPLES.map((ex) => (
              <button key={ex} type="button" onClick={() => setGoal(ex)}
                className="max-w-xs truncate rounded-full border border-zinc-200 px-2.5 py-1 text-[11px] text-zinc-600 hover:border-brand-300 hover:text-brand-700 dark:border-zinc-800 dark:text-zinc-400 dark:hover:border-brand-800 dark:hover:text-brand-300">
                {ex}
              </button>
            ))}
          </div>
        )}

        {showOptions && (
          <div className="grid gap-6 border-t border-zinc-200 bg-zinc-50/60 px-5 py-5 md:grid-cols-3 dark:border-zinc-800 dark:bg-zinc-950/40">
            <fieldset className="space-y-3">
              <legend className="mb-2 flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wide text-zinc-500"><Icons.Bolt className="h-3.5 w-3.5" /> Budget</legend>
              <Field label="Max cost (USD)" hint="The whole team; capped by the server's ceiling.">
                <input className={inputClass} inputMode="decimal" value={options.maxCost} onChange={(e) => set("maxCost", e.target.value)} placeholder="Server default" />
              </Field>
              <Field label="Max tokens">
                <input className={inputClass} inputMode="numeric" value={options.maxTokens} onChange={(e) => set("maxTokens", e.target.value)} placeholder="Server default" />
              </Field>
              <div className="grid grid-cols-2 gap-2">
                <Field label="Time limit (min)">
                  <input className={inputClass} inputMode="numeric" value={options.minutes} onChange={(e) => set("minutes", e.target.value)} placeholder="15" />
                </Field>
                <Field label="Direct sub-agents">
                  <input className={inputClass} inputMode="numeric" value={options.maxChildren} onChange={(e) => set("maxChildren", e.target.value)} placeholder="5" />
                </Field>
              </div>
            </fieldset>

            <fieldset className="space-y-3">
              <legend className="mb-2 flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wide text-zinc-500"><Icons.Sliders className="h-3.5 w-3.5" /> Team shape</legend>
              <div className="grid grid-cols-2 gap-2">
                <Field label="Max agents">
                  <input className={inputClass} inputMode="numeric" value={options.maxAgents} onChange={(e) => set("maxAgents", e.target.value)} placeholder="No limit" />
                </Field>
                <Field label="Fan-out per level" hint="e.g. 3, 2, 0">
                  <input className={inputClass} value={options.fanOut} onChange={(e) => set("fanOut", e.target.value)} placeholder="Any" />
                </Field>
              </div>
              <Field label="Roles that may spawn" hint="Comma-separated; * wildcards. Empty: any role.">
                <input className={inputClass} value={options.spawners} onChange={(e) => set("spawners", e.target.value)} placeholder="Root Agent, *architect*" />
              </Field>
              <Field label="Goal type" hint="Matches the server's per-type limits.">
                <input className={inputClass} value={options.goalType} onChange={(e) => set("goalType", e.target.value)} placeholder="research, coding…" />
              </Field>
              <Toggle checked={options.noDuplicates} onChange={(v) => set("noDuplicates", v)} label="No duplicate roles"
                description="Refuse a second live agent doing the same job." />
            </fieldset>

            <fieldset className="space-y-3">
              <legend className="mb-2 flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wide text-zinc-500"><Icons.Link className="h-3.5 w-3.5" /> Delivery</legend>
              <Field label="Completion webhook" hint="POSTed the result when the task finishes.">
                <input className={inputClass} value={options.callbackUrl} onChange={(e) => set("callbackUrl", e.target.value)} placeholder="https://…" />
              </Field>
              <Field label="Webhook secret" hint="Signs deliveries: X-Aktor-Signature.">
                <input className={inputClass} type="password" value={options.callbackSecret} onChange={(e) => set("callbackSecret", e.target.value)} />
              </Field>
              <Field label="Correlation ID" hint="Your id for this run, stamped on its events.">
                <input className={inputClass} value={options.correlationId} onChange={(e) => set("correlationId", e.target.value)} placeholder="Generated" />
              </Field>
            </fieldset>
          </div>
        )}

        <div className="flex flex-wrap items-center justify-between gap-2 border-t border-zinc-200 px-4 py-3 dark:border-zinc-800">
          <button type="button" onClick={() => setShowOptions((v) => !v)}
            className="inline-flex items-center gap-1.5 rounded-lg px-2 py-1 text-xs font-medium text-zinc-600 hover:bg-zinc-100 dark:text-zinc-400 dark:hover:bg-zinc-800">
            <Icons.Sliders className="h-3.5 w-3.5" />
            {showOptions ? "Hide options" : "Budget, team & delivery"}
            <Icons.ChevronDown className={cx("h-3 w-3 transition-transform", showOptions && "rotate-180")} />
          </button>
          {models && (
            <div className="mr-auto flex min-w-0 items-center gap-1">
              <ModelPicker view={models} value={model} onChange={(id) => { setModel(id); setEstimate(null); }} />
              <Link href="/settings?tab=model" title="Add or change models" className="rounded-md p-1 text-zinc-400 hover:bg-zinc-100 hover:text-zinc-700 dark:hover:bg-zinc-800 dark:hover:text-zinc-200">
                <Icons.Settings className="h-3.5 w-3.5" />
              </Link>
            </div>
          )}
          <div className="flex items-center gap-2">
            <span className="hidden text-[11px] text-zinc-400 sm:inline">Ctrl + Enter to run</span>
            <Button type="button" onClick={estimateOnly} disabled={!goal.trim() || busy !== null} icon={<Icons.Sparkles className="h-3.5 w-3.5" />}>
              {busy === "estimating" && !estimate ? "Estimating…" : "Estimate cost"}
            </Button>
            <Button type="submit" variant="primary" disabled={!goal.trim() || busy !== null} icon={<Icons.Play className="h-3.5 w-3.5" />}>
              {busy === "starting" ? "Starting…" : "Run task"}
            </Button>
          </div>
        </div>
      </form>

      {error && <div className="px-4 pb-3"><ErrorBanner error={error} onClose={() => setError(null)} /></div>}

      <Modal
        open={estimate !== null}
        onClose={() => setEstimate(null)}
        wide
        title={estimate?.mustConfirm ? "This task could be costly" : "Cost and team estimate"}
        description={estimate?.mustConfirm
          ? `The high estimate is above the $${estimate.preview.confirm_above_usd.toFixed(2)} confirmation threshold. The budget below is what the runtime enforces.`
          : "One planning call's view of the team the root agent is likely to build, and what it should cost."}
        footer={
          <>
            <Button onClick={() => setEstimate(null)}>Cancel</Button>
            <Button variant="primary" disabled={busy !== null} onClick={() => estimate && start(estimate.preview)} icon={<Icons.Play className="h-3.5 w-3.5" />}>
              {busy === "starting" ? "Starting…" : `Run (budget $${estimate?.preview.budget.max_cost_usd.toFixed(2)})`}
            </Button>
          </>
        }
      >
        {estimate && <TaskPreviewCard preview={estimate.preview} />}
      </Modal>
    </Card>
  );
}
