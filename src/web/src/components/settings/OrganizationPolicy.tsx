"use client";

import { useEffect, useState } from "react";
import { apiErrorMessage, getOrganizationPolicy, getOrganizationPolicyAudit, updateOrganizationPolicy } from "@/lib/api";
import type { AuditEntry, OrganizationSafetyPolicy } from "@/lib/workspaceTypes";
import { ago } from "@/components/ui";
import { LEVELS, RulesEditor, TeamShapeEditor } from "../workspace/SafetyPanel";

/** Common starting points, added as rules. */
const SUGGESTIONS: { label: string; rule: { tool_pattern: string; applies: "Any" | "Writes" | "Unsafe"; decision: "Deny" | "RequireApproval" | "Allow"; name: string } }[] = [
  { label: "Payments need approval", rule: { tool_pattern: "*__*pay*", applies: "Writes", decision: "RequireApproval", name: "Payments need approval" } },
  { label: "Deletes need approval", rule: { tool_pattern: "*__*delete*", applies: "Writes", decision: "RequireApproval", name: "Deletes need approval" } },
  { label: "Messages to people need approval", rule: { tool_pattern: "*__send_*", applies: "Unsafe", decision: "RequireApproval", name: "Messages need approval" } },
  { label: "No shell commands", rule: { tool_pattern: "shell_exec", applies: "Any", decision: "Deny", name: "No shell commands" } },
];

/**
 * The organization's safety policy (docs/safety.md#organization-policy): set once, applied to every
 * workspace and task. Each is also checked against its own policy, and the stricter answer wins, so
 * workspaces can add to these rules but never loosen them.
 */
export function OrganizationPolicy({ canEdit }: { canEdit: boolean }) {
  const [saved, setSaved] = useState<OrganizationSafetyPolicy | null>(null);
  const [draft, setDraft] = useState<OrganizationSafetyPolicy | null>(null);
  const [history, setHistory] = useState<AuditEntry[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [version, setVersion] = useState(0);

  useEffect(() => {
    getOrganizationPolicy().then((p) => { setSaved(p); setDraft(p); }).catch((e) => setError(apiErrorMessage(e)));
    getOrganizationPolicyAudit().then(setHistory).catch(() => setHistory([]));
  }, [version]);

  if (!draft || !saved) return <div className="text-sm text-zinc-500">{error ?? "Loading…"}</div>;
  const dirty = JSON.stringify(draft) !== JSON.stringify(saved);

  async function save() {
    if (!draft) return;
    setBusy(true);
    setError(null);
    try {
      await updateOrganizationPolicy(draft);
      setVersion((v) => v + 1);
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-6">
      <section className="space-y-4 rounded-xl border border-zinc-200 bg-white p-5 shadow-sm dark:border-zinc-800 dark:bg-zinc-900/60">
        <div>
          <h2 className="text-sm font-semibold">Organization safety policy</h2>
          <p className="mt-1 text-xs text-zinc-500">
            Set once, applied to every workspace pipeline and every task. Each is also checked against its own policy and the
            stricter answer wins: a workspace can add rules or ask for more oversight, never less. In a task nobody can approve a
            call, so one that needs approval doesn&apos;t run.
            {!canEdit && " Only admins can change it."}
          </p>
        </div>

        <div className="space-y-1.5">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-zinc-500">Minimum autonomy</h3>
          {LEVELS.map((l) => (
            <label key={l.value} className="flex cursor-pointer gap-2 text-xs">
              <input type="radio" name="org-autonomy" disabled={!canEdit} checked={draft.minimum_autonomy === l.value}
                onChange={() => setDraft({ ...draft, minimum_autonomy: l.value })} />
              <span>
                <span className="font-medium">{l.value === "Autonomous" ? "No minimum" : `At least ${l.label.toLowerCase()}`}</span>
                <span className="block text-zinc-500">{l.value === "Autonomous" ? "Each workspace chooses its own level." : l.help}</span>
              </span>
            </label>
          ))}
        </div>

        <div className="space-y-1.5">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-zinc-500">Rules</h3>
          <p className="text-[11px] text-zinc-500">
            Patterns match tool names, e.g. <code>billing__*</code>, <code>*__send_sms</code> or <code>shell_exec</code>. The first
            match wins. &quot;Allow&quot; here only exempts a tool from the minimum above; a workspace&apos;s own policy still applies.
          </p>
          <RulesEditor rules={draft.rules} disabled={!canEdit} onChange={(rules) => setDraft({ ...draft, rules })} />
          {canEdit && (
            <div className="flex flex-wrap gap-1.5 pt-1">
              {SUGGESTIONS.filter((s) => !draft.rules.some((r) => r.tool_pattern === s.rule.tool_pattern)).map((s) => (
                <button key={s.label} onClick={() => setDraft({ ...draft, rules: [...draft.rules, { id: crypto.randomUUID().replace(/-/g, "").slice(0, 8), ...s.rule }] })}
                  className="rounded-full border border-zinc-200 px-2 py-0.5 text-[11px] text-zinc-600 hover:border-brand-400 hover:text-brand-700 dark:border-zinc-700 dark:text-zinc-400">
                  + {s.label}
                </button>
              ))}
            </div>
          )}
        </div>

        {canEdit ? (
          <TeamShapeEditor team={draft.team ?? null} onChange={(team) => setDraft({ ...draft, team })}
            help="Limits every team in the organization keeps to, on top of the server's and each workspace's or task's own." />
        ) : draft.team ? <p className="text-xs text-zinc-500">Team-shape limits are set.</p> : null}

        {error && <div className="text-xs text-rose-600">{error}</div>}
        <div className="flex items-center gap-3">
          {canEdit && (
            <button disabled={!dirty || busy} onClick={save}
              className="rounded-lg bg-brand-500 px-3.5 py-1.5 text-sm font-medium text-white shadow-sm hover:bg-brand-600 disabled:opacity-50">
              {busy ? "Saving…" : "Save policy"}
            </button>
          )}
          {dirty && canEdit && <button onClick={() => setDraft(saved)} className="text-xs text-zinc-500 hover:underline">Discard changes</button>}
          {saved.updated_at && <span className="text-[11px] text-zinc-400">Last changed {ago(saved.updated_at)}{saved.updated_by ? ` by ${saved.updated_by}` : ""}</span>}
        </div>
      </section>

      {history.length > 0 && (
        <section className="space-y-2 rounded-xl border border-zinc-200 bg-white p-5 shadow-sm dark:border-zinc-800 dark:bg-zinc-900/60">
          <h2 className="text-sm font-semibold">Changes</h2>
          <ul className="space-y-1 text-xs text-zinc-600 dark:text-zinc-400">
            {history.map((h) => (
              <li key={h.seq}>{new Date(h.at).toLocaleString()} · {h.actor_name}: {h.summary}</li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}
