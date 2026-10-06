"use client";

import { useCallback, useEffect, useState } from "react";
import { apiErrorMessage, decideApproval, getOrganizationPolicy, listAudit, updateSafetyPolicy, verifyAudit } from "@/lib/api";
import type { TeamPolicy } from "@/lib/workspaceTypes";
import type {
  ApprovalRecord,
  ApprovalRule,
  AuditEntry,
  AuditVerification,
  AutonomyLevel,
  OrganizationSafetyPolicy,
  PolicyDecision,
  SafetyPolicy,
  SideEffectScope,
  WorkspaceSnapshot,
} from "@/lib/workspaceTypes";

export const LEVELS: { value: AutonomyLevel; label: string; help: string }[] = [
  { value: "Autonomous", label: "Autonomous", help: "Agents act on their own. Rules below still apply." },
  { value: "SemiAutonomous", label: "Semi-autonomous", help: "Actions that can't be undone or safely repeated (sending, paying, deleting) need your approval." },
  { value: "Supervised", label: "Supervised", help: "Every external write needs your approval. Reads never do." },
];
const SCOPES: { value: SideEffectScope; label: string }[] = [
  { value: "Writes", label: "writes" },
  { value: "Unsafe", label: "unsafe writes" },
  { value: "Any", label: "any call" },
];
/** How strict each level is, to compare with the organization's minimum. */
export const LEVEL_ORDER: Record<AutonomyLevel, number> = { Autonomous: 0, SemiAutonomous: 1, Supervised: 2 };
const DECISIONS: { value: PolicyDecision; label: string }[] = [
  { value: "RequireApproval", label: "ask me" },
  { value: "Deny", label: "block" },
  { value: "Allow", label: "allow" },
];
const OUTCOME_STYLE: Record<string, string> = {
  ok: "text-emerald-600 dark:text-emerald-400",
  failed: "text-rose-600 dark:text-rose-400",
  denied: "text-rose-600 dark:text-rose-400",
  pending: "text-amber-600 dark:text-amber-400",
  unknown: "text-amber-600 dark:text-amber-400",
};

const field = "rounded border border-zinc-300 bg-white px-2 py-1 dark:border-zinc-700 dark:bg-zinc-900";

function prettyArgs(json: string) {
  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
}

/** A pending approval with Approve / Reject; used in the Safety tab and above the chat. */
/** Fired on the window when an approval is decided, so every view showing approvals refreshes. */
export const APPROVALS_CHANGED = "aktor:approvals-changed";

export function ApprovalCard({ workspaceId, approval, onDecided, compact = false }: {
  workspaceId: string;
  approval: ApprovalRecord;
  onDecided: () => void;
  compact?: boolean;
}) {
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function decide(approve: boolean) {
    setBusy(true);
    setError(null);
    try {
      await decideApproval(workspaceId, approval.approval_id, approve, reason.trim());
      window.dispatchEvent(new Event(APPROVALS_CHANGED));
      onDecided();
    } catch (err) {
      setError(apiErrorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="rounded-md border border-amber-300 bg-amber-50 p-2 text-xs dark:border-amber-800 dark:bg-amber-950/50">
      <div className="flex items-baseline justify-between gap-2">
        <div>
          <span className="font-semibold">{approval.code}</span> · <span className="font-medium">{approval.agent_name}</span> wants to run{" "}
          <code className="rounded bg-white/70 px-1 dark:bg-zinc-900">{approval.tool_name}</code>
        </div>
        <span className="shrink-0 text-[10px] text-zinc-500">expires {new Date(approval.expires_at).toLocaleString()}</span>
      </div>
      {approval.agent_note && <div className="mt-1 italic text-zinc-600 dark:text-zinc-300">&ldquo;{approval.agent_note}&rdquo;</div>}
      {!compact && (
        <>
          <pre className="mt-1 max-h-40 overflow-auto rounded bg-white/70 p-1.5 text-[11px] dark:bg-zinc-900">{prettyArgs(approval.arguments_json)}</pre>
          <div className="mt-1 text-[10px] text-zinc-500">Why: {approval.policy_reason}</div>
        </>
      )}
      <div className="mt-2 flex gap-1.5">
        <input value={reason} onChange={(e) => setReason(e.target.value)} placeholder="Reason (optional)" className={`${field} min-w-0 flex-1 text-xs`} />
        <button disabled={busy} onClick={() => decide(true)} className="rounded bg-emerald-600 px-2.5 font-medium text-white hover:bg-emerald-700 disabled:opacity-50">
          Approve
        </button>
        <button disabled={busy} onClick={() => decide(false)} className="rounded bg-rose-600 px-2.5 font-medium text-white hover:bg-rose-700 disabled:opacity-50">
          Reject
        </button>
      </div>
      {error && <div className="mt-1 text-rose-600">{error}</div>}
    </div>
  );
}

/** Limits on the workspace's team: size, who may spawn, fan-out per level, duplicates. Counted over
 * live agents, since a workspace runs for months. Enforced by the runtime at every spawn. */
export function TeamShapeEditor({ team, onChange, help }: { team: TeamPolicy | null; onChange: (t: TeamPolicy | null) => void; help?: string }) {
  const t: TeamPolicy = team ?? { max_agents: null, spawner_roles: [], max_fan_out_by_depth: [], prevent_duplicate_roles: true, count_finished_agents: false };
  const set = (patch: Partial<TeamPolicy>) => onChange({ ...t, count_finished_agents: false, ...patch });
  return (
    <>
      <h3 className="pt-3 text-xs font-semibold uppercase tracking-wide text-zinc-500">Team shape</h3>
      <p className="text-[11px] text-zinc-500">{help ?? "Limits on top of the server's, checked by the runtime every time an agent tries to start another."}</p>
      <div className="grid grid-cols-2 gap-2 text-xs">
        <label className="space-y-1">
          <span className="text-zinc-500">Max live agents</span>
          <input className={`${field} w-full`} inputMode="numeric" placeholder="No limit" value={t.max_agents ?? ""}
            onChange={(e) => set({ max_agents: e.target.value.trim() === "" ? null : Number(e.target.value) || 0 })} />
        </label>
        <label className="space-y-1">
          <span className="text-zinc-500">Fan-out per level</span>
          <input className={`${field} w-full`} placeholder="e.g. 3, 0" value={t.max_fan_out_by_depth.join(", ")}
            onChange={(e) => set({ max_fan_out_by_depth: e.target.value.split(/[,\s]+/).filter((x) => x !== "").map(Number).filter((n) => !Number.isNaN(n)) })} />
        </label>
        <label className="col-span-2 space-y-1">
          <span className="text-zinc-500">Roles that may spawn (comma-separated, * wildcards; empty: any)</span>
          <input className={`${field} w-full`} placeholder="Coordinator, *lead*" value={t.spawner_roles.join(", ")}
            onChange={(e) => set({ spawner_roles: e.target.value.split(",").map((x) => x.trim()).filter(Boolean) })} />
        </label>
      </div>
      <label className="flex items-center gap-2 text-xs">
        <input type="checkbox" checked={t.prevent_duplicate_roles} onChange={(e) => set({ prevent_duplicate_roles: e.target.checked })} />
        Refuse a second live agent with the same role and goal
      </label>
      {team && (
        <button onClick={() => onChange(null)} className="text-xs text-zinc-500 hover:underline">Clear team rules</button>
      )}
    </>
  );
}

/** Tool rules: a pattern, which calls it covers, and what happens. Checked in order; the first match wins. */
export function RulesEditor({ rules, onChange, disabled = false }: { rules: ApprovalRule[]; onChange: (rules: ApprovalRule[]) => void; disabled?: boolean }) {
  const setRule = (i: number, patch: Partial<ApprovalRule>) => onChange(rules.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  return (
    <>
      {rules.map((r, i) => (
        <div key={r.id} className="flex flex-wrap items-center gap-1 text-xs">
          <input value={r.tool_pattern} disabled={disabled} onChange={(e) => setRule(i, { tool_pattern: e.target.value })} className={`${field} w-32`} placeholder="tool pattern" />
          <select value={r.applies} disabled={disabled} onChange={(e) => setRule(i, { applies: e.target.value as SideEffectScope })} className={field}>
            {SCOPES.map((s) => <option key={s.value} value={s.value}>{s.label}</option>)}
          </select>
          <span>→</span>
          <select value={r.decision} disabled={disabled} onChange={(e) => setRule(i, { decision: e.target.value as PolicyDecision })} className={field}>
            {DECISIONS.map((d) => <option key={d.value} value={d.value}>{d.label}</option>)}
          </select>
          <input value={r.name} disabled={disabled} onChange={(e) => setRule(i, { name: e.target.value })} className={`${field} w-36`} placeholder="name (optional)" />
          {!disabled && (
            <button onClick={() => onChange(rules.filter((_, j) => j !== i))} className="px-1 text-zinc-400 hover:text-rose-600" aria-label="Remove rule">
              ✕
            </button>
          )}
        </div>
      ))}
      {!disabled && (
        <button
          onClick={() => onChange([...rules, { id: crypto.randomUUID().replace(/-/g, "").slice(0, 8), name: "", tool_pattern: "*", applies: "Writes", decision: "RequireApproval" }])}
          className="text-xs text-brand-600 hover:underline dark:text-brand-400"
        >
          + Add rule
        </button>
      )}
    </>
  );
}

/** The organization's rules as they apply in this workspace (read-only; changed in Settings). */
function OrganizationRules({ policy }: { policy: OrganizationSafetyPolicy }) {
  const decision = (d: PolicyDecision) => DECISIONS.find((x) => x.value === d)?.label ?? d;
  const scope = (a: SideEffectScope) => SCOPES.find((x) => x.value === a)?.label ?? a;
  return (
    <section className="space-y-1 rounded-lg border border-violet-200 bg-violet-50/60 p-2.5 text-xs dark:border-violet-900 dark:bg-violet-950/20">
      <div className="flex items-center justify-between">
        <h3 className="font-semibold text-violet-900 dark:text-violet-200">Your organization&apos;s policy applies here too</h3>
        <a href="/settings?tab=safety" className="text-[11px] text-violet-700 hover:underline dark:text-violet-300">Settings →</a>
      </div>
      {policy.minimum_autonomy !== "Autonomous" && (
        <div className="text-violet-900 dark:text-violet-200">At least <span className="font-medium">{LEVELS.find((l) => l.value === policy.minimum_autonomy)?.label}</span> everywhere.</div>
      )}
      {policy.rules.map((r) => (
        <div key={r.id} className="text-violet-900 dark:text-violet-200">
          <code>{r.tool_pattern}</code> ({scope(r.applies)}) → <span className="font-medium">{decision(r.decision)}</span>{r.name ? ` · ${r.name}` : ""}
        </div>
      ))}
      {policy.team && <div className="text-violet-900 dark:text-violet-200">Team-shape limits are set.</div>}
      <p className="text-[11px] text-violet-700/80 dark:text-violet-300/80">
        Checked as well as this workspace&apos;s policy: the stricter answer wins, so the rules below can add to it but not loosen it.
      </p>
    </section>
  );
}

function PolicyEditor({ workspaceId, policy, onSaved, minimum = "Autonomous" }: { workspaceId: string; policy: SafetyPolicy; onSaved: () => void; minimum?: AutonomyLevel }) {
  const [draft, setDraft] = useState<SafetyPolicy>(policy);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const dirty = JSON.stringify(draft) !== JSON.stringify(policy);

  async function save() {
    setSaving(true);
    setError(null);
    try {
      await updateSafetyPolicy(workspaceId, draft);
      onSaved();
    } catch (err) {
      setError(apiErrorMessage(err));
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="space-y-2">
      <h3 className="text-xs font-semibold uppercase tracking-wide text-zinc-500">Autonomy</h3>
      <div className="space-y-1">
        {LEVELS.map((l) => {
          // Below the organization's minimum: it would still apply, so say so.
          const overridden = LEVEL_ORDER[l.value] < LEVEL_ORDER[minimum];
          return (
            <label key={l.value} className={`flex cursor-pointer gap-2 text-xs ${overridden ? "opacity-60" : ""}`}>
              <input type="radio" name="autonomy" checked={draft.autonomy === l.value} onChange={() => setDraft({ ...draft, autonomy: l.value })} />
              <span>
                <span className="font-medium">{l.label}</span>
                <span className="block text-zinc-500">
                  {l.help}
                  {overridden && " Your organization requires more oversight, so its minimum applies instead."}
                </span>
              </span>
            </label>
          );
        })}
      </div>

      <h3 className="pt-2 text-xs font-semibold uppercase tracking-wide text-zinc-500">Rules</h3>
      <p className="text-[11px] text-zinc-500">
        Checked in order before the autonomy level; the first match wins. Patterns match tool names, e.g. <code>billing__*</code> or{" "}
        <code>*__send_sms</code>.
      </p>
      <RulesEditor rules={draft.rules} onChange={(rules) => setDraft((d) => ({ ...d, rules }))} />

      <label className="flex items-center gap-2 pt-1 text-xs">
        Unanswered requests expire after
        <input
          type="number"
          min={1}
          max={720}
          value={draft.approval_timeout_hours}
          onChange={(e) => setDraft({ ...draft, approval_timeout_hours: Number(e.target.value) || 1 })}
          className={`${field} w-16`}
        />
        hours
      </label>

      <TeamShapeEditor team={draft.team ?? null} onChange={(team) => setDraft({ ...draft, team })} />

      {error && <div className="text-xs text-rose-600">{error}</div>}
      <button disabled={!dirty || saving} onClick={save} className="rounded bg-brand-500 px-3 py-1 text-xs font-medium text-white hover:bg-brand-600 disabled:opacity-40">
        {saving ? "Saving…" : "Save policy"}
      </button>
    </section>
  );
}

function AuditLog({ workspaceId, refreshKey }: { workspaceId: string; refreshKey: string }) {
  const [entries, setEntries] = useState<AuditEntry[]>([]);
  const [query, setQuery] = useState("");
  const [verification, setVerification] = useState<AuditVerification | null>(null);
  const [open, setOpen] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setEntries(await listAudit(workspaceId, { q: query.trim() || undefined, limit: 200 }));
      setError(null);
    } catch (err) {
      setError(apiErrorMessage(err));
    }
  }, [workspaceId, query]);

  useEffect(() => {
    const t = setTimeout(load, 250);
    return () => clearTimeout(t);
  }, [load, refreshKey]);

  async function verify() {
    try {
      setVerification(await verifyAudit(workspaceId));
    } catch (err) {
      setError(apiErrorMessage(err));
    }
  }

  return (
    <section className="space-y-2">
      <div className="flex items-center justify-between">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-zinc-500">Audit log</h3>
        <button onClick={verify} className="rounded border border-zinc-300 px-2 py-0.5 text-[11px] hover:bg-zinc-100 dark:border-zinc-700 dark:hover:bg-zinc-800">
          Verify integrity
        </button>
      </div>
      {verification && (
        <div className={`rounded px-2 py-1 text-[11px] ${verification.valid ? "bg-emerald-50 text-emerald-800 dark:bg-emerald-950 dark:text-emerald-200" : "bg-rose-50 text-rose-800 dark:bg-rose-950 dark:text-rose-200"}`}>
          {verification.valid ? "✓ " : "✗ "}
          {verification.message}
        </div>
      )}
      <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search (tool, summary)…" className={`${field} w-full text-xs`} />
      {error && <div className="text-xs text-rose-600">{error}</div>}
      {entries.length === 0 && <div className="text-xs text-zinc-500">Nothing recorded yet.</div>}
      <ul className="divide-y divide-zinc-200 text-xs dark:divide-zinc-800">
        {entries.map((e) => (
          <li key={e.seq} className="py-1.5">
            <button className="w-full text-left" onClick={() => setOpen(open === e.seq ? null : e.seq)}>
              <div className="flex justify-between gap-2">
                <span className="font-mono text-[10px] text-zinc-500">#{e.seq} {new Date(e.at).toLocaleString()}</span>
                <span className={`text-[10px] font-medium ${OUTCOME_STYLE[e.outcome] ?? "text-zinc-500"}`}>{e.outcome}</span>
              </div>
              <div>
                <span className="font-medium">{e.action}</span> <span className="text-zinc-500">by {e.actor_name || e.actor_id}</span>
              </div>
              <div className="truncate text-zinc-600 dark:text-zinc-300">{e.summary}</div>
            </button>
            {open === e.seq && (
              <pre className="mt-1 max-h-60 overflow-auto rounded bg-zinc-100 p-1.5 text-[10px] dark:bg-zinc-900">
                {prettyArgs(e.detail_json)}
                {"\n\nhash "}
                {e.hash}
              </pre>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}

export function SafetyPanel({ workspace, onChanged }: { workspace: WorkspaceSnapshot; onChanged: () => void }) {
  const policy = workspace.safety_policy ?? { autonomy: "Autonomous", rules: [], approval_timeout_hours: 72 };
  const approvals = workspace.approvals ?? [];
  const pending = approvals.filter((a) => a.status === "Pending");
  const decided = approvals.filter((a) => a.status !== "Pending").slice(0, 10);
  const [orgPolicy, setOrgPolicy] = useState<OrganizationSafetyPolicy | null>(null);
  useEffect(() => {
    let live = true;
    getOrganizationPolicy().then((p) => { if (live) setOrgPolicy(p); }).catch(() => { /* shown when it loads */ });
    return () => { live = false; };
  }, [workspace.updated_at]);
  const orgApplies = orgPolicy && (orgPolicy.minimum_autonomy !== "Autonomous" || orgPolicy.rules.length > 0 || !!orgPolicy.team);

  return (
    <div className="space-y-5 p-3">
      <section className="space-y-2">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-zinc-500">Waiting for you ({pending.length})</h3>
        {pending.length === 0 && <div className="text-xs text-zinc-500">No pending approvals.</div>}
        {pending.map((a) => <ApprovalCard key={a.approval_id} workspaceId={workspace.workspace_id} approval={a} onDecided={onChanged} />)}
        {decided.length > 0 && (
          <ul className="space-y-0.5 pt-1 text-[11px] text-zinc-500">
            {decided.map((a) => (
              <li key={a.approval_id}>
                {a.code} {a.tool_name}: <span className="font-medium">{a.status.toLowerCase()}</span>
                {a.decided_by ? ` by ${a.decided_by}` : ""}
                {a.decision_reason ? ` (“${a.decision_reason}”)` : ""}
              </li>
            ))}
          </ul>
        )}
      </section>

      {/* Keyed on the saved policy: a refresh keeps unsaved edits, a saved change resets the form. */}
      {orgPolicy && orgApplies && <OrganizationRules policy={orgPolicy} />}
      <PolicyEditor key={JSON.stringify(policy)} workspaceId={workspace.workspace_id} policy={policy} onSaved={onChanged}
        minimum={orgPolicy?.minimum_autonomy ?? "Autonomous"} />

      <AuditLog workspaceId={workspace.workspace_id} refreshKey={workspace.updated_at} />
    </div>
  );
}

/**
 * Pending approvals across the top of a workspace, with Approve and Reject right there: the agent
 * that asked is stopped until someone decides, so it shouldn't take a trip to the Safety tab.
 */
export function ApprovalBanner({ workspace, onDecided }: {
  workspace: { workspace_id: string; approvals?: ApprovalRecord[] };
  onDecided: () => void;
}) {
  const pending = workspace.approvals?.filter((a) => a.status === "Pending") ?? [];
  if (pending.length === 0) return null;
  return (
    <div role="alert" className="shrink-0 border-b border-amber-300 bg-amber-50 px-4 py-2 dark:border-amber-800 dark:bg-amber-500/10">
      <div className="mb-1.5 flex items-center gap-2 text-xs font-semibold text-amber-900 dark:text-amber-200">
        <span className="h-2 w-2 animate-pulse rounded-full bg-amber-500" />
        {pending.length === 1 ? "An agent is waiting for your approval" : `${pending.length} agents are waiting for your approval`}
      </div>
      <div className="flex max-h-48 gap-2 overflow-x-auto pb-1">
        {pending.map((a) => (
          <div key={a.approval_id} className="w-80 shrink-0">
            <ApprovalCard workspaceId={workspace.workspace_id} approval={a} onDecided={onDecided} compact />
          </div>
        ))}
      </div>
    </div>
  );
}
