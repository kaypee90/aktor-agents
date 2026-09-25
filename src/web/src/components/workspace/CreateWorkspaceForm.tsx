"use client";

import { useState } from "react";
import { createWorkspace } from "@/lib/api";

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

  const input = "w-full rounded border border-neutral-300 bg-white px-2 py-1.5 text-sm dark:border-neutral-700 dark:bg-neutral-900";

  return (
    <form onSubmit={submit} className="mx-auto max-w-2xl space-y-3 p-6">
      <div>
        <h2 className="text-base font-semibold">New workspace</h2>
        <p className="text-xs text-neutral-500">
          Tell it what you want done, once or on an ongoing basis. A coordinator agent sets up the agents, schedules and
          webhooks it needs, keeps running, and takes new instructions from you at any time.
        </p>
      </div>
      <div className="flex flex-wrap gap-1">
        {EXAMPLES.map((ex) => (
          <button key={ex.name} type="button" onClick={() => { setName(ex.name); setGoal(ex.goal); }}
            className="rounded-full border border-neutral-300 px-2 py-0.5 text-[11px] text-neutral-600 hover:bg-neutral-100 dark:border-neutral-700 dark:text-neutral-300 dark:hover:bg-neutral-800">
            {ex.name}
          </button>
        ))}
      </div>
      <label className="block space-y-1 text-xs">
        <span className="text-neutral-500">Name</span>
        <input value={name} onChange={(e) => setName(e.target.value)} className={input} />
      </label>
      <label className="block space-y-1 text-xs">
        <span className="text-neutral-500">What should it do?</span>
        <textarea value={goal} onChange={(e) => setGoal(e.target.value)} rows={4} className={input} />
      </label>
      <div className="grid grid-cols-2 gap-3 text-xs">
        <label className="space-y-1">
          <span className="text-neutral-500">Daily token budget</span>
          <input type="number" min={1000} step={1000} value={tokens} onChange={(e) => setTokens(Number(e.target.value) || 1000)} className={input} />
        </label>
        <label className="space-y-1">
          <span className="text-neutral-500">Daily cost budget (USD)</span>
          <input type="number" min={0.01} step={0.5} value={dollars} onChange={(e) => setDollars(Number(e.target.value) || 0.01)} className={input} />
        </label>
      </div>
      <p className="text-[11px] text-neutral-500">
        The budget covers every agent in the workspace, all day. When it runs out, agents pause until midnight UTC. The
        runtime enforces it, not the agents.
      </p>
      {error && <div className="rounded border border-rose-300 bg-rose-50 p-2 text-xs text-rose-700 dark:border-rose-800 dark:bg-rose-950 dark:text-rose-300">{error}</div>}
      <button type="submit" disabled={busy || !goal.trim()} className="rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50">
        {busy ? "Creating…" : "Create workspace"}
      </button>
    </form>
  );
}
