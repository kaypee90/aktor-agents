"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import type { RuntimeEvent } from "@/lib/types";
import type { ActivityKind, Resident, WorldActivity, WorldSnapshot } from "@/lib/worldTypes";
import { EventStream } from "../EventStream";
import { KIND_ICON, KIND_LABEL, accentFor, residentMap } from "./worldUi";

type Tab = "conversations" | "minds" | "board" | "votes" | "events";

const CONVERSATION_KINDS: ActivityKind[] = [
  "said", "talked", "posted", "moved", "gave", "proposed", "voted", "removed", "rejected",
  "dormant", "revived", "joined", "left", "world",
];

type Filter = "all" | "speech" | "private" | "social";
const FILTERS: Record<Filter, (a: WorldActivity) => boolean> = {
  all: () => true,
  speech: (a) => a.kind === "said" || a.kind === "posted",
  private: (a) => a.kind === "talked",
  social: (a) => ["gave", "proposed", "voted", "removed", "rejected", "joined", "left", "dormant", "revived"].includes(a.kind),
};

export function WorldFeed({
  world,
  events,
  onSelect,
}: {
  world: WorldSnapshot;
  events: RuntimeEvent[];
  onSelect: (agentId: string) => void;
}) {
  const [tab, setTab] = useState<Tab>("conversations");
  const residents = useMemo(() => residentMap(world), [world]);
  const openVotes = world.proposals.filter((p) => p.outcome === "Open").length;

  const tabs: [Tab, string][] = [
    ["conversations", "Conversations"],
    ["minds", "Minds"],
    ["board", `Board (${world.board.length})`],
    ["votes", openVotes > 0 ? `Votes (${openVotes} open)` : "Votes"],
    ["events", "Raw events"],
  ];

  return (
    <div className="flex h-full flex-col overflow-hidden">
      <div className="flex border-b border-neutral-200 text-xs dark:border-neutral-800">
        {tabs.map(([t, label]) => (
          <button
            key={t}
            onClick={() => setTab(t)}
            className={`flex-1 px-2 py-2 ${tab === t ? "border-b-2 border-blue-500 font-medium" : "text-neutral-500"}`}
          >
            {label}
          </button>
        ))}
      </div>

      <div className="min-h-0 flex-1 overflow-hidden">
        {tab === "conversations" && <Conversations world={world} residents={residents} onSelect={onSelect} />}
        {tab === "minds" && <Minds world={world} residents={residents} onSelect={onSelect} />}
        {tab === "board" && <Board world={world} onSelect={onSelect} />}
        {tab === "votes" && <Votes world={world} residents={residents} />}
        {tab === "events" && <EventStream events={events} />}
      </div>
    </div>
  );
}

function Name({ id, residents, onSelect }: { id: string | null; residents: Map<string, Resident>; onSelect: (id: string) => void }) {
  if (!id) return null;
  const r = residents.get(id);
  return (
    <button onClick={() => onSelect(id)} className={`font-semibold hover:underline ${accentFor(id)}`}>
      {r?.name ?? id}
    </button>
  );
}

function useAutoScroll(dep: number) {
  const ref = useRef<HTMLDivElement>(null);
  const [stick, setStick] = useState(true);
  useEffect(() => {
    if (stick) ref.current?.scrollIntoView({ block: "end" });
  }, [dep, stick]);
  return { ref, stick, setStick };
}

function Conversations({ world, residents, onSelect }: { world: WorldSnapshot; residents: Map<string, Resident>; onSelect: (id: string) => void }) {
  const [filter, setFilter] = useState<Filter>("all");
  const items = world.activity.filter((a) => CONVERSATION_KINDS.includes(a.kind) && FILTERS[filter](a));
  const { ref, stick, setStick } = useAutoScroll(items.length);

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center gap-1 border-b border-neutral-200 px-2 py-1.5 text-[11px] dark:border-neutral-800">
        {(Object.keys(FILTERS) as Filter[]).map((f) => (
          <button
            key={f}
            onClick={() => setFilter(f)}
            className={`rounded-full px-2 py-0.5 capitalize ${filter === f ? "bg-neutral-800 text-white dark:bg-neutral-100 dark:text-neutral-900" : "text-neutral-500 hover:bg-neutral-100 dark:hover:bg-neutral-800"}`}
          >
            {f}
          </button>
        ))}
        <label className="ml-auto flex items-center gap-1 text-neutral-500">
          <input type="checkbox" checked={stick} onChange={(e) => setStick(e.target.checked)} /> follow
        </label>
      </div>

      <div className="flex-1 space-y-1.5 overflow-y-auto p-2 text-xs">
        {items.length === 0 && <div className="p-4 text-center text-neutral-500">Nothing has happened yet…</div>}
        {items.map((a) => (
          <ActivityItem key={a.seq} a={a} residents={residents} onSelect={onSelect} />
        ))}
        <div ref={ref} />
      </div>
    </div>
  );
}

function ActivityItem({ a, residents, onSelect }: { a: WorldActivity; residents: Map<string, Resident>; onSelect: (id: string) => void }) {
  const speech = (a.kind === "said" || a.kind === "talked" || a.kind === "posted") && a.quote;

  return (
    <div className="flex gap-2">
      <span className="w-8 shrink-0 pt-0.5 text-right text-[10px] tabular-nums text-neutral-400">t{a.tick}</span>
      <span className="shrink-0" title={KIND_LABEL[a.kind]}>{KIND_ICON[a.kind]}</span>
      <div className="min-w-0 flex-1">
        {speech ? (
          <>
            <div className="text-[11px]">
              <Name id={a.actor_id} residents={residents} onSelect={onSelect} />
              {a.kind === "talked" && (
                <>
                  <span className="text-neutral-400"> → </span>
                  <Name id={a.target_id} residents={residents} onSelect={onSelect} />
                  <span className="ml-1 rounded bg-fuchsia-100 px-1 text-[9px] uppercase text-fuchsia-700 dark:bg-fuchsia-950 dark:text-fuchsia-300">private</span>
                </>
              )}
              {a.kind === "said" && a.location && <span className="text-neutral-400"> at {a.location}</span>}
              {a.kind === "posted" && <span className="text-neutral-400"> on the board</span>}
            </div>
            <div
              className={`mt-0.5 inline-block max-w-full whitespace-pre-wrap break-words rounded-lg px-2.5 py-1.5 ${
                a.kind === "talked"
                  ? "border border-dashed border-fuchsia-300 bg-fuchsia-50 dark:border-fuchsia-800 dark:bg-fuchsia-950/50"
                  : a.kind === "posted"
                    ? "border border-amber-300 bg-amber-50 dark:border-amber-800 dark:bg-amber-950/50"
                    : "bg-neutral-100 dark:bg-neutral-800"
              }`}
            >
              {a.quote}
            </div>
          </>
        ) : (
          <div className={`pt-0.5 ${a.kind === "removed" ? "font-semibold text-rose-600 dark:text-rose-400" : "text-neutral-600 dark:text-neutral-400"}`}>
            {a.text}
          </div>
        )}
      </div>
    </div>
  );
}

/** Each resident's private planning — their end_turn plans and notes to self. This is the
 * structured "reason summary" the agents chose to record, never hidden chain-of-thought. */
function Minds({ world, residents, onSelect }: { world: WorldSnapshot; residents: Map<string, Resident>; onSelect: (id: string) => void }) {
  const byResident = new Map<string, WorldActivity[]>();
  for (const a of world.activity) {
    if ((a.kind === "plan" || a.kind === "note") && a.actor_id) {
      if (!byResident.has(a.actor_id)) byResident.set(a.actor_id, []);
      byResident.get(a.actor_id)!.push(a);
    }
  }

  const ordered = world.residents.filter((r) => byResident.has(r.agent_id) || r.last_plan);

  return (
    <div className="h-full space-y-3 overflow-y-auto p-2 text-xs">
      {ordered.length === 0 && <div className="p-4 text-center text-neutral-500">No plans recorded yet.</div>}
      {ordered.map((r) => {
        const entries = (byResident.get(r.agent_id) ?? []).slice(-6).reverse();
        return (
          <div key={r.agent_id} className="rounded border border-neutral-200 p-2 dark:border-neutral-800">
            <div className="flex items-center justify-between">
              <Name id={r.agent_id} residents={residents} onSelect={onSelect} />
              <span className="text-[10px] text-neutral-400">{r.state} · ⚡{r.energy} · {r.location}</span>
            </div>
            <ul className="mt-1 space-y-1">
              {entries.map((e) => (
                <li key={e.seq} className="flex gap-1.5">
                  <span className="shrink-0">{KIND_ICON[e.kind]}</span>
                  <span className="text-neutral-400">t{e.tick}</span>
                  <span className="text-neutral-700 dark:text-neutral-300">{e.quote}</span>
                </li>
              ))}
            </ul>
          </div>
        );
      })}
    </div>
  );
}

function Board({ world, onSelect }: { world: WorldSnapshot; onSelect: (id: string) => void }) {
  return (
    <div className="h-full space-y-2 overflow-y-auto p-2 text-xs">
      {world.board.length === 0 && <div className="p-4 text-center text-neutral-500">The board is empty.</div>}
      {[...world.board].reverse().map((p, i) => (
        <div key={`${p.tick}-${i}`} className="rounded border border-amber-300 bg-amber-50 p-2 dark:border-amber-800 dark:bg-amber-950/40">
          <div className="flex justify-between text-[10px] text-neutral-500">
            <button onClick={() => onSelect(p.author_id)} className={`font-semibold hover:underline ${accentFor(p.author_id)}`}>{p.author_name}</button>
            <span>tick {p.tick}</span>
          </div>
          <div className="mt-1 whitespace-pre-wrap">{p.text}</div>
        </div>
      ))}
    </div>
  );
}

function Votes({ world, residents }: { world: WorldSnapshot; residents: Map<string, Resident> }) {
  const name = (id: string) => residents.get(id)?.name ?? id;
  return (
    <div className="h-full space-y-2 overflow-y-auto p-2 text-xs">
      {world.proposals.length === 0 && <div className="p-4 text-center text-neutral-500">No removal votes so far.</div>}
      {[...world.proposals].reverse().map((p) => {
        const votes = Object.entries(p.votes);
        const yes = votes.filter(([, v]) => v).length;
        const no = votes.length - yes;
        const needed = Math.floor(p.eligible_voters / 2) + 1;
        const tone =
          p.outcome === "Passed" ? "border-rose-300 dark:border-rose-800" : p.outcome === "Rejected" ? "border-neutral-300 dark:border-neutral-700" : "border-blue-300 dark:border-blue-800";
        return (
          <div key={p.proposal_id} className={`rounded border p-2 ${tone}`}>
            <div className="flex justify-between">
              <span className="font-semibold">{p.proposal_id}: remove {name(p.target_id)}?</span>
              <span className="text-[10px] uppercase text-neutral-500">{p.outcome}</span>
            </div>
            <div className="mt-0.5 text-neutral-500">
              Proposed by {name(p.proposer_id)} at tick {p.opened_tick}: “{p.reason}”
            </div>
            <div className="mt-1.5 flex h-2 overflow-hidden rounded bg-neutral-200 dark:bg-neutral-800">
              <div className="bg-rose-500" style={{ width: `${(yes / p.eligible_voters) * 100}%` }} />
              <div className="bg-emerald-500" style={{ width: `${(no / p.eligible_voters) * 100}%` }} />
            </div>
            <div className="mt-1 text-[10px] text-neutral-500">
              {yes} for · {no} against · {p.eligible_voters - votes.length} not voted · {needed} needed · closes after tick {p.deadline_tick}
            </div>
            {votes.length > 0 && (
              <div className="mt-1 flex flex-wrap gap-1">
                {votes.map(([id, v]) => (
                  <span key={id} className={`rounded px-1.5 text-[10px] ${v ? "bg-rose-100 text-rose-700 dark:bg-rose-950 dark:text-rose-300" : "bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300"}`}>
                    {name(id)}: {v ? "remove" : "keep"}
                  </span>
                ))}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}
