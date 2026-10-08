"use client";

import Link from "next/link";
import { Button, PageHeader, StatusBadge, cx } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { useSearchParams } from "next/navigation";
import { Suspense, useCallback, useEffect, useRef, useState } from "react";
import { getPipelineRun, getWorkspace, getWorkspaceHistory, listWorkspaces, subscribeToEvents } from "@/lib/api";
import type { PipelineRunView } from "@/lib/pipelineTypes";
import type { RuntimeEvent } from "@/lib/types";
import type { WorkspaceListItem, WorkspaceSnapshot } from "@/lib/workspaceTypes";
import { AgentDetailsPanel } from "@/components/AgentDetailsPanel";
import { Split } from "@/components/ui/Split";
import { CreateWorkspaceForm } from "@/components/workspace/CreateWorkspaceForm";
import { PipelinePanel } from "@/components/workspace/PipelinePanel";
import { RunsPanel } from "@/components/workspace/RunsPanel";
import { WorkspaceHeader, withoutTemplateTag } from "@/components/workspace/WorkspaceHeader";
import { ApprovalBanner } from "@/components/workspace/SafetyPanel";
import { WorkspaceSidePanel } from "@/components/workspace/WorkspaceSidePanel";
import { WorkspaceChatWidget } from "@/components/workspace/WorkspaceChatWidget";
import { WorkspaceTeamView } from "@/components/workspace/WorkspaceTeamView";

const POLL_MS = 2000;
const MAX_EVENTS = 500;
const LAST_KEY = "aktor:lastWorkspaceId";
const VIEW_KEY = "aktor:workspaceView";

type CenterView = "live" | "pipeline";

const LAYOUT_KEY = "aktor:workspaceLayout";

/** Which sections around the canvas are shown; hiding them gives the canvas the room. */
interface Layout {
  /** The list of workspaces. */
  list: boolean;
  /** The page title and the workspace's header (goal, triggers, budget). */
  header: boolean;
  /** Chat, events, files, integrations and safety, on the right. */
  side: boolean;
  /** The runs under the canvas. */
  runs: boolean;
}

const ALL_SHOWN: Layout = { list: true, header: true, side: true, runs: true };
const ALL_HIDDEN: Layout = { list: false, header: false, side: false, runs: false };

/** The viewer's last layout; on a first visit from a small screen, the side sections start hidden. */
function readLayout(): Layout {
  try {
    const raw = localStorage.getItem(LAYOUT_KEY);
    if (raw) return { ...ALL_SHOWN, ...JSON.parse(raw) };
    if (window.innerWidth < 1280) return { ...ALL_SHOWN, list: false, side: false };
  } catch {
    // Storage or window unavailable: everything shows.
  }
  return ALL_SHOWN;
}

function readView(): CenterView {
  try {
    return localStorage.getItem(VIEW_KEY) === "pipeline" ? "pipeline" : "live";
  } catch {
    return "live";
  }
}

function remember(id: string | null) {
  try {
    if (id) localStorage.setItem(LAST_KEY, id);
    else localStorage.removeItem(LAST_KEY);
  } catch {
    // Storage unavailable: only costs reopening the last workspace.
  }
}

function recall(): string | null {
  try {
    return localStorage.getItem(LAST_KEY);
  } catch {
    return null;
  }
}

export default function WorkspacesPage() {
  return <Suspense><Workspaces /></Suspense>;
}

function Workspaces() {
  const [workspaces, setWorkspaces] = useState<WorkspaceListItem[]>([]);
  // Deep links (?id=<workspace>) from MCP/A2A results and new templates open that workspace.
  // useSearchParams stays current on in-app navigation, unlike window.location.
  const params = useSearchParams();
  const urlId = params.get("id") ?? params.get("workspace");
  const [workspaceId, setWorkspaceId] = useState<string | null>(urlId);
  const [workspace, setWorkspace] = useState<WorkspaceSnapshot | null>(null);
  const [events, setEvents] = useState<RuntimeEvent[]>([]);
  const [selectedAgent, setSelectedAgent] = useState<string | null>(null);
  const [selectedRunId, setSelectedRunId] = useState<string | null>(null);
  const [selectedRun, setSelectedRun] = useState<PipelineRunView | null>(null);
  const [creating, setCreating] = useState(false);
  // The centre shows the agents at work (live) or the pipeline (editing, or a run's stages).
  const [view, setView] = useState<CenterView>(readView);
  const changeView = useCallback((next: CenterView) => {
    setView(next);
    try { localStorage.setItem(VIEW_KEY, next); } catch { /* only costs the remembered choice */ }
  }, []);
  // Opening a run from the list shows its stages on the pipeline.
  const selectRun = useCallback((runId: string | null) => {
    setSelectedRunId(runId);
    if (runId) changeView("pipeline");
  }, [changeView]);
  const [layout, setLayout] = useState<Layout>(readLayout);
  // The layout before "Focus the canvas", to bring it back.
  const beforeFocus = useRef<Layout | null>(null);
  const changeLayout = useCallback((next: Layout) => {
    setLayout(next);
    try { localStorage.setItem(LAYOUT_KEY, JSON.stringify(next)); } catch { /* only costs the remembered layout */ }
  }, []);
  const focused = !layout.list && !layout.header && !layout.side && !layout.runs;
  const toggleFocus = useCallback(() => {
    if (focused) {
      changeLayout(beforeFocus.current && Object.values(beforeFocus.current).some(Boolean) ? beforeFocus.current : ALL_SHOWN);
    } else {
      beforeFocus.current = layout;
      changeLayout(ALL_HIDDEN);
    }
  }, [focused, layout, changeLayout]);

  // Shift+F focuses the canvas and back, unless typing.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      if (target && (target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName))) return;
      if (e.shiftKey && !e.metaKey && !e.ctrlKey && !e.altKey && e.key.toLowerCase() === "f") {
        e.preventDefault();
        toggleFocus();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [toggleFocus]);

  const reloadList = useCallback(() => listWorkspaces().then(setWorkspaces).catch(() => {}), []);

  useEffect(() => {
    listWorkspaces()
      .then((list) => {
        setWorkspaces(list);
        const last = urlId ?? recall();
        if (last && list.some((w) => w.workspace_id === last)) setWorkspaceId(last);
        else if (list.length === 0) setCreating(true);
      })
      .catch(() => setCreating(true));
    // Once, on open: later URL changes are followed below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // A link to another workspace while this page is open (same route, so no remount).
  const [seenUrlId, setSeenUrlId] = useState(urlId);
  if (urlId !== seenUrlId) {
    setSeenUrlId(urlId);
    if (urlId) {
      setWorkspaceId(urlId);
      setWorkspace(null);
      setEvents([]);
      setSelectedAgent(null);
      setSelectedRunId(null);
      setCreating(false);
    }
  }

  const open = useCallback((id: string) => {
    setWorkspaceId(id);
    setWorkspace(null);
    setEvents([]);
    setSelectedAgent(null);
    setSelectedRunId(null);
    setCreating(false);
    remember(id);
    reloadList();
  }, [reloadList]);

  const refresh = useCallback(async () => {
    if (!workspaceId) return;
    try {
      setWorkspace(await getWorkspace(workspaceId));
    } catch {
      // The poll loop retries.
    }
  }, [workspaceId]);

  useEffect(() => {
    if (!workspaceId) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    async function poll() {
      try {
        const next = await getWorkspace(workspaceId!);
        if (!cancelled) setWorkspace(next);
      } catch {
        // Try again next tick.
      }
      if (!cancelled) timer = setTimeout(poll, POLL_MS);
    }
    poll();
    return () => { cancelled = true; clearTimeout(timer); };
  }, [workspaceId]);

  // Recent history first, so the team view and events show what just happened after a reload;
  // live events that arrived meanwhile are kept, and duplicates dropped.
  useEffect(() => {
    if (!workspaceId) return;
    let cancelled = false;
    getWorkspaceHistory(workspaceId)
      .then((history) => {
        if (cancelled) return;
        setEvents((live) => {
          const seen = new Set(history.map((e) => e.event_id));
          return [...history, ...live.filter((e) => !seen.has(e.event_id))].slice(-MAX_EVENTS);
        });
      })
      .catch(() => { /* History is a convenience; live events still arrive. */ });
    return () => { cancelled = true; };
  }, [workspaceId]);

  // The run shown on the canvas, kept current while it's in progress.
  const refreshRun = useCallback(async () => {
    if (!workspaceId || !selectedRunId) return;
    try {
      setSelectedRun(await getPipelineRun(workspaceId, selectedRunId));
    } catch {
      // The poll loop retries.
    }
  }, [workspaceId, selectedRunId]);

  useEffect(() => {
    if (!selectedRunId) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    async function poll() {
      await refreshRun();
      if (!cancelled) timer = setTimeout(poll, POLL_MS);
    }
    poll();
    return () => { cancelled = true; clearTimeout(timer); };
  }, [selectedRunId, refreshRun]);

  useEffect(() => {
    if (!workspaceId) return;
    const source = subscribeToEvents((evt) => {
      setEvents((prev) => {
        const next = [...prev, evt];
        return next.length > MAX_EVENTS ? next.slice(next.length - MAX_EVENTS) : next;
      });
      // Chat, triggers, pipeline changes and run progress should appear at once, not on the next poll.
      if (["WorkspaceMessage", "TriggerFired", "WorkspaceChanged", "PipelineChanged", "PipelineRunUpdated"].includes(evt.type)) {
        refresh();
        if (evt.type === "PipelineRunUpdated" && evt.data?.run_id === selectedRunId) refreshRun();
      }
    }, workspaceId);
    return () => source.close();
  }, [workspaceId, refresh, refreshRun, selectedRunId]);

  const list = (
    <aside className="h-full overflow-y-auto bg-zinc-50/50 p-2 dark:bg-zinc-950">
      {workspaces.length === 0 && <div className="p-4 text-xs text-zinc-500">No workspaces yet.</div>}
      <ul className="space-y-1">
        {workspaces.map((w) => (
          <li key={w.workspace_id}>
            <button onClick={() => open(w.workspace_id)}
              className={cx(
                "block w-full rounded-lg px-3 py-2.5 text-left transition-colors",
                w.workspace_id === workspaceId
                  ? "bg-white shadow-sm ring-1 ring-zinc-200 dark:bg-zinc-900 dark:ring-zinc-800"
                  : "hover:bg-zinc-100 dark:hover:bg-zinc-900/60",
              )}>
              <span className="flex items-center justify-between gap-2">
                <span className="truncate text-sm font-medium text-zinc-900 dark:text-zinc-100">{w.name}</span>
                <StatusBadge status={w.status} />
              </span>
              <span className="mt-0.5 line-clamp-2 text-xs text-zinc-500">{withoutTemplateTag(w.goal)}</span>
              <span className="mt-1 block text-[11px] text-zinc-400">{w.triggers} trigger{w.triggers === 1 ? "" : "s"}</span>
            </button>
          </li>
        ))}
      </ul>
    </aside>
  );

  let main: React.ReactNode;
  if (creating) {
    main = <div className="h-full overflow-y-auto"><CreateWorkspaceForm onCreated={open} /></div>;
  } else if (!workspace || !workspace.pipeline) {
    main = (
      <div className="flex h-full items-center justify-center text-sm text-zinc-500">
        {workspaceId ? "Loading workspace…" : "Select a workspace or create a new one."}
      </div>
    );
  } else {
    // The run on the canvas: only once it's loaded (not a previously selected one).
    const shownRun = selectedRun?.run_id === selectedRunId ? selectedRun : null;
    // Every section can be resized: drag a divider, or focus it and use the arrow keys.
    const working = workspace.agents.filter((a) => !a.agent_id.startsWith("run-") && ["Thinking", "Executing", "Spawning"].includes(a.status)).length;
    const tab = (id: CenterView, label: React.ReactNode) => (
      <button role="tab" aria-selected={view === id} onClick={() => changeView(id)}
        className={cx("-mb-px inline-flex items-center gap-1.5 border-b-2 px-3 py-2 text-xs transition-colors",
          view === id ? "border-brand-500 font-medium text-zinc-900 dark:text-zinc-100" : "border-transparent text-zinc-500 hover:text-zinc-800 dark:hover:text-zinc-200")}>
        {label}
      </button>
    );
    const toggle = (key: keyof Layout, label: string, icon: React.ReactNode) => (
      <button type="button" aria-pressed={layout[key]} onClick={() => changeLayout({ ...layout, [key]: !layout[key] })}
        title={`${layout[key] ? "Hide" : "Show"} ${label}`} aria-label={`${layout[key] ? "Hide" : "Show"} ${label}`}
        className={cx("rounded-md p-1.5 transition-colors",
          layout[key]
            ? "text-zinc-700 hover:bg-zinc-100 dark:text-zinc-300 dark:hover:bg-zinc-800"
            : "text-zinc-400 hover:bg-zinc-100 hover:text-zinc-600 dark:text-zinc-600 dark:hover:bg-zinc-800 dark:hover:text-zinc-300")}>
        {icon}
      </button>
    );
    // Show or hide the sections around the canvas, or hide them all at once (Shift+F).
    const layoutControls = (
      <div role="group" aria-label="Sections" className="ml-auto flex shrink-0 items-center gap-0.5 py-1 pl-2">
        {toggle("list", "the workspace list", <Icons.PanelLeft className="h-3.5 w-3.5" />)}
        {toggle("header", "the header", <Icons.PanelTop className="h-3.5 w-3.5" />)}
        {toggle("runs", "the runs", <Icons.PanelBottom className="h-3.5 w-3.5" />)}
        {toggle("side", "the side panel", <Icons.PanelRight className="h-3.5 w-3.5" />)}
        <span className="mx-1 h-4 w-px bg-zinc-200 dark:bg-zinc-800" />
        <button type="button" onClick={toggleFocus} aria-pressed={focused}
          title={focused ? "Show the sections again (Shift+F)" : "Focus the canvas: hide every section (Shift+F)"}
          className={cx("inline-flex items-center gap-1 rounded-md px-1.5 py-1 text-xs transition-colors",
            focused ? "bg-brand-50 text-brand-700 hover:bg-brand-100 dark:bg-brand-500/10 dark:text-brand-300" : "text-zinc-600 hover:bg-zinc-100 dark:text-zinc-400 dark:hover:bg-zinc-800")}>
          {focused ? <Icons.Minimize className="h-3.5 w-3.5" /> : <Icons.Maximize className="h-3.5 w-3.5" />}
          <span className="hidden sm:inline">{focused ? "Exit focus" : "Focus"}</span>
        </button>
      </div>
    );
    // Both views stay mounted, so switching keeps an unapplied change and the live history.
    const center = (
      <Split direction="vertical" sized="second" initial={260} min={120} minOther={220} storageKey="workspace-runs" label="Resize the runs panel"
        collapse={layout.runs ? null : "second"}>
        <div className="flex h-full flex-col">
          <div className="flex shrink-0 items-center border-b border-zinc-200 px-2 dark:border-zinc-800">
          <div role="tablist" className="flex min-w-0 items-center overflow-x-auto">
            {tab("live", <>
              <Icons.Graph className="h-3.5 w-3.5" /> Live agents
              {working > 0 && (
                <span className="inline-flex items-center gap-1 rounded-full bg-emerald-50 px-1.5 text-[10px] font-medium text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300">
                  <span className="h-1.5 w-1.5 animate-pulse rounded-full bg-emerald-500" />{working} working
                </span>
              )}
            </>)}
            {tab("pipeline", <><Icons.Workspaces className="h-3.5 w-3.5" /> Pipeline{shownRun && <span className="text-zinc-400">· run #{shownRun.number}</span>}</>)}
          </div>
          {layoutControls}
          </div>
          <div className="relative min-h-0 flex-1">
            {/* Hidden by opacity, not visibility: React Flow sets its nodes visible itself. */}
            <div aria-hidden={view !== "live"} className={cx("absolute inset-0", view !== "live" && "pointer-events-none opacity-0")}>
              <WorkspaceTeamView workspace={workspace} events={events} selectedId={selectedAgent} onSelect={setSelectedAgent} />
            </div>
            <div aria-hidden={view !== "pipeline"} className={cx("absolute inset-0", view !== "pipeline" && "pointer-events-none opacity-0")}>
              <PipelinePanel workspace={workspace} run={shownRun} onCloseRun={() => setSelectedRunId(null)} onChanged={refresh} onSelectAgent={setSelectedAgent} />
            </div>
          </div>
        </div>
        <RunsPanel workspace={workspace} selectedRun={shownRun} onSelectRun={selectRun} onChanged={() => { refresh(); refreshRun(); }} />
      </Split>
    );
    const side = <WorkspaceSidePanel workspace={workspace} events={events} onSelectAgent={setSelectedAgent} onChanged={refresh} />;
    main = (
      <div className="relative flex h-full min-w-0 flex-col">
        {/* Hidden, not unmounted, so an edit in progress survives hiding the header. */}
        <div className={cx("shrink-0", !layout.header && "hidden")}><WorkspaceHeader workspace={workspace} onChanged={refresh} /></div>
        <ApprovalBanner workspace={workspace} onDecided={refresh} />
        <Split className="min-h-0 flex-1" sized="second" initial={380} min={280} max={900} minOther={420} storageKey="workspace-side" label="Resize the side panel"
          collapse={layout.side ? null : "second"}>
          <Split sized="second" initial={360} min={260} max={720} minOther={360} storageKey="workspace-agent" label="Resize the agent details">
            {center}
            {selectedAgent ? <AgentDetailsPanel agentId={selectedAgent} onClose={() => setSelectedAgent(null)} /> : null}
          </Split>
          {side}
        </Split>
        <WorkspaceChatWidget workspace={workspace} onSent={refresh} onSelectRun={selectRun} />
      </div>
    );
  }

  // The layout applies once a workspace is open: without one, the list and title are how to get to one.
  const showingWorkspace = !creating && !!workspace?.pipeline;
  return (
    <div className="flex h-full flex-col">
      {(!showingWorkspace || layout.header) && <PageHeader
        title="Workspaces"
        description="Reusable agent pipelines: describe what you need, adjust the stages in plain language or on the canvas, and run them by hand or from schedules, webhooks and watches."
        actions={
          <>
            <Link href="/templates"><Button icon={<Icons.Templates className="h-3.5 w-3.5" />}>Templates</Button></Link>
            <Button variant="primary" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => { setCreating(true); setWorkspaceId(null); setWorkspace(null); }}>
              New workspace
            </Button>
          </>
        }
      />}
      <Split className="min-h-0 flex-1" initial={240} min={180} max={480} minOther={600} storageKey="workspace-list" label="Resize the workspace list"
        collapse={!showingWorkspace || layout.list ? null : "first"}>
        {list}
        {main}
      </Split>
    </div>
  );
}
