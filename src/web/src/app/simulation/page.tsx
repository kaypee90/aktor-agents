"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { getWorld, listWorlds, subscribeToEvents } from "@/lib/api";
import type { RuntimeEvent } from "@/lib/types";
import type { WorldListItem, WorldSnapshot } from "@/lib/worldTypes";
import { CreateWorldForm } from "@/components/world/CreateWorldForm";
import { ResidentPanel } from "@/components/world/ResidentPanel";
import { WorldFeed } from "@/components/world/WorldFeed";
import { WorldMap } from "@/components/world/WorldMap";
import { WorldStatusBar } from "@/components/world/WorldStatusBar";
import { accentFor } from "@/components/world/worldUi";

const POLL_MS = 1500;
const MAX_EVENTS = 500;
const LAST_WORLD_KEY = "aktor:lastWorldId";

function readLastWorld(): string | null {
  try {
    return localStorage.getItem(LAST_WORLD_KEY);
  } catch {
    return null;
  }
}

function rememberWorld(id: string | null) {
  try {
    if (id) localStorage.setItem(LAST_WORLD_KEY, id);
    else localStorage.removeItem(LAST_WORLD_KEY);
  } catch {
    // Storage unavailable (private mode etc.) — only costs the convenience of reopening the world.
  }
}

export default function SimulationPage() {
  const [worldId, setWorldId] = useState<string | null>(null);
  const [world, setWorld] = useState<WorldSnapshot | null>(null);
  const [worlds, setWorlds] = useState<WorldListItem[]>([]);
  const [events, setEvents] = useState<RuntimeEvent[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  // Open ?world=<id> if given (shareable link), else reopen the last world viewed if it still exists.
  useEffect(() => {
    listWorlds()
      .then((list) => {
        setWorlds(list);
        const fromUrl = new URLSearchParams(window.location.search).get("world");
        const last = fromUrl ?? readLastWorld();
        if (last && (fromUrl || list.some((w) => w.world_id === last))) setWorldId(last);
      })
      .catch(() => setWorlds([]));
  }, []);

  const open = useCallback((id: string | null) => {
    setWorldId(id);
    setWorld(null);
    setEvents([]);
    setSelected(null);
    setLoadError(null);
    rememberWorld(id);
    listWorlds().then(setWorlds).catch(() => {});
  }, []);

  const refresh = useCallback(async () => {
    if (!worldId) return;
    try {
      setWorld(await getWorld(worldId));
    } catch {
      // The poll loop below reports load errors.
    }
  }, [worldId]);

  // Poll the snapshot while the world is alive; once it has ended, the last load is final.
  useEffect(() => {
    if (!worldId) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;

    async function poll() {
      let ended = false;
      try {
        const next = await getWorld(worldId!);
        if (cancelled) return;
        setWorld(next);
        setLoadError(null);
        ended = next.status === "Ended";
      } catch (err) {
        if (cancelled) return;
        setLoadError(err instanceof Error ? err.message : "Failed to load world");
      }
      if (!ended) timer = setTimeout(poll, POLL_MS);
    }

    poll();
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [worldId]);

  // Live runtime events for this world (the "Raw events" tab).
  useEffect(() => {
    if (!worldId) return;
    const source = subscribeToEvents((evt) => {
      setEvents((prev) => {
        const next = [...prev, evt];
        return next.length > MAX_EVENTS ? next.slice(next.length - MAX_EVENTS) : next;
      });
    }, worldId);
    return () => source.close();
  }, [worldId]);

  const departed = world?.residents.filter((r) => r.state === "Removed" || r.state === "Left") ?? [];

  return (
    <div className="flex h-screen flex-col bg-white text-neutral-900 dark:bg-neutral-950 dark:text-neutral-100">
      <header className="flex items-center justify-between gap-3 border-b border-neutral-200 px-4 py-3 dark:border-neutral-800">
        <div>
          <h1 className="text-lg font-semibold">Aktor Agents: World Simulation</h1>
          <p className="text-xs text-neutral-500">Autonomous agents living in a shared world: watch them plan, talk, trade, vote and multiply.</p>
        </div>
        <nav className="flex items-center gap-2 text-xs">
          <Link href="/" className="rounded px-2 py-1 text-neutral-600 hover:bg-neutral-100 dark:text-neutral-300 dark:hover:bg-neutral-800">Tasks</Link>
          <span className="rounded bg-neutral-100 px-2 py-1 font-medium dark:bg-neutral-800">Simulation</span>
          <Link href="/workspaces" className="rounded px-2 py-1 text-neutral-600 hover:bg-neutral-100 dark:text-neutral-300 dark:hover:bg-neutral-800">Workspaces</Link>
          <select
            value={worldId ?? ""}
            onChange={(e) => open(e.target.value || null)}
            className="ml-2 max-w-56 rounded border border-neutral-300 bg-white px-2 py-1 dark:border-neutral-700 dark:bg-neutral-900"
          >
            <option value="">+ New world</option>
            {worlds.map((w) => (
              <option key={w.world_id} value={w.world_id}>
                {w.name} · {w.status} · t{w.tick}/{w.max_ticks}
              </option>
            ))}
            {worldId && !worlds.some((w) => w.world_id === worldId) && <option value={worldId}>{worldId}</option>}
          </select>
        </nav>
      </header>

      {!worldId && (
        <div className="flex-1 overflow-y-auto">
          <CreateWorldForm onCreated={open} />
        </div>
      )}

      {worldId && !world && (
        <div className="flex flex-1 items-center justify-center text-sm text-neutral-500">
          {loadError ? (
            <div className="space-y-2 text-center">
              <div>Couldn&apos;t load this world: {loadError}</div>
              <button onClick={() => open(null)} className="rounded border border-neutral-300 px-3 py-1 text-xs dark:border-neutral-700">Create a new world</button>
            </div>
          ) : (
            "Loading world…"
          )}
        </div>
      )}

      {world && (
        <>
          <WorldStatusBar world={world} onChanged={refresh} />
          <div className="flex min-h-0 flex-1">
            <div className="flex min-w-0 flex-1 flex-col border-r border-neutral-200 dark:border-neutral-800">
              <div className="min-h-0 flex-1">
                <WorldMap world={world} selectedId={selected} onSelect={setSelected} />
              </div>
              {departed.length > 0 && (
                <div className="flex flex-wrap items-center gap-1.5 border-t border-neutral-200 px-3 py-1.5 text-[11px] dark:border-neutral-800">
                  <span className="text-neutral-500">Departed:</span>
                  {departed.map((r) => (
                    <button key={r.agent_id} onClick={() => setSelected(r.agent_id)} className={`rounded bg-neutral-100 px-1.5 hover:underline dark:bg-neutral-800 ${accentFor(r.agent_id)}`}>
                      {r.name} ({r.state === "Removed" ? "voted out" : "left"})
                    </button>
                  ))}
                </div>
              )}
            </div>

            {selected && (
              <div className="w-96 shrink-0 border-r border-neutral-200 dark:border-neutral-800">
                <ResidentPanel world={world} agentId={selected} onClose={() => setSelected(null)} onSelect={setSelected} />
              </div>
            )}

            <div className="w-[26rem] shrink-0">
              <WorldFeed world={world} events={events} onSelect={setSelected} />
            </div>
          </div>
        </>
      )}
    </div>
  );
}
