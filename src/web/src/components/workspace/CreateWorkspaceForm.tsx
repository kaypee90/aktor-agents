"use client";

import { useEffect, useState } from "react";
import { createWorkspace, createWorkspaceFromTemplate, listWorkspaceTemplates } from "@/lib/api";
import type { WorkspaceTemplate } from "@/lib/workspaceTypes";

// Deliberately varied: workspaces are generic, and these only seed the form.
const EXAMPLES = [
  { name: "Morning briefing", goal: "Every weekday at 08:00 UTC, research the latest news in my industry and send me a five-bullet summary." },
  { name: "Support inbox", goal: "Create a webhook for new support tickets. Classify each by urgency, draft a reply, and alert me immediately about urgent ones." },
  { name: "Sales follow-ups", goal: "Each afternoon, check my CRM for deals with no activity in 7 days and draft follow-up emails for me to review." },
  { name: "Ops watchdog", goal: "Watch our status API every 5 minutes and alert me by SMS if any service reports an error." },
  { name: "Research project", goal: "Research the market for AI-powered property management software and produce a report with competitors and pricing." },
  { name: "Shop inventory", goal: "Check my store's inventory every hour and alert me when any product drops below 10 units." },
];

export function CreateWorkspaceForm({ onCreated }: { onCreated: (id: string) => void }) {
  const [name, setName] = useState(EXAMPLES[0].name);
  const [goal, setGoal] = useState(EXAMPLES[0].goal);
  const [tokens, setTokens] = useState(500_000);
  const [dollars, setDollars] = useState(2);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [templates, setTemplates] = useState<WorkspaceTemplate[]>([]);
  const [useDemoSystem, setUseDemoSystem] = useState(true);

  useEffect(() => {
    listWorkspaceTemplates().then(setTemplates).catch(() => setTemplates([]));
  }, []);

  async function fromTemplate(t: WorkspaceTemplate) {
    setBusy(true);
    setError(null);
    try {
      const { workspace_id } = await createWorkspaceFromTemplate(t.id, undefined, useDemoSystem);
      onCreated(workspace_id);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to create workspace");
    } finally {
      setBusy(false);
    }
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const { workspace_id } = await createWorkspace({ name, goal, daily_token_limit: tokens, daily_cost_limit_usd: dollars });
      onCreated(workspace_id);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to create workspace");
    } finally {
      setBusy(false);
    }
  }

  const input = "w-full rounded border border-zinc-300 bg-white px-2 py-1.5 text-sm dark:border-zinc-700 dark:bg-zinc-900";

  return (
    <form onSubmit={submit} className="mx-auto max-w-2xl space-y-3 p-6">
      <div>
        <h2 className="text-base font-semibold">New workspace</h2>
        <p className="text-xs text-zinc-500">
          Tell it what you want done, once or on an ongoing basis. A coordinator agent sets up the agents, schedules and
          webhooks it needs, keeps running, and takes new instructions from you at any time.
        </p>
      </div>
      {templates.length > 0 && (
        <div className="space-y-2 rounded border border-emerald-300 bg-emerald-50 p-3 dark:border-emerald-800 dark:bg-emerald-950/40">
          <div className="text-xs font-semibold text-emerald-800 dark:text-emerald-200">Start from a template</div>
          {templates.map((t) => (
            <div key={t.id} className="flex items-start gap-3 text-xs">
              <div className="flex-1">
                <div className="font-medium">{t.name}</div>
                <div className="text-zinc-600 dark:text-zinc-400">{t.description}</div>
                <div className="mt-0.5 text-[11px] text-zinc-500">
                  {t.autonomy} · webhooks: {t.webhooks.map((w) => w.name).join(", ") || "none"}
                </div>
              </div>
              <button
                type="button"
                disabled={busy}
                onClick={() => fromTemplate(t)}
                className="shrink-0 rounded bg-emerald-600 px-3 py-1.5 font-medium text-white hover:bg-emerald-700 disabled:opacity-50"
              >
                Create
              </button>
            </div>
          ))}
          <label className="flex items-center gap-2 text-[11px] text-zinc-600 dark:text-zinc-400">
            <input type="checkbox" checked={useDemoSystem} onChange={(e) => setUseDemoSystem(e.target.checked)} />
            Connect the simulated production system, so it can be tried right away (add your real connections later)
          </label>
        </div>
      )}
      <div className="flex flex-wrap gap-1">
        {EXAMPLES.map((ex) => (
          <button key={ex.name} type="button" onClick={() => { setName(ex.name); setGoal(ex.goal); }}
            className="rounded-full border border-zinc-300 px-2 py-0.5 text-[11px] text-zinc-600 hover:bg-zinc-100 dark:border-zinc-700 dark:text-zinc-300 dark:hover:bg-zinc-800">
            {ex.name}
          </button>
        ))}
      </div>
      <label className="block space-y-1 text-xs">
        <span className="text-zinc-500">Name</span>
        <input value={name} onChange={(e) => setName(e.target.value)} className={input} />
      </label>
      <label className="block space-y-1 text-xs">
        <span className="text-zinc-500">What should it do?</span>
        <textarea value={goal} onChange={(e) => setGoal(e.target.value)} rows={4} className={input} />
      </label>
      <div className="grid grid-cols-2 gap-3 text-xs">
        <label className="space-y-1">
          <span className="text-zinc-500">Daily token budget</span>
          <input type="number" min={1000} step={1} value={tokens} onChange={(e) => setTokens(Number(e.target.value) || 1000)} className={input} />
        </label>
        <label className="space-y-1">
          <span className="text-zinc-500">Daily cost budget (USD)</span>
          <input type="number" min={0.01} step={0.01} value={dollars} onChange={(e) => setDollars(Number(e.target.value) || 0.01)} className={input} />
        </label>
      </div>
      <p className="text-[11px] text-zinc-500">
        The budget covers every agent in the workspace, all day. When it runs out, agents pause until midnight UTC. The
        runtime enforces it, not the agents.
      </p>
      {error && <div className="rounded border border-rose-300 bg-rose-50 p-2 text-xs text-rose-700 dark:border-rose-800 dark:bg-rose-950 dark:text-rose-300">{error}</div>}
      <button type="submit" disabled={busy || !goal.trim()} className="rounded bg-brand-500 px-4 py-2 text-sm font-medium text-white hover:bg-brand-600 disabled:opacity-50">
        {busy ? "Creating…" : "Create workspace"}
      </button>
    </form>
  );
}
