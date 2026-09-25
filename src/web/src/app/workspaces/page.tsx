"use client";

import Link from "next/link";
import { AccountMenu } from "@/components/platform/AccountMenu";
import { useCallback, useEffect, useState } from "react";
import { getWorkspace, listWorkspaces, subscribeToEvents } from "@/lib/api";
import type { RuntimeEvent } from "@/lib/types";
import type { WorkspaceListItem, WorkspaceSnapshot } from "@/lib/workspaceTypes";
import { AgentDetailsPanel } from "@/components/AgentDetailsPanel";
import { CreateWorkspaceForm } from "@/components/workspace/CreateWorkspaceForm";
import { WorkspaceChat } from "@/components/workspace/WorkspaceChat";
import { WorkspaceHeader } from "@/components/workspace/WorkspaceHeader";
import { WorkspaceSidePanel } from "@/components/workspace/WorkspaceSidePanel";

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
  const [workspaces, setWorkspaces] = useState<WorkspaceListItem[]>([]);
  const [workspaceId, setWorkspaceId] = useState<string | null>(null);
  const [workspace, setWorkspace] = useState<WorkspaceSnapshot | null>(null);
  const [events, setEvents] = useState<RuntimeEvent[]>([]);
  const [selectedAgent, setSelectedAgent] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  const reloadList = useCallback(() => listWorkspaces().then(setWorkspaces).catch(() => {}), []);

  useEffect(() => {
    listWorkspaces()
      .then((list) => {
        setWorkspaces(list);
        const fromUrl = new URLSearchParams(window.location.search).get("workspace");
        const last = fromUrl ?? recall();
        if (last && list.some((w) => w.workspace_id === last)) setWorkspaceId(last);
        else if (list.length === 0) setCreating(true);
      })
      .catch(() => setCreating(true));
  }, []);

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
    <div className="flex h-screen flex-col bg-white text-neutral-900 dark:bg-neutral-950 dark:text-neutral-100">
      <header className="flex items-center justify-between gap-3 border-b border-neutral-200 px-4 py-3 dark:border-neutral-800">
        <div>
          <h1 className="text-lg font-semibold">Aktor Agents: Workspaces</h1>
          <p className="text-xs text-neutral-500">Long-running agents that work for you, take new instructions any time, and react to schedules and webhooks.</p>
        </div>
        <nav className="flex items-center gap-2 text-xs">
          <Link href="/" className="rounded px-2 py-1 text-neutral-600 hover:bg-neutral-100 dark:text-neutral-300 dark:hover:bg-neutral-800">Tasks</Link>
          <Link href="/simulation" className="rounded px-2 py-1 text-neutral-600 hover:bg-neutral-100 dark:text-neutral-300 dark:hover:bg-neutral-800">Simulation</Link>
          <span className="rounded bg-neutral-100 px-2 py-1 font-medium dark:bg-neutral-800">Workspaces</span>
          <AccountMenu />
        </nav>
      </header>

      <div className="flex min-h-0 flex-1">
        <aside className="w-60 shrink-0 overflow-y-auto border-r border-neutral-200 dark:border-neutral-800">
          <div className="p-2">
            <button onClick={() => { setCreating(true); setWorkspaceId(null); setWorkspace(null); }}
              className="w-full rounded bg-blue-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-blue-700">
              + New workspace
            </button>
          </div>
          <ul className="text-xs">
            {workspaces.map((w) => (
              <li key={w.workspace_id}>
                <button onClick={() => open(w.workspace_id)}
                  className={`block w-full px-3 py-2 text-left hover:bg-neutral-50 dark:hover:bg-neutral-900 ${w.workspace_id === workspaceId ? "bg-neutral-100 dark:bg-neutral-800" : ""}`}>
                  <span className="flex items-center justify-between gap-1">
                    <span className="truncate font-medium">{w.name}</span>
                    <span className="text-[9px] uppercase text-neutral-400">{w.status}</span>
                  </span>
                  <span className="block truncate text-neutral-500">{w.goal}</span>
                  <span className="block text-[10px] text-neutral-400">{w.agents} agents · {w.triggers} triggers</span>
                </button>
              </li>
            ))}
          </ul>
        </aside>

        {creating && (
          <div className="flex-1 overflow-y-auto"><CreateWorkspaceForm onCreated={open} /></div>
        )}

        {!creating && !workspace && (
          <div className="flex flex-1 items-center justify-center text-sm text-neutral-500">
            {workspaceId ? "Loading workspace…" : "Select a workspace or create a new one."}
          </div>
        )}

        {!creating && workspace && (
          <div className="flex min-w-0 flex-1 flex-col">
            <WorkspaceHeader workspace={workspace} onChanged={refresh} />
            <div className="flex min-h-0 flex-1">
              <div className="min-w-0 flex-1 border-r border-neutral-200 dark:border-neutral-800">
                <WorkspaceChat workspace={workspace} onSent={refresh} onSelectAgent={setSelectedAgent} />
              </div>
              {selectedAgent && (
                <div className="w-96 shrink-0 border-r border-neutral-200 dark:border-neutral-800">
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
