"use client";

import type { WorldSnapshot } from "@/lib/worldTypes";
import { AgentDetailsPanel } from "../AgentDetailsPanel";
import { accentFor, residentMap } from "./worldUi";

/** A resident's world-side profile (persona, drives, energy, notes, plan) above the standard
 * agent inspector (its tool-call trace and messages). */
export function ResidentPanel({ world, agentId, onClose, onSelect }: {
  world: WorldSnapshot;
  agentId: string;
  onClose: () => void;
  onSelect: (id: string) => void;
}) {
  const residents = residentMap(world);
  const r = residents.get(agentId);
  if (!r) return <AgentDetailsPanel agentId={agentId} onClose={onClose} />;

  const parent = r.parent_agent_id ? residents.get(r.parent_agent_id) : null;
  const children = world.residents.filter((x) => x.parent_agent_id === r.agent_id);

  return (
    <div className="flex h-full flex-col overflow-hidden">
      <div className="max-h-[45%] space-y-1.5 overflow-y-auto border-b border-neutral-200 p-3 text-xs dark:border-neutral-800">
        <div className="flex items-baseline justify-between">
          <span className={`text-sm font-semibold ${accentFor(r.agent_id)}`}>{r.name}</span>
          <span className="text-[10px] uppercase text-neutral-500">
            {world.status === "Ended" && r.state === "Active" ? "lived to the end" : r.state}
          </span>
        </div>
        <div className="text-neutral-500">
          {r.role} · at {r.location} · ⚡{r.energy}/{world.max_energy} · joined tick {r.joined_tick}
        </div>
        {r.persona && <div><span className="text-neutral-500">Persona: </span>{r.persona}</div>}
        <div><span className="text-neutral-500">Drives: </span>{r.drives}</div>
        {r.last_plan && <div><span className="text-neutral-500">Current plan: </span>🧭 {r.last_plan}</div>}
        {r.notes.length > 0 && (
          <div>
            <div className="text-neutral-500">Notes to self:</div>
            <ul className="ml-4 list-disc">
              {r.notes.map((n, i) => <li key={i}>{n}</li>)}
            </ul>
          </div>
        )}
        {(parent || children.length > 0) && (
          <div className="flex flex-wrap gap-1">
            {parent && (
              <button onClick={() => onSelect(parent.agent_id)} className="rounded bg-neutral-100 px-1.5 hover:underline dark:bg-neutral-800">
                brought in by {parent.name}
              </button>
            )}
            {children.map((c) => (
              <button key={c.agent_id} onClick={() => onSelect(c.agent_id)} className="rounded bg-neutral-100 px-1.5 hover:underline dark:bg-neutral-800">
                brought in {c.name}
              </button>
            ))}
          </div>
        )}
        <div className="text-neutral-500">LLM usage: {r.tokens_used.toLocaleString()} tokens · ${r.cost_usd.toFixed(4)}</div>
      </div>
      <div className="min-h-0 flex-1">
        <AgentDetailsPanel agentId={agentId} onClose={onClose} />
      </div>
    </div>
  );
}
