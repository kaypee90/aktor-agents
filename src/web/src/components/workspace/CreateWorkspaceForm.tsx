"use client";

import { useEffect, useState } from "react";
import { createWorkspace, createWorkspaceFromTemplate, listWorkspaceTemplates } from "@/lib/api";
import Link from "next/link";
import { pipelineShape, type WorkspaceTemplate } from "@/lib/workspaceTypes";

// Deliberately varied: workspaces are generic, and these only seed the form. Each describes a
// job to do again and again; the pipeline is drafted from it.
const EXAMPLES = [
  { name: "Market research", goal: "Research a market I name: size, competitors with pricing, and trends, then write a report with sources and have it fact-checked." },
  { name: "Support triage", goal: "For each support ticket: classify its urgency, find the relevant help-centre answer, and draft a reply in our tone." },
  { name: "Morning briefing", goal: "Research the latest news in my industry and turn it into a five-bullet briefing." },
  { name: "Code review", goal: "Review a pull request I link: check correctness, security and tests in parallel, then write one combined review." },
  { name: "Sales follow-ups", goal: "For deals with no activity in 7 days, look up each account and draft a follow-up email for me to review." },
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
          Describe a job you want done again and again. A pipeline of agents is drafted from it; change any stage in plain
          language or on the canvas, then run it with an input, or add a schedule, webhook or watch to run it automatically.
        </p>
      </div>
      {templates.length > 0 && (
        <div className="space-y-2 rounded border border-emerald-300 bg-emerald-50 p-3 dark:border-emerald-800 dark:bg-emerald-950/40">
          <div className="text-xs font-semibold text-emerald-800 dark:text-emerald-200">Start from a template</div>
          {templates.slice(0, 3).map((t) => (
            <div key={t.id} className="flex items-start gap-3 text-xs">
              <div className="flex-1">
                <div className="font-medium">{t.name}</div>
                <div className="text-zinc-600 dark:text-zinc-400">{t.description}</div>
                <div className="mt-0.5 text-[11px] text-zinc-500">{t.category} · {pipelineShape(t.stages)}</div>
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
          {templates.length > 3 && (
            <Link href="/templates" className="block text-[11px] font-medium text-emerald-800 hover:underline dark:text-emerald-200">
              See all {templates.length} templates →
            </Link>
          )}
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
        <span className="text-zinc-500">What is this pipeline for?</span>
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
        The budget covers every run of the pipeline, all day. When it runs out, agents pause until midnight UTC. The
        runtime enforces it, not the agents.
      </p>
      {error && <div className="rounded border border-rose-300 bg-rose-50 p-2 text-xs text-rose-700 dark:border-rose-800 dark:bg-rose-950 dark:text-rose-300">{error}</div>}
      <button type="submit" disabled={busy || !goal.trim()} className="rounded bg-brand-500 px-4 py-2 text-sm font-medium text-white hover:bg-brand-600 disabled:opacity-50">
        {busy ? "Drafting the pipeline…" : "Create workspace"}
      </button>
    </form>
  );
}
