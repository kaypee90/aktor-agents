"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { Icons } from "@/components/ui/icons";
import { getMe } from "@/lib/api";
import type { Me } from "@/lib/platformTypes";

const features = [
  {
    icon: Icons.Graph,
    title: "Teams that staff themselves",
    body: "Give one goal. A root agent plans the work, spawns the specialists it needs, and those agents spawn their own when a subtask calls for it.",
  },
  {
    icon: Icons.Chat,
    title: "Agents talk to each other",
    body: "Agents discover and message one another directly, so research reaches the architect without everything passing through the root.",
  },
  {
    icon: Icons.Shield,
    title: "Governed by the runtime",
    body: "Budgets, depth and team-size limits, tool permissions and approvals are enforced by the actor runtime, never left to the model.",
  },
  {
    icon: Icons.Bolt,
    title: "Tools in isolation",
    body: "Web search, files, git, shell and databases, each granted by capability. Anything that runs code runs in a locked-down container.",
  },
  {
    icon: Icons.Replay,
    title: "Every run is on the record",
    body: "Messages, tool calls and decisions are persisted. Watch a run live, then inspect, audit or replay it afterwards.",
  },
  {
    icon: Icons.Connect,
    title: "Works with your stack",
    body: "Start runs from the dashboard, the REST API, or over MCP, A2A and ACP from Claude Code, n8n, CrewAI and more.",
  },
];

const steps = [
  { title: "Describe the outcome", body: "A report, an analysis, a code change. Attach files for context." },
  { title: "Watch the team form", body: "The live graph shows agents spawning, working and messaging each other." },
  { title: "Get the result", body: "The root agent checks the work, assembles the final artifacts and reports." },
];

// The team a feasibility study tends to form, drawn as the hero illustration.
const tree = [
  { label: "Root agent", depth: 0, status: "Thinking" },
  { label: "Market research", depth: 1, status: "Executing" },
  { label: "Data collection", depth: 2, status: "Executing" },
  { label: "Competitor research", depth: 1, status: "Waiting" },
  { label: "Technical architecture", depth: 1, status: "Executing" },
  { label: "Database", depth: 2, status: "Completed" },
  { label: "Business model", depth: 1, status: "Completed" },
];

const statusTone: Record<string, string> = {
  Thinking: "bg-amber-400/15 text-amber-300",
  Executing: "bg-brand-500/15 text-brand-300",
  Waiting: "bg-zinc-500/20 text-zinc-300",
  Completed: "bg-emerald-400/15 text-emerald-300",
};

/** The public front page: what Aktor is, and the way in. */
export default function WelcomePage() {
  const [me, setMe] = useState<Me | null>(null);

  useEffect(() => {
    getMe().then(setMe).catch(() => { /* the page works without it */ });
  }, []);

  const signedIn = !!me && (me.authenticated || me.server.auth_mode === "disabled");
  const canSignUp = me?.server.signup_allowed !== false;

  return (
    <div className="h-full overflow-y-auto bg-white dark:bg-zinc-950">
      <header className="sticky top-0 z-10 border-b border-zinc-200/70 bg-white/80 backdrop-blur dark:border-zinc-800/70 dark:bg-zinc-950/80">
        <div className="mx-auto flex max-w-6xl items-center gap-3 px-4 py-3 sm:px-6">
          <Link href="/welcome" className="flex items-center gap-2.5">
            <Icons.Logo className="h-8 w-8" />
            <span className="text-base font-semibold tracking-tight">Aktor</span>
          </Link>
          <nav className="ml-6 hidden gap-5 text-sm text-zinc-600 md:flex dark:text-zinc-400">
            <a href="#features" className="hover:text-zinc-900 dark:hover:text-zinc-100">Features</a>
            <a href="#how" className="hover:text-zinc-900 dark:hover:text-zinc-100">How it works</a>
          </nav>
          <div className="ml-auto flex items-center gap-2">
            {signedIn ? (
              <Link href="/" className="rounded-lg bg-brand-500 px-3.5 py-2 text-sm font-medium text-white shadow-sm hover:bg-brand-600">
                Open dashboard
              </Link>
            ) : (
              <>
                <Link href="/login" className="rounded-lg px-3 py-2 text-sm font-medium text-zinc-700 hover:bg-zinc-100 dark:text-zinc-300 dark:hover:bg-zinc-900">
                  Sign in
                </Link>
                {canSignUp && (
                  <Link href="/login?mode=signup" className="rounded-lg bg-brand-500 px-3.5 py-2 text-sm font-medium text-white shadow-sm hover:bg-brand-600">
                    Get started
                  </Link>
                )}
              </>
            )}
          </div>
        </div>
      </header>

      <section className="relative overflow-hidden bg-zinc-950 text-zinc-100">
        <div className="pointer-events-none absolute -left-40 -top-40 h-[28rem] w-[28rem] rounded-full bg-brand-500/25 blur-3xl" />
        <div className="pointer-events-none absolute -bottom-48 right-0 h-[28rem] w-[28rem] rounded-full bg-brand-700/20 blur-3xl" />
        <div className="relative mx-auto grid max-w-6xl items-center gap-12 px-4 py-20 sm:px-6 lg:grid-cols-2 lg:py-28">
          <div>
            <span className="inline-flex items-center gap-1.5 rounded-full border border-zinc-800 bg-zinc-900/70 px-3 py-1 text-xs text-zinc-400">
              <Icons.Sparkles className="h-3.5 w-3.5 text-brand-400" /> Autonomous agent teams on an actor runtime
            </span>
            <h1 className="mt-6 text-4xl font-semibold leading-tight tracking-tight sm:text-5xl">
              Give a goal. <span className="text-brand-400">A team of agents</span> does the rest.
            </h1>
            <p className="mt-5 max-w-lg text-base leading-relaxed text-zinc-400">
              Aktor turns one request into a self-organizing team of AI agents that plan, delegate, collaborate and deliver, inside budgets
              and permissions the runtime enforces.
            </p>
            <div className="mt-8 flex flex-wrap gap-3">
              <Link href={signedIn ? "/" : canSignUp ? "/login?mode=signup" : "/login"}
                className="inline-flex items-center gap-1.5 rounded-lg bg-brand-500 px-5 py-2.5 text-sm font-medium text-white shadow-sm hover:bg-brand-600">
                {signedIn ? "Open dashboard" : "Start a run"} <Icons.ChevronRight className="h-4 w-4" />
              </Link>
              {!signedIn && (
                <Link href="/login" className="rounded-lg border border-zinc-700 px-5 py-2.5 text-sm font-medium text-zinc-200 hover:bg-zinc-900">
                  Sign in
                </Link>
              )}
            </div>
          </div>

          <div className="rounded-2xl border border-zinc-800 bg-zinc-900/70 p-5 shadow-2xl">
            <div className="mb-4 flex items-center justify-between text-xs text-zinc-500">
              <span className="font-mono">feasibility-study</span>
              <span className="inline-flex items-center gap-1.5"><span className="h-1.5 w-1.5 animate-pulse rounded-full bg-emerald-400" /> 7 agents live</span>
            </div>
            <ul className="space-y-1.5">
              {tree.map((node) => (
                <li key={node.label} className="flex items-center gap-2" style={{ paddingLeft: `${node.depth * 1.5}rem` }}>
                  {node.depth > 0 && <span className="h-px w-3 bg-zinc-700" />}
                  <span className="flex min-w-0 flex-1 items-center justify-between gap-2 rounded-lg border border-zinc-800 bg-zinc-950/60 px-3 py-2">
                    <span className="truncate text-sm text-zinc-200">{node.label}</span>
                    <span className={`shrink-0 rounded-full px-2 py-0.5 text-[11px] font-medium ${statusTone[node.status]}`}>{node.status}</span>
                  </span>
                </li>
              ))}
            </ul>
            <div className="mt-4 rounded-lg border border-zinc-800 bg-zinc-950/60 px-3 py-2 font-mono text-[11px] text-zinc-500">
              competitor-research → market-research · InformationRequest
            </div>
          </div>
        </div>
      </section>

      <section id="features" className="mx-auto max-w-6xl scroll-mt-16 px-4 py-20 sm:px-6">
        <h2 className="text-2xl font-semibold tracking-tight sm:text-3xl">The model reasons. The runtime governs.</h2>
        <p className="mt-3 max-w-2xl text-sm leading-relaxed text-zinc-500">
          Every agent is an actor with its own state, mailbox, goal and capabilities. Spawning, messaging, tools and limits all go through
          the runtime, which validates each request before anything happens.
        </p>
        <div className="mt-10 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {features.map((f) => (
            <div key={f.title} className="rounded-2xl border border-zinc-200 bg-white p-6 dark:border-zinc-800 dark:bg-zinc-900">
              <span className="inline-flex h-9 w-9 items-center justify-center rounded-lg bg-brand-50 text-brand-600 dark:bg-brand-500/10 dark:text-brand-400">
                <f.icon className="h-4 w-4" />
              </span>
              <h3 className="mt-4 text-sm font-semibold text-zinc-900 dark:text-zinc-100">{f.title}</h3>
              <p className="mt-1.5 text-sm leading-relaxed text-zinc-500">{f.body}</p>
            </div>
          ))}
        </div>
      </section>

      <section id="how" className="scroll-mt-16 border-y border-zinc-200 bg-zinc-50 dark:border-zinc-800 dark:bg-zinc-900/40">
        <div className="mx-auto max-w-6xl px-4 py-20 sm:px-6">
          <h2 className="text-2xl font-semibold tracking-tight sm:text-3xl">How it works</h2>
          <ol className="mt-10 grid gap-6 md:grid-cols-3">
            {steps.map((s, i) => (
              <li key={s.title} className="flex gap-4">
                <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-brand-500 text-sm font-semibold text-white">{i + 1}</span>
                <div>
                  <h3 className="text-sm font-semibold text-zinc-900 dark:text-zinc-100">{s.title}</h3>
                  <p className="mt-1 text-sm leading-relaxed text-zinc-500">{s.body}</p>
                </div>
              </li>
            ))}
          </ol>
        </div>
      </section>

      <section className="mx-auto max-w-6xl px-4 py-20 text-center sm:px-6">
        <h2 className="text-2xl font-semibold tracking-tight sm:text-3xl">Put a team on your next question.</h2>
        <p className="mx-auto mt-3 max-w-xl text-sm text-zinc-500">You provide the goal and watch. The agents work out the rest.</p>
        <Link href={signedIn ? "/" : "/login"}
          className="mt-8 inline-flex items-center gap-1.5 rounded-lg bg-brand-500 px-5 py-2.5 text-sm font-medium text-white shadow-sm hover:bg-brand-600">
          {signedIn ? "Open dashboard" : "Sign in to start"} <Icons.ChevronRight className="h-4 w-4" />
        </Link>
      </section>

      <footer className="border-t border-zinc-200 dark:border-zinc-800">
        <div className="mx-auto flex max-w-6xl items-center gap-2 px-4 py-6 text-xs text-zinc-500 sm:px-6">
          <Icons.Logo className="h-5 w-5" /> Aktor · Governed, autonomous agent teams
        </div>
      </footer>
    </div>
  );
}
