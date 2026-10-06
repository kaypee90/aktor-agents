"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import Link from "next/link";
import { apiErrorMessage, createTask, getLlmSettings, previewTask, uploadFiles, type CreateTaskInput, type LlmSettingsView } from "@/lib/api";
import { AttachButton, DropZone, PendingFiles, useAttachments } from "@/components/files/Attachments";
import { ModelPicker } from "./ModelPicker";
import type { TaskPreview } from "@/lib/types";
import { TaskPreviewCard } from "@/components/TaskPreviewCard";
import { Button, ErrorBanner, Field, Modal, Toggle, cx, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { DEFAULT_MODEL_HANDLE, MentionTextarea, mentionsIn, useModelMentionables, useSkillMentionables } from "@/components/ui/MentionTextarea";

const STARTER_KEY = "aktor:starterGoal";

const EXAMPLES: { title: string; prompt: string; icon: keyof typeof Icons }[] = [
  {
    title: "Research report",
    icon: "Book",
    prompt: "Research whether we should build an AI-powered property management SaaS. Produce a market, technical and business analysis as a Word document.",
  },
  {
    title: "Spreadsheet model",
    icon: "Analytics",
    prompt: "Build a 3-year revenue model for a SaaS with three pricing tiers, with the assumptions on their own sheet, as an Excel workbook.",
  },
  {
    title: "Slide deck",
    icon: "Sparkles",
    prompt: "Create a 10-slide pitch deck for a property management startup: problem, solution, market, business model, competition, team and the ask.",
  },
  {
    title: "Analyze my files",
    icon: "File",
    prompt: "Analyze the attached files and give me the key insights, the risks and recommended next steps.",
  },
];

/** An MCP server to connect for the task's agents before it starts. */
type McpServer = { name: string; url: string; token: string };

type Options = {
  maxCost: string; maxTokens: string; minutes: string;
  maxAgents: string; fanOut: string; spawners: string; noDuplicates: boolean; goalType: string;
  callbackUrl: string; callbackSecret: string; correlationId: string;
  mcp: McpServer[];
};

const EMPTY: Options = {
  maxCost: "", maxTokens: "", minutes: "",
  maxAgents: "", fanOut: "", spawners: "", noDuplicates: true, goalType: "",
  callbackUrl: "", callbackSecret: "", correlationId: "",
  mcp: [],
};

const num = (s: string) => (s.trim() === "" || Number.isNaN(Number(s)) ? undefined : Number(s));

/** The options form as the API's request; empty fields are left to the server's defaults. */
function toInput(goal: string, o: Options, previewId?: string): CreateTaskInput {
  const budget = {
    max_cost_usd: num(o.maxCost),
    max_tokens: num(o.maxTokens),
    max_duration_seconds: num(o.minutes) !== undefined ? num(o.minutes)! * 60 : undefined,
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
    connections: o.mcp.filter((m) => m.url.trim()).map((m, i) => ({
      plugin_id: "mcp",
      name: m.name.trim() || `mcp${o.mcp.length > 1 ? i + 1 : ""}`,
      settings: { transport: "http", url: m.url.trim() },
      secrets: (m.token.trim() ? { auth_header_value: m.token.trim() } : {}) as Record<string, string>,
    })),
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

  const modelMentions = useModelMentionables();
  const skillMentions = useSkillMentionables();
  const goalMentions = useMemo(() => [...modelMentions, ...skillMentions], [modelMentions, skillMentions]);
  // "@claude-fast" in the goal picks that model; "@anthropic" picks its first model if none is picked.
  function goalChanged(text: string) {
    setGoal(text);
    const handles = mentionsIn(text).map((h) => h.toLowerCase());
    const profiles = models ? [{ id: "server", provider: models.server.provider }, ...models.profiles] : [];
    const named = profiles.find((p) => handles.includes(p.id === "server" ? DEFAULT_MODEL_HANDLE : p.id.toLowerCase()));
    const byProvider = !model ? profiles.find((p) => handles.includes(p.provider.toLowerCase())) : undefined;
    const pick = named ?? byProvider;
    if (pick && pick.id !== model) { setModel(pick.id); setEstimate(null); }
  }

  const upload = useCallback(async (file: File) => (await uploadFiles([file]))[0].upload_id, []);
  const files = useAttachments(upload);
  const box = useRef<HTMLTextAreaElement>(null);
  const canRun = (goal.trim().length > 0 || files.ids.length > 0) && busy === null && !files.uploading;

  const set = <K extends keyof Options>(k: K, v: Options[K]) => setOptions((o) => ({ ...o, [k]: v }));
  const budgetForPreview = () => toInput(goal, options).budget;

  async function start(preview: TaskPreview | null) {
    setBusy("starting");
    try {
      const { task_id } = await createTask({ ...toInput(goal.trim(), options, preview?.preview_id), model: model || null, attachments: files.ids });
      setEstimate(null);
      setGoal("");
      files.clear();
      onStarted(task_id, preview);
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  async function run(e?: React.FormEvent) {
    e?.preventDefault();
    if (!canRun) return;
    setError(null);
    setBusy("estimating");
    let preview: TaskPreview | null = null;
    try {
      // Files alone get a default goal on the server; there's nothing to plan from yet.
      if (goal.trim()) preview = await previewTask(goal.trim(), budgetForPreview(), model || null);
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
    <div>
      <DropZone onFiles={files.add}>
      <div className="overflow-hidden rounded-3xl border border-zinc-200 bg-white shadow-sm transition focus-within:border-zinc-300 focus-within:shadow-md dark:border-zinc-800 dark:bg-zinc-900 dark:focus-within:border-zinc-700">
      <form onSubmit={run}>
        {files.pending.length > 0 && <div className="px-4 pt-3"><PendingFiles pending={files.pending} onRemove={files.remove} /></div>}
        <MentionTextarea
          ref={box}
          value={goal}
          onValueChange={goalChanged}
          mentionables={goalMentions}
          onInput={(e) => {
            const el = e.currentTarget;
            el.style.height = "auto";
            el.style.height = `${Math.min(el.scrollHeight, 320)}px`;
          }}
          onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); run(); } }}
          onPaste={(e) => { const pasted = Array.from(e.clipboardData.files); if (pasted.length > 0) { e.preventDefault(); files.add(pasted); } }}
          rows={3}
          placeholder="Ask for anything: a report, an analysis, a spreadsheet, a slide deck… Attach files for context, and type @ to pick a model or a skill."
          className="block max-h-80 w-full resize-none border-0 bg-transparent px-5 pt-4 text-[15px] text-zinc-900 outline-none placeholder:text-zinc-400 dark:text-zinc-100 dark:placeholder:text-zinc-600"
          disabled={busy !== null}
        />

        <div className="flex flex-wrap items-center gap-1 px-3 pb-3">
          <AttachButton onFiles={files.add} />
          <button type="button" onClick={() => setShowOptions((v) => !v)} title="Budget, team and delivery options"
            className={cx("inline-flex items-center gap-1.5 rounded-lg px-2 py-2 text-xs font-medium hover:bg-zinc-100 dark:hover:bg-zinc-800",
              showOptions ? "text-brand-700 dark:text-brand-300" : "text-zinc-500")}>
            <Icons.Sliders className="h-4 w-4" />
            <span className="hidden sm:inline">Options</span>
          </button>
          {models && (
            <div className="flex min-w-0 items-center gap-1">
              <ModelPicker view={models} value={model} onChange={(id) => { setModel(id); setEstimate(null); }} />
              <Link href="/settings?tab=model" title="Add or change models" className="rounded-md p-1 text-zinc-400 hover:bg-zinc-100 hover:text-zinc-700 dark:hover:bg-zinc-800 dark:hover:text-zinc-200">
                <Icons.Settings className="h-3.5 w-3.5" />
              </Link>
            </div>
          )}
          <div className="ml-auto flex items-center gap-2">
            <button type="button" onClick={estimateOnly} disabled={!goal.trim() || busy !== null}
              className="hidden rounded-lg px-2 py-1.5 text-xs font-medium text-zinc-500 hover:bg-zinc-100 hover:text-zinc-800 disabled:opacity-40 sm:inline dark:hover:bg-zinc-800 dark:hover:text-zinc-200">
              {busy === "estimating" && !estimate ? "Estimating…" : "Estimate cost"}
            </button>
            <button type="submit" disabled={!canRun} aria-label="Run task" title="Run (Enter)"
              className="flex h-9 w-9 items-center justify-center rounded-full bg-zinc-900 text-white transition hover:bg-zinc-700 disabled:bg-zinc-200 disabled:text-zinc-400 dark:bg-zinc-100 dark:text-zinc-900 dark:hover:bg-white dark:disabled:bg-zinc-800 dark:disabled:text-zinc-600">
              {busy !== null || files.uploading
                ? <span className="h-3 w-3 animate-spin rounded-full border-2 border-current border-t-transparent" />
                : <Icons.ArrowUp className="h-4 w-4" />}
            </button>
          </div>
        </div>

        {showOptions && (
          <div className="border-t border-zinc-200 bg-zinc-50/60 px-5 py-4 dark:border-zinc-800 dark:bg-zinc-950/40">
            <div className="mb-2 flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wide text-zinc-500">
              <Icons.Connect className="h-3.5 w-3.5" /> MCP servers
              <span className="font-normal normal-case tracking-normal text-zinc-400">: tools this task&apos;s agents can use; connected before it starts</span>
            </div>
            <div className="space-y-2">
              {options.mcp.map((m, i) => (
                <div key={i} className="grid gap-2 sm:grid-cols-[8rem_1fr_12rem_auto]">
                  <input className={inputClass} placeholder="name (e.g. github)" value={m.name}
                    onChange={(e) => set("mcp", options.mcp.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} />
                  <input className={inputClass} placeholder="https://example.com/mcp" value={m.url}
                    onChange={(e) => set("mcp", options.mcp.map((x, j) => (j === i ? { ...x, url: e.target.value } : x)))} />
                  <input className={inputClass} type="password" autoComplete="off" placeholder="Bearer token (optional)" value={m.token}
                    onChange={(e) => set("mcp", options.mcp.map((x, j) => (j === i ? { ...x, token: e.target.value } : x)))} />
                  <button type="button" onClick={() => set("mcp", options.mcp.filter((_, j) => j !== i))} aria-label="Remove MCP server"
                    className="rounded-md px-2 text-zinc-400 hover:bg-zinc-100 hover:text-zinc-700 dark:hover:bg-zinc-800">
                    <Icons.X className="h-4 w-4" />
                  </button>
                </div>
              ))}
              <button type="button" onClick={() => set("mcp", [...options.mcp, { name: "", url: "", token: "" }])}
                className="text-xs font-medium text-brand-600 hover:underline dark:text-brand-400">
                + Add an MCP server
              </button>
              <p className="text-[11px] text-zinc-400">You can also connect servers (and switch their tools on and off) from the task&apos;s Tools button while it runs.</p>
            </div>
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
              <Field label="Max time (minutes)" hint="Wall-clock limit for the whole team.">
                <input className={inputClass} inputMode="numeric" value={options.minutes} onChange={(e) => set("minutes", e.target.value)} placeholder="15" />
              </Field>
            </fieldset>

            <fieldset className="space-y-3">
              <legend className="mb-2 flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wide text-zinc-500"><Icons.Sliders className="h-3.5 w-3.5" /> Team shape</legend>
              <div className="grid grid-cols-2 gap-2">
                <Field label="Max agents">
                  <input className={inputClass} inputMode="numeric" value={options.maxAgents} onChange={(e) => set("maxAgents", e.target.value)} placeholder="No limit" />
                </Field>
                <Field label="Fan-out per level" hint="Sub-agents each agent may start, root first. e.g. 3, 0">
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

      </form>
      </div>
      </DropZone>

      {error && <div className="mt-3"><ErrorBanner error={error} onClose={() => setError(null)} /></div>}

      {!goal && files.pending.length === 0 && (
        <div className="mt-6 grid gap-3 sm:grid-cols-2">
          {EXAMPLES.map((ex) => {
            const Icon = Icons[ex.icon] as (p: { className?: string }) => React.ReactElement;
            return (
              <button key={ex.title} type="button"
                onClick={() => { setGoal(ex.prompt); requestAnimationFrame(() => box.current?.focus()); }}
                className="group rounded-2xl border border-zinc-200 bg-white p-4 text-left transition hover:border-brand-300 hover:shadow-sm dark:border-zinc-800 dark:bg-zinc-900 dark:hover:border-brand-800">
                <span className="flex items-center gap-2 text-sm font-medium text-zinc-900 dark:text-zinc-100">
                  <Icon className="h-4 w-4 text-brand-500" /> {ex.title}
                </span>
                <span className="mt-1 line-clamp-2 block text-xs text-zinc-500">{ex.prompt}</span>
              </button>
            );
          })}
        </div>
      )}

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
    </div>
  );
}
