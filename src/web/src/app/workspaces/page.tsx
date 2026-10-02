"use client";

import Link from "next/link";
import { Button, PageHeader, StatusBadge, cx } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { useSearchParams } from "next/navigation";
import { Suspense, useCallback, useEffect, useState } from "react";
import { getWorkspace, getWorkspaceHistory, listWorkspaces, subscribeToEvents } from "@/lib/api";
import type { RuntimeEvent } from "@/lib/types";
import type { WorkspaceListItem, WorkspaceSnapshot } from "@/lib/workspaceTypes";
import { AgentDetailsPanel } from "@/components/AgentDetailsPanel";
import { CreateWorkspaceForm } from "@/components/workspace/CreateWorkspaceForm";
import { WorkspaceChatWidget, readChatOpen, rememberChatOpen } from "@/components/workspace/WorkspaceChatWidget";
import { WorkspaceHeader, withoutTemplateTag } from "@/components/workspace/WorkspaceHeader";
import { WorkspaceSidePanel } from "@/components/workspace/WorkspaceSidePanel";
import { WorkspaceTeamView } from "@/components/workspace/WorkspaceTeamView";

const POLL_MS = 2000;
const MAX_EVENTS = 500;
const LAST_KEY = "aktor:lastWorkspaceId";

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
  const [creating, setCreating] = useState(false);
  // Shared by the chat widget and the team view, which keeps the agents clear of the open chat.
  const [chatOpen, setChatOpen] = useState(readChatOpen);

  const changeChatOpen = useCallback((open: boolean) => {
    setChatOpen(open);
    rememberChatOpen(open);
  }, []);

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
      setCreating(false);
    }
  }

  const open = useCallback((id: string) => {
    setWorkspaceId(id);
    setWorkspace(null);
    setEvents([]);
    setSelectedAgent(null);
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

  useEffect(() => {
    if (!workspaceId) return;
    const source = subscribeToEvents((evt) => {
      setEvents((prev) => {
        const next = [...prev, evt];
        return next.length > MAX_EVENTS ? next.slice(next.length - MAX_EVENTS) : next;
      });
      // Chat messages and trigger fires should appear immediately, not on the next poll.
      if (evt.type === "WorkspaceMessage" || evt.type === "TriggerFired" || evt.type === "WorkspaceChanged") refresh();
    }, workspaceId);
    return () => source.close();
  }, [workspaceId, refresh]);

  return (
    <div className="flex h-full flex-col">
      <PageHeader
        title="Workspaces"
        description="Long-running agent teams that work for you: they take new instructions any time, react to schedules, webhooks and watches, and stay inside a daily budget."
        actions={
          <>
            <Link href="/templates"><Button icon={<Icons.Templates className="h-3.5 w-3.5" />}>Templates</Button></Link>
            <Button variant="primary" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => { setCreating(true); setWorkspaceId(null); setWorkspace(null); }}>
              New workspace
            </Button>
          </>
        }
      />

      <div className="flex min-h-0 flex-1">
        <aside className="w-60 shrink-0 overflow-y-auto border-r border-zinc-200 bg-zinc-50/50 p-2 dark:border-zinc-800 dark:bg-zinc-950">
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
                  <span className="mt-1 block text-[11px] text-zinc-400">{w.agents} agents · {w.triggers} triggers</span>
                </button>
              </li>
            ))}
          </ul>
        </aside>

        {creating && (
          <div className="flex-1 overflow-y-auto"><CreateWorkspaceForm onCreated={open} /></div>
        )}

        {!creating && !workspace && (
          <div className="flex flex-1 items-center justify-center text-sm text-zinc-500">
            {workspaceId ? "Loading workspace…" : "Select a workspace or create a new one."}
          </div>
        )}

        {!creating && workspace && (
          <div className="flex min-w-0 flex-1 flex-col">
            <WorkspaceHeader workspace={workspace} onChanged={refresh} />
            <div className="flex min-h-0 flex-1">
              <div className="relative min-w-0 flex-1 border-r border-zinc-200 dark:border-zinc-800">
                <WorkspaceTeamView workspace={workspace} events={events} selectedId={selectedAgent} onSelect={setSelectedAgent} chatOpen={chatOpen} />
                <WorkspaceChatWidget workspace={workspace} open={chatOpen} onOpenChange={changeChatOpen} onSent={refresh} onSelectAgent={setSelectedAgent} />
              </div>
              {selectedAgent && (
                <div className="w-96 shrink-0 border-r border-zinc-200 dark:border-zinc-800">
                  <AgentDetailsPanel agentId={selectedAgent} onClose={() => setSelectedAgent(null)} />
                </div>
              )}
              <div className="w-96 shrink-0">
                <WorkspaceSidePanel workspace={workspace} events={events} onSelectAgent={setSelectedAgent} onChanged={refresh} />
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
