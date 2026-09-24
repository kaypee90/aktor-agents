"use client";

import { useState } from "react";
import { createWorld } from "@/lib/api";

const EXAMPLES = [
  "A small fishing village where a storm has destroyed half the boats, and the villagers must decide how to share what's left.",
  "A startup co-working space: four founders competing for one investor's money, a journalist looking for a scandal, and a barista who hears everything.",
  "A medieval town electing its first mayor, with a merchant, a priest, a blacksmith, and a newcomer nobody trusts.",
  "A research station on Mars running low on supplies, with a commander, two scientists, an engineer, and a stowaway.",
];

export function CreateWorldForm({ onCreated }: { onCreated: (worldId: string) => void }) {
  const [seed, setSeed] = useState(EXAMPLES[0]);
  const [population, setPopulation] = useState(5);
  const [tickSeconds, setTickSeconds] = useState(15);
  const [maxTicks, setMaxTicks] = useState(30);
  const [maxMinutes, setMaxMinutes] = useState(20);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const { world_id } = await createWorld({
        seed,
        population,
        tick_interval_seconds: tickSeconds,
        max_ticks: maxTicks,
        max_duration_minutes: maxMinutes,
      });
      onCreated(world_id);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to create world");
    } finally {
      setBusy(false);
    }
  }

  const num = (v: string, min: number, max: number) => Math.max(min, Math.min(max, Number(v) || min));

  return (
    <form onSubmit={submit} className="mx-auto max-w-3xl space-y-3 p-6">
      <div>
        <h2 className="text-base font-semibold">Create a world</h2>
        <p className="text-xs text-neutral-500">
          Describe a setting. An LLM invents its places and residents, then every resident is an autonomous
          agent: it perceives, plans, talks, moves, trades energy, votes and brings new agents into the
          world, with no human in the loop.
        </p>
      </div>

      <textarea
        value={seed}
        onChange={(e) => setSeed(e.target.value)}
        rows={3}
        className="w-full rounded border border-neutral-300 bg-white p-2 text-sm dark:border-neutral-700 dark:bg-neutral-900"
        placeholder="Describe the world…"
      />
      <div className="flex flex-wrap gap-1">
        {EXAMPLES.map((ex) => (
          <button type="button" key={ex} onClick={() => setSeed(ex)} className="truncate rounded-full border border-neutral-300 px-2 py-0.5 text-[11px] text-neutral-600 hover:bg-neutral-100 dark:border-neutral-700 dark:text-neutral-300 dark:hover:bg-neutral-800" style={{ maxWidth: 260 }} title={ex}>
            {ex}
          </button>
        ))}
      </div>

      <div className="grid grid-cols-2 gap-3 text-xs sm:grid-cols-4">
        <label className="space-y-1">
          <span className="text-neutral-500">Residents</span>
          <input type="number" value={population} min={2} max={12} onChange={(e) => setPopulation(num(e.target.value, 2, 12))} className="w-full rounded border border-neutral-300 bg-white px-2 py-1 dark:border-neutral-700 dark:bg-neutral-900" />
        </label>
        <label className="space-y-1">
          <span className="text-neutral-500">Seconds per tick</span>
          <input type="number" value={tickSeconds} min={5} max={600} onChange={(e) => setTickSeconds(num(e.target.value, 5, 600))} className="w-full rounded border border-neutral-300 bg-white px-2 py-1 dark:border-neutral-700 dark:bg-neutral-900" />
        </label>
        <label className="space-y-1">
          <span className="text-neutral-500">Max ticks</span>
          <input type="number" value={maxTicks} min={1} max={500} onChange={(e) => setMaxTicks(num(e.target.value, 1, 500))} className="w-full rounded border border-neutral-300 bg-white px-2 py-1 dark:border-neutral-700 dark:bg-neutral-900" />
        </label>
        <label className="space-y-1">
          <span className="text-neutral-500">Max minutes</span>
          <input type="number" value={maxMinutes} min={1} max={240} onChange={(e) => setMaxMinutes(num(e.target.value, 1, 240))} className="w-full rounded border border-neutral-300 bg-white px-2 py-1 dark:border-neutral-700 dark:bg-neutral-900" />
        </label>
      </div>

      <p className="text-[11px] text-neutral-500">
        Cost bound: the world stops after {maxTicks} ticks or {maxMinutes} minutes, whichever comes first — at most
        about {population * maxTicks} resident turns (plus replies to private messages) for this population. Each
        resident also has its own spending cap.
      </p>

      {error && <div className="rounded border border-rose-300 bg-rose-50 p-2 text-xs text-rose-700 dark:border-rose-800 dark:bg-rose-950 dark:text-rose-300">{error}</div>}

      <button type="submit" disabled={busy || !seed.trim()} className="rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50">
        {busy ? "Generating world…" : "Create world and start"}
      </button>
    </form>
  );
}
