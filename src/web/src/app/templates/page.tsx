"use client";

import { useRouter } from "next/navigation";
import { useEffect, useMemo, useState } from "react";
import { apiErrorMessage, createWorkspaceFromTemplate, listWorkspaceTemplates, simulateWorkspaceAlert, startPipelineRun } from "@/lib/api";
import { pipelineShape, type WorkspaceTemplate } from "@/lib/workspaceTypes";
import { Badge, Button, Card, ErrorBanner, Modal, PageHeader, Toggle, cx } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

/** Goals that work well as one-off tasks; they open the task composer with the goal filled in. */
const TASK_STARTERS = [
  {
    title: "Feasibility study",
    goal: "Research whether we should build an AI-powered property management SaaS. Produce a market, technical and business analysis.",
    tags: ["research", "multi-agent"],
  },
  {
    title: "Ship a feature",
    goal: "Add user authentication to this application: design, security review, backend, frontend and tests.",
    tags: ["coding", "review"],
  },
  {
    title: "Competitive teardown",
    goal: "Compare our product with the five closest competitors on pricing, features and positioning, and recommend three changes.",
    tags: ["research"],
  },
  {
    title: "Technical decision",
    goal: "Compare the top five open-source vector databases for a 50M-document search product and recommend one, with a migration plan.",
    tags: ["analysis"],
  },
];

const CATEGORY_ICON: Record<string, (p: { className?: string }) => React.ReactNode> = {
  Operations: Icons.Shield,
  Support: Icons.Chat,
  Research: Icons.Search,
  Engineering: Icons.Graph,
  Sales: Icons.Bolt,
  Marketing: Icons.Sparkles,
  "Legal & finance": Icons.File,
  People: Icons.User,
};

function cronText(cron: string) {
  const [min, hour, , , dow] = cron.split(" ");
  const days: Record<string, string> = { "1": "Mondays", "1-5": "weekdays", "*": "every day" };
  return /^\d+$/.test(min) && /^\d+$/.test(hour) ? `${days[dow] ?? `days ${dow}`} ${hour.padStart(2, "0")}:${min.padStart(2, "0")} UTC` : cron;
}

/** Ready-made starting points: workspace pipelines for real work, and task goals. */
export default function TemplatesPage() {
  const router = useRouter();
  const [templates, setTemplates] = useState<WorkspaceTemplate[]>([]);
  const [category, setCategory] = useState<string | null>(null);
  const [chosen, setChosen] = useState<WorkspaceTemplate | null>(null);
  const [useDemo, setUseDemo] = useState(true);
  const [tryIt, setTryIt] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    listWorkspaceTemplates().then(setTemplates).catch((e) => setError(apiErrorMessage(e)));
  }, []);

  const categories = useMemo(() => [...new Set(templates.map((t) => t.category))], [templates]);
  const shown = category ? templates.filter((t) => t.category === category) : templates;

  async function create() {
    if (!chosen) return;
    setBusy(true);
    setError(null);
    try {
      const created = await createWorkspaceFromTemplate(chosen.id, undefined, useDemo);
      if (tryIt) {
        // A webhook template is tried through its own webhook, exactly as the real service would.
        if (chosen.webhooks.length > 0) await simulateWorkspaceAlert(created.workspace_id).catch(() => undefined);
        else if (created.sample_input) await startPipelineRun(created.workspace_id, created.sample_input).catch(() => undefined);
      }
      router.push(`/workspaces?id=${created.workspace_id}`);
    } catch (e) {
      setError(apiErrorMessage(e));
      setBusy(false);
    }
  }

  return (
    <div>
      <PageHeader title="Templates" description="Start from something that works. Workspace templates are real-world pipelines, with their safety policy, triggers and a sample to try; change any stage afterwards. Task starters give a one-off team a well-shaped goal." />
      <div className="mx-auto max-w-6xl space-y-8 px-6 py-6">
        <ErrorBanner error={error} onClose={() => setError(null)} />

        <section>
          <div className="mb-3 flex flex-wrap items-center gap-2">
            <h2 className="mr-2 text-sm font-semibold text-zinc-900 dark:text-zinc-100">Workspace templates</h2>
            {[null, ...categories].map((c) => (
              <button key={c ?? "all"} onClick={() => setCategory(c)}
                className={cx("rounded-full border px-2.5 py-0.5 text-xs transition",
                  category === c ? "border-brand-400 bg-brand-50 text-brand-700 dark:border-brand-600 dark:bg-brand-500/10 dark:text-brand-300"
                    : "border-zinc-200 text-zinc-600 hover:border-zinc-300 dark:border-zinc-700 dark:text-zinc-400")}>
                {c ?? `All (${templates.length})`}
              </button>
            ))}
          </div>
          <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
            {shown.map((t) => {
              const Icon = CATEGORY_ICON[t.category] ?? Icons.Workspaces;
              return (
                <Card key={t.id} className="flex flex-col p-5">
                  <div className="mb-3 flex items-center gap-3">
                    <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-brand-50 text-brand-600 dark:bg-brand-950/60 dark:text-brand-400">
                      <Icon className="h-5 w-5" />
                    </div>
                    <span className="text-[11px] font-medium uppercase tracking-wide text-zinc-500">{t.category}</span>
                  </div>
                  <div className="font-semibold text-zinc-900 dark:text-zinc-100">{t.name}</div>
                  <p className="mt-1 text-sm text-zinc-600 dark:text-zinc-400">{t.description}</p>
                  <p className="mt-3 flex-1 rounded-lg bg-zinc-50 px-2.5 py-2 text-[11px] leading-relaxed text-zinc-600 dark:bg-zinc-900 dark:text-zinc-400" title="The pipeline: stages side by side run at the same time">
                    {pipelineShape(t.stages)}
                  </p>
                  <div className="mt-3 flex flex-wrap gap-1.5">
                    <Badge tone={t.autonomy === "Autonomous" ? "green" : "amber"}>{t.autonomy}</Badge>
                    {t.webhooks.map((w) => <Badge key={w.name}>Webhook: {w.name}</Badge>)}
                    {t.schedules.map((s) => <Badge key={s.name}>Schedule: {cronText(s.cron)}</Badge>)}
                    {t.connections.map((c) => <Badge key={c.name}>{c.demo_only ? "Demo " : ""}{c.name}</Badge>)}
                  </div>
                  <Button className="mt-4" variant="primary" onClick={() => setChosen(t)}>Use template</Button>
                </Card>
              );
            })}
            {templates.length === 0 && <div className="text-sm text-zinc-500">Loading templates…</div>}
          </div>
        </section>

        <section>
          <h2 className="mb-3 text-sm font-semibold text-zinc-900 dark:text-zinc-100">Task starters</h2>
          <div className="grid gap-4 md:grid-cols-2">
            {TASK_STARTERS.map((s) => (
              <Card key={s.title} className="flex flex-col p-5">
                <div className="flex items-center justify-between">
                  <div className="font-semibold text-zinc-900 dark:text-zinc-100">{s.title}</div>
                  <div className="flex gap-1">{s.tags.map((tag) => <Badge key={tag}>{tag}</Badge>)}</div>
                </div>
                <p className="mt-2 flex-1 text-sm text-zinc-600 dark:text-zinc-400">{s.goal}</p>
                <Button className="mt-4 self-start" icon={<Icons.Play className="h-3.5 w-3.5" />}
                  onClick={() => {
                    try { sessionStorage.setItem("aktor:starterGoal", s.goal); } catch { /* best effort */ }
                    router.push("/");
                  }}>
                  Open in composer
                </Button>
              </Card>
            ))}
          </div>
        </section>
      </div>

      <Modal
        open={chosen !== null}
        onClose={() => setChosen(null)}
        wide
        title={chosen ? `Create "${chosen.name}"` : ""}
        description="A starting point: change any stage, its safety policy, triggers and connections afterwards."
        footer={
          <>
            <Button onClick={() => setChosen(null)}>Cancel</Button>
            <Button variant="primary" disabled={busy} onClick={create}>{busy ? "Creating…" : "Create workspace"}</Button>
          </>
        }
      >
        {chosen && (
          <div className="space-y-4 text-sm">
            <div>
              <div className="mb-1 text-xs font-medium text-zinc-500">Pipeline</div>
              <ol className="space-y-1">
                {chosen.stages.map((s, i) => (
                  <li key={s.stage_id} className="flex gap-2">
                    <span className="w-5 shrink-0 text-right font-mono text-xs text-zinc-400">{i + 1}</span>
                    <span className="font-medium text-zinc-800 dark:text-zinc-200">{s.name}</span>
                    {s.inputs.length > 0 && (
                      <span className="text-xs text-zinc-500">after {s.inputs.map((x) => chosen.stages.find((y) => y.stage_id === x)?.name ?? x).join(", ")}</span>
                    )}
                  </li>
                ))}
              </ol>
            </div>
            {(chosen.webhooks.length > 0 || chosen.schedules.length > 0) && (
              <div className="text-xs text-zinc-600 dark:text-zinc-400">
                <div className="mb-1 font-medium text-zinc-500">Triggers</div>
                {chosen.webhooks.map((w) => <div key={w.name}>Webhook “{w.name}”: each delivery runs the pipeline (its secret URL is shown once created).</div>)}
                {chosen.schedules.map((s) => <div key={s.name}>Schedule: {cronText(s.cron)}.</div>)}
              </div>
            )}
            {chosen.sample_input && (
              <div>
                <Toggle checked={tryIt} onChange={setTryIt} label="Try it now with the sample"
                  description={chosen.webhooks.length > 0 ? "Sends this sample through the workspace's own webhook, as the real service would." : "Starts a first run with this input."} />
                <pre className="mt-2 max-h-40 overflow-y-auto whitespace-pre-wrap rounded-lg bg-zinc-50 p-3 text-xs text-zinc-700 dark:bg-zinc-950 dark:text-zinc-300">{chosen.sample_input}</pre>
              </div>
            )}
            {chosen.connections.some((c) => c.demo_only) && (
              <Toggle checked={useDemo} onChange={setUseDemo} label="Connect the simulated production system"
                description="Lets you try it at once. Add your real connections later in the Integrations tab." />
            )}
          </div>
        )}
      </Modal>
    </div>
  );
}
