"use client";

import { useCallback, useEffect, useState } from "react";
import { apiErrorMessage, decideApproval, listAudit, updateSafetyPolicy, verifyAudit } from "@/lib/api";
import type {
  ApprovalRecord,
  ApprovalRule,
  AuditEntry,
  AuditVerification,
  AutonomyLevel,
  PolicyDecision,
  SafetyPolicy,
  SideEffectScope,
  WorkspaceSnapshot,
} from "@/lib/workspaceTypes";

const LEVELS: { value: AutonomyLevel; label: string; help: string }[] = [
  { value: "Autonomous", label: "Autonomous", help: "Agents act on their own. Rules below still apply." },
  { value: "SemiAutonomous", label: "Semi-autonomous", help: "Actions that can't be undone or safely repeated (sending, paying, deleting) need your approval." },
  { value: "Supervised", label: "Supervised", help: "Every external write needs your approval. Reads never do." },
];
const SCOPES: { value: SideEffectScope; label: string }[] = [
  { value: "Writes", label: "writes" },
  { value: "Unsafe", label: "unsafe writes" },
  { value: "Any", label: "any call" },
];
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

const field = "rounded border border-neutral-300 bg-white px-2 py-1 dark:border-neutral-700 dark:bg-neutral-900";

function prettyArgs(json: string) {
  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
}

/** A pending approval with Approve / Reject; used in the Safety tab and above the chat. */
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
          <code className="rounded bg-white/70 px-1 dark:bg-neutral-900">{approval.tool_name}</code>
        </div>
        <span className="shrink-0 text-[10px] text-neutral-500">expires {new Date(approval.expires_at).toLocaleString()}</span>
      </div>
      {approval.agent_note && <div className="mt-1 italic text-neutral-600 dark:text-neutral-300">&ldquo;{approval.agent_note}&rdquo;</div>}
      {!compact && (
        <>
          <pre className="mt-1 max-h-40 overflow-auto rounded bg-white/70 p-1.5 text-[11px] dark:bg-neutral-900">{prettyArgs(approval.arguments_json)}</pre>
          <div className="mt-1 text-[10px] text-neutral-500">Why: {approval.policy_reason}</div>
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

function PolicyEditor({ workspaceId, policy, onSaved }: { workspaceId: string; policy: SafetyPolicy; onSaved: () => void }) {
  const [draft, setDraft] = useState<SafetyPolicy>(policy);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const dirty = JSON.stringify(draft) !== JSON.stringify(policy);

  const setRule = (i: number, patch: Partial<ApprovalRule>) =>
    setDraft((d) => ({ ...d, rules: d.rules.map((r, j) => (j === i ? { ...r, ...patch } : r)) }));

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
      <h3 className="text-xs font-semibold uppercase tracking-wide text-neutral-500">Autonomy</h3>
      <div className="space-y-1">
        {LEVELS.map((l) => (
          <label key={l.value} className="flex cursor-pointer gap-2 text-xs">
            <input type="radio" name="autonomy" checked={draft.autonomy === l.value} onChange={() => setDraft({ ...draft, autonomy: l.value })} />
            <span>
              <span className="font-medium">{l.label}</span>
              <span className="block text-neutral-500">{l.help}</span>
            </span>
          </label>
        ))}
      </div>

      <h3 className="pt-2 text-xs font-semibold uppercase tracking-wide text-neutral-500">Rules</h3>
      <p className="text-[11px] text-neutral-500">
        Checked in order before the autonomy level; the first match wins. Patterns match tool names, e.g. <code>billing__*</code> or{" "}
        <code>*__send_sms</code>.
      </p>
      {draft.rules.map((r, i) => (
        <div key={r.id} className="flex flex-wrap items-center gap-1 text-xs">
          <input value={r.tool_pattern} onChange={(e) => setRule(i, { tool_pattern: e.target.value })} className={`${field} w-32`} placeholder="tool pattern" />
          <select value={r.applies} onChange={(e) => setRule(i, { applies: e.target.value as SideEffectScope })} className={field}>
            {SCOPES.map((s) => <option key={s.value} value={s.value}>{s.label}</option>)}
          </select>
          <span>→</span>
          <select value={r.decision} onChange={(e) => setRule(i, { decision: e.target.value as PolicyDecision })} className={field}>
            {DECISIONS.map((d) => <option key={d.value} value={d.value}>{d.label}</option>)}
          </select>
          <button onClick={() => setDraft((d) => ({ ...d, rules: d.rules.filter((_, j) => j !== i) }))} className="px-1 text-neutral-400 hover:text-rose-600" aria-label="Remove rule">
            ✕
          </button>
        </div>
      ))}
      <button
        onClick={() => setDraft((d) => ({
          ...d,
          rules: [...d.rules, { id: crypto.randomUUID().replace(/-/g, "").slice(0, 8), name: "", tool_pattern: "*", applies: "Writes", decision: "RequireApproval" }],
        }))}
        className="text-xs text-blue-600 hover:underline"
      >
        + Add rule
      </button>

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

      {error && <div className="text-xs text-rose-600">{error}</div>}
      <button disabled={!dirty || saving} onClick={save} className="rounded bg-blue-600 px-3 py-1 text-xs font-medium text-white hover:bg-blue-700 disabled:opacity-40">
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
        <h3 className="text-xs font-semibold uppercase tracking-wide text-neutral-500">Audit log</h3>
        <button onClick={verify} className="rounded border border-neutral-300 px-2 py-0.5 text-[11px] hover:bg-neutral-100 dark:border-neutral-700 dark:hover:bg-neutral-800">
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
      {entries.length === 0 && <div className="text-xs text-neutral-500">Nothing recorded yet.</div>}
      <ul className="divide-y divide-neutral-200 text-xs dark:divide-neutral-800">
        {entries.map((e) => (
          <li key={e.seq} className="py-1.5">
            <button className="w-full text-left" onClick={() => setOpen(open === e.seq ? null : e.seq)}>
              <div className="flex justify-between gap-2">
                <span className="font-mono text-[10px] text-neutral-500">#{e.seq} {new Date(e.at).toLocaleString()}</span>
                <span className={`text-[10px] font-medium ${OUTCOME_STYLE[e.outcome] ?? "text-neutral-500"}`}>{e.outcome}</span>
              </div>
              <div>
                <span className="font-medium">{e.action}</span> <span className="text-neutral-500">by {e.actor_name || e.actor_id}</span>
              </div>
              <div className="truncate text-neutral-600 dark:text-neutral-300">{e.summary}</div>
            </button>
            {open === e.seq && (
              <pre className="mt-1 max-h-60 overflow-auto rounded bg-neutral-100 p-1.5 text-[10px] dark:bg-neutral-900">
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

  return (
    <div className="space-y-5 p-3">
      <section className="space-y-2">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-neutral-500">Waiting for you ({pending.length})</h3>
        {pending.length === 0 && <div className="text-xs text-neutral-500">No pending approvals.</div>}
        {pending.map((a) => <ApprovalCard key={a.approval_id} workspaceId={workspace.workspace_id} approval={a} onDecided={onChanged} />)}
        {decided.length > 0 && (
          <ul className="space-y-0.5 pt-1 text-[11px] text-neutral-500">
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
      <PolicyEditor key={JSON.stringify(policy)} workspaceId={workspace.workspace_id} policy={policy} onSaved={onChanged} />

      <AuditLog workspaceId={workspace.workspace_id} refreshKey={workspace.updated_at} />
    </div>
  );
}
