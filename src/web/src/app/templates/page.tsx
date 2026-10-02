"use client";

import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { apiErrorMessage, createWorkspaceFromTemplate, listWorkspaceTemplates } from "@/lib/api";
import type { WorkspaceTemplate } from "@/lib/workspaceTypes";
import { Badge, Button, Card, ErrorBanner, Modal, PageHeader, Toggle } from "@/components/ui";
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

/** Ready-made starting points: workspace templates and task goals. */
export default function TemplatesPage() {
  const router = useRouter();
  const [templates, setTemplates] = useState<WorkspaceTemplate[]>([]);
  const [chosen, setChosen] = useState<WorkspaceTemplate | null>(null);
  const [useDemo, setUseDemo] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    listWorkspaceTemplates().then(setTemplates).catch((e) => setError(apiErrorMessage(e)));
  }, []);

  async function create() {
    if (!chosen) return;
    setBusy(true);
    setError(null);
    try {
      const { workspace_id } = await createWorkspaceFromTemplate(chosen.id, undefined, useDemo);
      router.push(`/workspaces?id=${workspace_id}`);
    } catch (e) {
      setError(apiErrorMessage(e));
      setBusy(false);
    }
  }

  return (
    <div>
      <PageHeader title="Templates" description="Start from something that works. Workspace templates set up a standing team with its safety policy, connections and triggers; task starters give a team a well-shaped goal." />
      <div className="mx-auto max-w-6xl space-y-8 px-6 py-6">
        <ErrorBanner error={error} onClose={() => setError(null)} />

        <section>
          <h2 className="mb-3 text-sm font-semibold text-zinc-900 dark:text-zinc-100">Workspace templates</h2>
          <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
            {templates.map((t) => (
              <Card key={t.id} className="flex flex-col p-5">
                <div className="mb-3 flex h-10 w-10 items-center justify-center rounded-xl bg-brand-50 text-brand-600 dark:bg-brand-950/60 dark:text-brand-400">
                  <Icons.Shield className="h-5 w-5" />
                </div>
                <div className="font-semibold text-zinc-900 dark:text-zinc-100">{t.name}</div>
                <p className="mt-1 flex-1 text-sm text-zinc-600 dark:text-zinc-400">{t.description}</p>
                <div className="mt-3 flex flex-wrap gap-1.5">
                  <Badge tone="amber">{t.autonomy}</Badge>
                  {t.webhooks.map((w) => <Badge key={w.name}>Webhook: {w.name}</Badge>)}
                  {t.connections.map((c) => <Badge key={c.name}>{c.demo_only ? "Demo" : ""} {c.name}</Badge>)}
                </div>
                <Button className="mt-4" variant="primary" onClick={() => setChosen(t)}>Use template</Button>
              </Card>
            ))}
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
        description="The workspace's coordinator follows these instructions. You can edit them, the safety policy and the connections afterwards."
        footer={
          <>
            <Button onClick={() => setChosen(null)}>Cancel</Button>
            <Button variant="primary" disabled={busy} onClick={create}>{busy ? "Creating…" : "Create workspace"}</Button>
          </>
        }
      >
        {chosen && (
          <div className="space-y-4">
            <pre className="max-h-72 overflow-y-auto whitespace-pre-wrap rounded-lg bg-zinc-50 p-3 text-xs text-zinc-700 dark:bg-zinc-950 dark:text-zinc-300">{chosen.goal}</pre>
            {chosen.connections.some((c) => c.demo_only) && (
              <Toggle checked={useDemo} onChange={setUseDemo} label="Connect the simulated production system"
                description="Lets you try it at once (Simulate alert). Add your real connections later in the Integrations tab." />
            )}
          </div>
        )}
      </Modal>
    </div>
  );
}
