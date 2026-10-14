"use client";

import { useState } from "react";
import { apiErrorMessage, setWorkspaceVariables } from "@/lib/api";
import { Icons } from "@/components/ui/icons";

const NAME = /^[a-z][a-z0-9_]{0,39}$/;

type Row = { id: number; name: string; value: string };

const rowsOf = (variables: Record<string, string>): Row[] =>
  Object.entries(variables).sort(([a], [b]) => a.localeCompare(b)).map(([name, value], i) => ({ id: i, name, value }));

/**
 * A workspace's variables: values every stage can use, set once. Actions and scripts read
 * `{{ vars.name }}` (in a script, `data["vars"]`); agents get `{{ vars.name }}` in their
 * instructions filled in and see every variable. Saved together; a run keeps the values it started with.
 */
export function VariablesPanel({ workspaceId, variables, onChanged }: {
  workspaceId: string;
  variables: Record<string, string>;
  onChanged: () => void;
}) {
  const [rows, setRows] = useState<Row[]>(() => rowsOf(variables));
  const [nextId, setNextId] = useState(1000);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [copied, setCopied] = useState<string | null>(null);

  const names = rows.map((r) => r.name.trim());
  const problem = rows.map((r, i) => {
    const name = names[i];
    if (!name) return r.value ? "Give it a name." : null;
    if (!NAME.test(name)) return "Lowercase letters, digits and _, starting with a letter.";
    if (names.indexOf(name) !== i) return "This name is used twice.";
    return null;
  });
  const current = JSON.stringify(Object.fromEntries(rows.filter((r) => r.name.trim()).map((r) => [r.name.trim(), r.value])));
  const dirty = current !== JSON.stringify(Object.fromEntries(rowsOf(variables).map((r) => [r.name, r.value])));

  const update = (id: number, patch: Partial<Row>) => {
    setSaved(false);
    setRows((rs) => rs.map((r) => (r.id === id ? { ...r, ...patch } : r)));
  };

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const result = await setWorkspaceVariables(workspaceId, JSON.parse(current) as Record<string, string>);
      setRows(rowsOf(result.variables));
      setSaved(true);
      onChanged();
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  function copy(name: string) {
    void navigator.clipboard?.writeText(`{{ vars.${name} }}`);
    setCopied(name);
    setTimeout(() => setCopied((c) => (c === name ? null : c)), 1200);
  }

  return (
    <div className="space-y-3 p-3 text-xs">
      <p className="text-zinc-500">
        Values every stage can use, set once here. Write <code className="rounded bg-zinc-100 px-1 dark:bg-zinc-800">{"{{ vars.name }}"}</code> in
        an action&apos;s arguments or an agent&apos;s instructions; scripts read <code className="rounded bg-zinc-100 px-1 dark:bg-zinc-800">vars</code> in
        their data, and agents see every variable. A run keeps the values it started with.
      </p>
      <p className="rounded bg-amber-50 px-2 py-1.5 text-[11px] text-amber-800 dark:bg-amber-950/40 dark:text-amber-200">
        Not for secrets: agents and run data can see these. Put API keys and passwords in a connection (Integrations).
      </p>

      {rows.length === 0 && <div className="text-zinc-500">No variables yet, e.g. <code>email_recipient</code> or <code>company_name</code>.</div>}

      <ul className="space-y-2">
        {rows.map((r, i) => (
          <li key={r.id} className="space-y-1 rounded border border-zinc-200 p-2 dark:border-zinc-800">
            <div className="flex items-center gap-1.5">
              <input value={r.name} onChange={(e) => update(r.id, { name: e.target.value.toLowerCase().replace(/[\s-]+/g, "_") })}
                placeholder="name" aria-label="Variable name" spellCheck={false}
                className="w-40 rounded border border-zinc-300 bg-white px-2 py-1 font-mono dark:border-zinc-700 dark:bg-zinc-900" />
              {names[i] && !problem[i] && (
                <button type="button" onClick={() => copy(names[i])} title="Copy the expression"
                  className="truncate font-mono text-[10px] text-brand-600 hover:underline dark:text-brand-400">
                  {copied === names[i] ? "copied" : `{{ vars.${names[i]} }}`}
                </button>
              )}
              <button type="button" onClick={() => { setSaved(false); setRows((rs) => rs.filter((x) => x.id !== r.id)); }} title="Remove this variable"
                className="ml-auto rounded p-1 text-zinc-400 hover:bg-zinc-100 hover:text-rose-600 dark:hover:bg-zinc-800">
                <Icons.X className="h-3.5 w-3.5" />
              </button>
            </div>
            <textarea value={r.value} onChange={(e) => update(r.id, { value: e.target.value })} rows={r.value.includes("\n") ? 3 : 1}
              placeholder="value" aria-label={`Value of ${names[i] || "the variable"}`}
              className="block w-full resize-y rounded border border-zinc-300 bg-white px-2 py-1 dark:border-zinc-700 dark:bg-zinc-900" />
            {problem[i] && <div className="text-[11px] text-rose-600">{problem[i]}</div>}
          </li>
        ))}
      </ul>

      {error && <div className="rounded bg-rose-50 p-2 text-rose-700 dark:bg-rose-950 dark:text-rose-300">{error}</div>}
      <div className="flex items-center gap-2">
        <button type="button" onClick={() => { setSaved(false); setRows((rs) => [...rs, { id: nextId, name: "", value: "" }]); setNextId((n) => n + 1); }}
          className="rounded border border-zinc-300 px-3 py-1 hover:bg-zinc-100 dark:border-zinc-700 dark:hover:bg-zinc-800">
          + Add variable
        </button>
        <button type="button" onClick={save} disabled={busy || !dirty || problem.some(Boolean)}
          className="rounded bg-brand-500 px-3 py-1 font-medium text-white hover:bg-brand-600 disabled:opacity-50">
          {busy ? "Saving…" : "Save"}
        </button>
        {saved && !dirty && <span className="text-emerald-600">Saved</span>}
      </div>
    </div>
  );
}
