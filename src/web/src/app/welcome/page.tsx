"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { Icons } from "@/components/ui/icons";
import { getMe } from "@/lib/api";
import type { Me } from "@/lib/platformTypes";

const features = [
  {
    icon: Icons.Shield,
    title: "Governed by the runtime",
    body: "Budgets, tool permissions, safety rules and approvals are enforced by the runtime, never left to the model. A child agent's limits can only narrow.",
  },
  {
    icon: Icons.Users,
    title: "Each customer in their own lane",
    body: "Records, conversations and runs are scoped to the organization and the end user in every query, so one customer never sees another's data.",
  },
  {
    icon: Icons.Replay,
    title: "Runs that survive a crash",
    body: "Every step is journaled before the next one runs. After a restart, work resumes, and a cut-off action that can't safely repeat is flagged, never retried blindly.",
  },
  {
    icon: Icons.Bolt,
    title: "Tools in isolation",
    body: "Web search, files, git, shell and databases, each granted by capability. Anything that runs code runs in a locked-down container.",
  },
  {
    icon: Icons.Check,
    title: "Every run is on the record",
    body: "Messages, tool calls, decisions, spend and approvals are persisted in a tamper-evident audit log. Inspect, replay or fork any run afterwards.",
  },
  {
    icon: Icons.Graph,
    title: "Agent teams when a job needs one",
    body: "For open-ended work, an agent can bring in specialists that message each other directly, inside the same limits. Watch them on the live graph.",
  },
];

// What people do with it: the product's areas, as they appear in the dashboard.
const products = [
  {
    icon: Icons.Solutions,
    title: "Solutions you build and ship",
    body: "Package your own agentic app with its data model, workflows, a customer-facing assistant and evals. Deploy it with one command, publish it to the registry, and upgrade it without breaking work in progress.",
  },
  {
    icon: Icons.Tasks,
    title: "Tasks",
    body: "Chat with a team like you would with an assistant. Detailed answers with copyable commands, follow-ups that keep every step, files in, and Word, PDF, Excel and slide decks out.",
  },
  {
    icon: Icons.Workspaces,
    title: "Workspaces",
    body: "Reusable pipelines of agents you edit in plain language or on a canvas. Run them on schedules, webhooks, watches and forms, mix agents with actions and scripts that need no model, and clone or share them as templates.",
  },
  {
    icon: Icons.Knowledge,
    title: "Knowledge and skills",
    body: "Upload documents and facts, teach your ways of working as skills, and point agents at either with an @mention.",
  },
  {
    icon: Icons.Connect,
    title: "Your APIs as tools and MCP servers",
    body: "Connect MCP servers and REST APIs, add endpoints by hand or from OpenAPI, and serve them to Claude Code or n8n as an MCP server. The credential never leaves the vault.",
  },
  {
    icon: Icons.Analytics,
    title: "Any model, every token counted",
    body: "Anthropic, OpenAI, Gemini or local Ollama models with list prices built in. Switch a task's model at any time and see spend by model, role, tool and token kind.",
  },
];

// Building on the platform: what a solution brings, beside the code that shows it.
const build = [
  { icon: Icons.Knowledge, title: "Your data model", body: "Entities with typed fields. Agents get tools for them, every write is checked, and a record change can start a run or reach your backend as a signed webhook." },
  { icon: Icons.Users, title: "Your customers", body: "An embeddable assistant your users sign in to with your own login. Each one reaches only their own conversations and records, enforced by the runtime." },
  { icon: Icons.Download, title: "A registry", body: "Publish signed packages, install them by name in any organization, and yank a bad version without breaking installs." },
  { icon: Icons.Analytics, title: "Usage you can bill", body: "Cost per end user, workspace or day from the runtime's own metering, with a monthly cap per user." },
  { icon: Icons.Check, title: "Evals in CI", body: "Expected behaviour, checked by running the workspace for real. A regression fails the build." },
  { icon: Icons.Sliders, title: "No code required", body: "Build a solution with forms, see problems as you type, and deploy from the page. Download the YAML any time; the CLI and SDKs are there if you want them." },
];

const steps = [
  { title: "Build the solution", body: "Start from a template or a blank page: the data model, the workflows that act on it, and the assistant your customers talk to." },
  { title: "Deploy it to your customers", body: "Install it with one command or from the page, connect your own sign-in, and embed the assistant in your product." },
  { title: "Operate it with confidence", body: "Set a spend cap per customer, require approval for risky actions, watch cost and outcomes, and upgrade without breaking work in progress." },
];

// A deployed solution serving its customers, drawn as the hero illustration.
const requests = [
  { who: "Unit 4B", issue: "Leak under the kitchen sink", status: "Resolved", cost: "$0.04" },
  { who: "Unit 12A", issue: "No heat since this morning", status: "Approval", cost: "$0.06" },
  { who: "Unit 7C", issue: "Dishwasher won't drain", status: "Working", cost: "$0.02" },
  { who: "Unit 3F", issue: "Lost key fob", status: "Resolved", cost: "$0.01" },
];

const statusTone: Record<string, string> = {
  Working: "bg-brand-500/15 text-brand-300",
  Approval: "bg-amber-400/15 text-amber-300",
  Resolved: "bg-emerald-400/15 text-emerald-300",
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
            <a href="#build" className="hover:text-zinc-900 dark:hover:text-zinc-100">Solutions</a>
            <a href="#features" className="hover:text-zinc-900 dark:hover:text-zinc-100">Governance</a>
            <a href="#products" className="hover:text-zinc-900 dark:hover:text-zinc-100">Building blocks</a>
            <a href="#how" className="hover:text-zinc-900 dark:hover:text-zinc-100">How it works</a>
            <Link href="/docs" className="hover:text-zinc-900 dark:hover:text-zinc-100">Docs</Link>
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
        <div className="relative mx-auto grid max-w-6xl grid-cols-1 items-center gap-12 px-4 py-20 sm:px-6 lg:grid-cols-2 lg:py-28">
          <div>
            <span className="inline-flex items-center gap-1.5 rounded-full border border-zinc-800 bg-zinc-900/70 px-3 py-1 text-xs text-zinc-400">
              <Icons.Sparkles className="h-3.5 w-3.5 text-brand-400" /> The governed platform for agentic products
            </span>
            <h1 className="mt-6 text-4xl font-semibold leading-tight tracking-tight sm:text-5xl">
              Ship agents to your customers. <span className="text-brand-400">The runtime keeps them in bounds.</span>
            </h1>
            <p className="mt-5 max-w-lg text-base leading-relaxed text-zinc-400">
              Build a solution from workflows, your own data model and a customer-facing assistant. Aktor runs it with each customer
              isolated, spend capped per customer, approvals on risky actions and every step on the record, so you build the product,
              not the agent platform.
            </p>
            <div className="mt-8 flex flex-wrap gap-3">
              <Link href={signedIn ? "/" : canSignUp ? "/login?mode=signup" : "/login"}
                className="inline-flex items-center gap-1.5 rounded-lg bg-brand-500 px-5 py-2.5 text-sm font-medium text-white shadow-sm hover:bg-brand-600">
                {signedIn ? "Open dashboard" : "Start building"} <Icons.ChevronRight className="h-4 w-4" />
              </Link>
              <Link href="/docs" className="rounded-lg border border-zinc-700 px-5 py-2.5 text-sm font-medium text-zinc-200 hover:bg-zinc-900">
                Read the docs
              </Link>
            </div>
          </div>

          <div className="rounded-2xl border border-zinc-800 bg-zinc-900/70 p-5 shadow-2xl">
            <div className="mb-4 flex items-center justify-between text-xs text-zinc-500">
              <span className="font-mono">property-mgmt · maintenance-desk</span>
              <span className="inline-flex items-center gap-1.5"><span className="h-1.5 w-1.5 animate-pulse rounded-full bg-emerald-400" /> live</span>
            </div>
            <ul className="space-y-1.5">
              {requests.map((r) => (
                <li key={r.who} className="flex items-center gap-3 rounded-lg border border-zinc-800 bg-zinc-950/60 px-3 py-2">
                  <span className="w-16 shrink-0 font-mono text-[11px] text-zinc-500">{r.who}</span>
                  <span className="min-w-0 flex-1 truncate text-sm text-zinc-200">{r.issue}</span>
                  <span className="shrink-0 font-mono text-[11px] text-zinc-500">{r.cost}</span>
                  <span className={`shrink-0 rounded-full px-2 py-0.5 text-[11px] font-medium ${statusTone[r.status]}`}>{r.status}</span>
                </li>
              ))}
            </ul>
            <div className="mt-4 rounded-lg border border-zinc-800 bg-zinc-950/60 px-3 py-2 font-mono text-[11px] text-zinc-500">
              Unit 12A · dispatch_vendor waits for approval · cap $5.00/resident/month
            </div>
          </div>
        </div>
      </section>

      <section id="build" className="scroll-mt-16 border-t border-zinc-800 bg-zinc-950 text-zinc-100">
        <div className="mx-auto grid max-w-6xl grid-cols-1 gap-12 px-4 py-20 sm:px-6 lg:grid-cols-[1fr_1.1fr]">
          <div>
            <span className="inline-flex items-center gap-1.5 rounded-full border border-zinc-800 bg-zinc-900/70 px-3 py-1 text-xs text-zinc-400">
              <Icons.Solutions className="h-3.5 w-3.5 text-brand-400" /> For builders
            </span>
            <h2 className="mt-5 text-2xl font-semibold tracking-tight sm:text-3xl">A business app in a folder.</h2>
            <p className="mt-3 max-w-lg text-sm leading-relaxed text-zinc-400">
              A <span className="text-zinc-200">solution</span> is a business app in a folder: its workflows, data model, the assistant your
              customers talk to, and evals. Aktor runs it with the same budgets, approvals and audit as everything else, so you ship the
              product and the runtime keeps it in bounds.
            </p>
            <div className="mt-8 grid gap-5 sm:grid-cols-2">
              {build.map((b) => (
                <div key={b.title} className="flex gap-3">
                  <span className="mt-0.5 inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-brand-500/15 text-brand-300">
                    <b.icon className="h-4 w-4" />
                  </span>
                  <div>
                    <h3 className="text-sm font-semibold text-zinc-100">{b.title}</h3>
                    <p className="mt-1 text-sm leading-relaxed text-zinc-400">{b.body}</p>
                  </div>
                </div>
              ))}
            </div>
            <Link href="/docs#solutions" className="mt-8 inline-flex items-center gap-1.5 text-sm font-medium text-brand-300 hover:text-brand-200">
              How solutions work <Icons.ChevronRight className="h-4 w-4" />
            </Link>
          </div>
          <div className="space-y-4">
            <div className="overflow-hidden rounded-2xl border border-zinc-800 bg-zinc-900/70 shadow-2xl">
              <div className="border-b border-zinc-800 px-4 py-2 font-mono text-[11px] text-zinc-500">aktor.yaml</div>
              <pre className="overflow-x-auto p-4 text-[12.5px] leading-5 text-zinc-300">{`name: property-mgmt
version: 1.2.0
entities:
  Ticket:
    fields:
      issue:     { type: text, required: true }
      priority:  { type: enum, values: [low, normal, urgent] }
      requester: { type: end_user }
workspaces:
  - key: maintenance-desk
    data: { Ticket: write }
    triggers:
      - { kind: Record, entity: Ticket, on: [created] }
assistant:
  name: Resident helper
  data: { Ticket: write }`}</pre>
            </div>
            <div className="overflow-hidden rounded-2xl border border-zinc-800 bg-zinc-900/70">
              <div className="border-b border-zinc-800 px-4 py-2 font-mono text-[11px] text-zinc-500">terminal</div>
              <pre className="overflow-x-auto p-4 text-[12.5px] leading-6 text-zinc-300">{`$ aktor deploy
✓ installed property-mgmt 1.2.0
$ aktor test
✓ urgent leak is triaged  4.2s
$ aktor publish --public
✓ published property-mgmt@1.2.0`}</pre>
            </div>
          </div>
        </div>
      </section>

      <section id="features" className="mx-auto max-w-6xl scroll-mt-16 px-4 py-20 sm:px-6">
        <h2 className="text-2xl font-semibold tracking-tight sm:text-3xl">The model reasons. The runtime governs.</h2>
        <p className="mt-3 max-w-2xl text-sm leading-relaxed text-zinc-500">
          The model proposes; the runtime checks every action against your limits before it happens. That is what lets you put agents in
          front of customers.
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

      <section id="products" className="mx-auto max-w-6xl scroll-mt-16 px-4 pb-20 sm:px-6">
        <h2 className="text-2xl font-semibold tracking-tight sm:text-3xl">Built from workflows and tasks.</h2>
        <p className="mt-3 max-w-2xl text-sm leading-relaxed text-zinc-500">
          Every solution is made of the same pieces, and each one is useful on its own: run a workflow every day, or hand a one-off job to a task.
        </p>
        <div className="mt-10 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {products.map((f) => (
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
        <h2 className="text-2xl font-semibold tracking-tight sm:text-3xl">Ship your first solution.</h2>
        <p className="mx-auto mt-3 max-w-xl text-sm text-zinc-500">Describe it in the builder, or start from the property-maintenance or helpdesk sample, and put an assistant in front of your customers.</p>
        <Link href={signedIn ? "/" : "/login"}
          className="mt-8 inline-flex items-center gap-1.5 rounded-lg bg-brand-500 px-5 py-2.5 text-sm font-medium text-white shadow-sm hover:bg-brand-600">
          {signedIn ? "Open dashboard" : "Sign in to start"} <Icons.ChevronRight className="h-4 w-4" />
        </Link>
      </section>

      <footer className="border-t border-zinc-200 dark:border-zinc-800">
        <div className="mx-auto flex max-w-6xl items-center gap-2 px-4 py-6 text-xs text-zinc-500 sm:px-6">
          <Icons.Logo className="h-5 w-5" /> Aktor · The governed platform for agentic products
          <Link href="/docs" className="ml-auto hover:text-zinc-800 dark:hover:text-zinc-200">Documentation</Link>
        </div>
      </footer>
    </div>
  );
}
