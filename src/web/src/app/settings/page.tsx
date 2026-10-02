"use client";

import { useSearchParams } from "next/navigation";
import { ModelSettings } from "@/components/settings/ModelSettings";
import { Suspense, useEffect, useState } from "react";
import {
  API_BASE,
  apiErrorMessage,
  changePassword,
  createApiKey,
  getBilling,
  getOrganization,
  inviteMember,
  listApiKeys,
  listInvitations,
  listMembers,
  openBillingPortal,
  removeMember,
  renameOrganization,
  revokeApiKey,
  revokeInvitation,
  setMemberRole,
  startCheckout,
} from "@/lib/api";
import { ROLES, ROLE_HELP, atLeast } from "@/lib/platformTypes";
import type { ApiKey, BillingView, Invitation, Member, Organization, Plan, Role } from "@/lib/platformTypes";
import { PageHeader } from "@/components/ui";
import { useAuth } from "@/components/platform/AuthProvider";

type Tab = "organization" | "model" | "api-keys" | "billing" | "account";

const field = "rounded-lg border border-zinc-200 bg-white px-3 py-1.5 text-sm shadow-sm outline-none focus:border-brand-400 focus:ring-2 focus:ring-brand-500/20 dark:border-zinc-800 dark:bg-zinc-950";
const button = "rounded-lg bg-brand-500 px-3.5 py-1.5 text-sm font-medium text-white shadow-sm hover:bg-brand-600 disabled:opacity-50";
const subtle = "rounded-lg border border-zinc-200 px-2.5 py-1 text-xs hover:bg-zinc-100 dark:border-zinc-800 dark:hover:bg-zinc-800";

const fmt = (n: number) => n.toLocaleString();
const limit = (n: number, unit = "") => (n > 0 ? `${fmt(n)}${unit}` : "unlimited");

function Section({ title, children, right }: { title: string; children: React.ReactNode; right?: React.ReactNode }) {
  return (
    <section className="space-y-3 rounded-xl border border-zinc-200 bg-white p-5 shadow-sm dark:border-zinc-800 dark:bg-zinc-900/60">
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-semibold">{title}</h2>
        {right}
      </div>
      {children}
    </section>
  );
}

function CopyOnce({ label, value }: { label: string; value: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="space-y-1 rounded border border-amber-300 bg-amber-50 p-2 text-xs dark:border-amber-800 dark:bg-amber-950/40">
      <div className="font-medium">{label}</div>
      <div className="flex gap-2">
        <code className="min-w-0 flex-1 truncate rounded bg-white px-1.5 py-1 dark:bg-zinc-900">{value}</code>
        <button type="button" className={subtle} onClick={() => navigator.clipboard.writeText(value).then(() => setCopied(true))}>
          {copied ? "copied" : "copy"}
        </button>
      </div>
    </div>
  );
}

function OrganizationTab({ role }: { role: Role }) {
  const { me, refresh } = useAuth();
  const [org, setOrg] = useState<Organization | null>(null);
  const [members, setMembers] = useState<Member[]>([]);
  const [invitations, setInvitations] = useState<Invitation[]>([]);
  const [name, setName] = useState("");
  const [inviteEmail, setInviteEmail] = useState("");
  const [inviteRole, setInviteRole] = useState<Role>("Member");
  const [link, setLink] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const isAdmin = atLeast(role, "Admin");
  const [version, setVersion] = useState(0);

  useEffect(() => {
    let cancelled = false;
    Promise.all([getOrganization(), listMembers(), isAdmin ? listInvitations() : Promise.resolve([] as Invitation[])])
      .then(([o, m, i]) => {
        if (cancelled) return;
        setOrg(o);
        setName(o.name);
        setMembers(m);
        setInvitations(i);
      })
      .catch((err) => !cancelled && setError(apiErrorMessage(err)));
    return () => { cancelled = true; };
  }, [isAdmin, version]);

  async function act(fn: () => Promise<unknown>) {
    setError(null);
    try {
      await fn();
      setVersion((v) => v + 1);
    } catch (err) {
      setError(apiErrorMessage(err));
    }
  }

  async function invite(e: React.FormEvent) {
    e.preventDefault();
    await act(async () => {
      const created = await inviteMember(inviteEmail, inviteRole);
      setLink(`${window.location.origin}/login?invite=${encodeURIComponent(created.token)}`);
      setInviteEmail("");
    });
  }

  return (
    <div className="space-y-4">
      {error && <div className="text-sm text-rose-600">{error}</div>}
      <Section title="Organization">
        <div className="flex gap-2">
          <input value={name} onChange={(e) => setName(e.target.value)} disabled={!isAdmin} className={`${field} flex-1`} />
          {isAdmin && (
            <button className={button} disabled={!name.trim() || name === org?.name}
              onClick={() => act(async () => { await renameOrganization(name); await refresh(); })}>
              Rename
            </button>
          )}
        </div>
        <div className="text-xs text-zinc-500">Id <code>{org?.tenant_id}</code> · {org?.plan.name} plan · you are {role}</div>
      </Section>

      <Section title={`Members (${members.length}${org && org.plan.max_members > 0 ? ` of ${org.plan.max_members}` : ""})`}>
        <ul className="divide-y divide-zinc-200 text-sm dark:divide-zinc-800">
          {members.map((m) => {
            const self = m.user_id === me?.user?.user_id;
            return (
              <li key={m.user_id} className="flex items-center justify-between gap-2 py-2">
                <div className="min-w-0">
                  <div className="truncate font-medium">{m.name} {self && <span className="text-xs text-zinc-500">(you)</span>}</div>
                  <div className="truncate text-xs text-zinc-500">{m.email}</div>
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  {isAdmin && !self ? (
                    <select value={m.role} className={field} onChange={(e) => act(() => setMemberRole(m.user_id, e.target.value as Role))}>
                      {ROLES.map((r) => <option key={r} value={r}>{r}</option>)}
                    </select>
                  ) : (
                    <span className="text-xs">{m.role}</span>
                  )}
                  {(isAdmin || self) && (
                    <button className={subtle} onClick={() => {
                      if (confirm(self ? "Leave this organization?" : `Remove ${m.email}?`)) {
                        void act(async () => {
                          await removeMember(m.user_id);
                          // eslint-disable-next-line @next/next/no-location-assign-relative-destination -- a full reload drops everything loaded for the organization just left
                          if (self) window.location.assign("/login");
                        });
                      }
                    }}>
                      {self ? "Leave" : "Remove"}
                    </button>
                  )}
                </div>
              </li>
            );
          })}
        </ul>
      </Section>

      {isAdmin && (
        <Section title="Invite someone">
          <form onSubmit={invite} className="flex flex-wrap gap-2">
            <input value={inviteEmail} onChange={(e) => setInviteEmail(e.target.value)} type="email" required placeholder="email@company.com" className={`${field} min-w-0 flex-1`} />
            <select value={inviteRole} onChange={(e) => setInviteRole(e.target.value as Role)} className={field}>
              {ROLES.filter((r) => atLeast(role, r)).map((r) => <option key={r} value={r}>{r}</option>)}
            </select>
            <button type="submit" className={button}>Create invitation</button>
          </form>
          <p className="text-xs text-zinc-500">{inviteRole}: {ROLE_HELP[inviteRole]}.</p>
          {link && <CopyOnce label="Send them this link (shown once; it works for their email address only):" value={link} />}
          {invitations.filter((i) => !i.accepted_at && !i.revoked_at).length > 0 && (
            <ul className="space-y-1 text-xs">
              {invitations.filter((i) => !i.accepted_at && !i.revoked_at).map((i) => (
                <li key={i.invitation_id} className="flex items-center justify-between">
                  <span>{i.email} · {i.role} · expires {new Date(i.expires_at).toLocaleDateString()}</span>
                  <button className={subtle} onClick={() => act(() => revokeInvitation(i.invitation_id))}>Revoke</button>
                </li>
              ))}
            </ul>
          )}
        </Section>
      )}
    </div>
  );
}

function ApiKeysTab({ role }: { role: Role }) {
  const [keys, setKeys] = useState<ApiKey[]>([]);
  const [name, setName] = useState("");
  const [keyRole, setKeyRole] = useState<Role>("Member");
  const [created, setCreated] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [version, setVersion] = useState(0);
  const load = () => setVersion((v) => v + 1);

  useEffect(() => {
    let cancelled = false;
    listApiKeys().then((k) => !cancelled && setKeys(k)).catch((err) => !cancelled && setError(apiErrorMessage(err)));
    return () => { cancelled = true; };
  }, [version]);

  async function create(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const result = await createApiKey(name || "API key", keyRole);
      setCreated(result.key);
      setName("");
      load();
    } catch (err) {
      setError(apiErrorMessage(err));
    }
  }

  return (
    <div className="space-y-4">
      <Section title="Create an API key">
        <form onSubmit={create} className="flex flex-wrap gap-2">
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Name, e.g. CI or my-app" className={`${field} min-w-0 flex-1`} />
          <select value={keyRole} onChange={(e) => setKeyRole(e.target.value as Role)} className={field}>
            {ROLES.filter((r) => r !== "Owner" && atLeast(role, r)).map((r) => <option key={r} value={r}>{r}</option>)}
          </select>
          <button type="submit" className={button}>Create</button>
        </form>
        {error && <div className="text-sm text-rose-600">{error}</div>}
        {created && <CopyOnce label="Your new key (shown once; store it somewhere safe):" value={created} />}
        <p className="text-xs text-zinc-500">
          Send it as <code>Authorization: Bearer ak_…</code>. The API is described at{" "}
          <a className="text-blue-600 hover:underline" href={`${API_BASE}/openapi/v1.json`} target="_blank" rel="noreferrer">/openapi/v1.json</a>;
          the TypeScript SDK is in <code>sdk/typescript</code>.
        </p>
      </Section>
      <Section title="Keys">
        {keys.length === 0 && <div className="text-sm text-zinc-500">No keys yet.</div>}
        <ul className="divide-y divide-zinc-200 text-sm dark:divide-zinc-800">
          {keys.map((k) => (
            <li key={k.key_id} className="flex items-center justify-between gap-2 py-2">
              <div className="min-w-0">
                <div className="truncate font-medium">{k.name} <span className="text-xs text-zinc-500">· {k.role}</span></div>
                <div className="truncate font-mono text-xs text-zinc-500">
                  {k.display} · created {new Date(k.created_at).toLocaleDateString()} · {k.last_used_at ? `used ${new Date(k.last_used_at).toLocaleString()}` : "never used"}
                </div>
              </div>
              {k.revoked_at ? (
                <span className="text-xs text-rose-600">revoked</span>
              ) : (
                <button className={subtle} onClick={async () => {
                  if (!confirm(`Revoke "${k.name}"? Anything using it stops working.`)) return;
                  try { await revokeApiKey(k.key_id); load(); } catch (err) { setError(apiErrorMessage(err)); }
                }}>
                  Revoke
                </button>
              )}
            </li>
          ))}
        </ul>
      </Section>
    </div>
  );
}

function Meter({ label, used, max, format }: { label: string; used: number; max: number; format: (n: number) => string }) {
  const pct = max > 0 ? Math.min(100, (used / max) * 100) : 0;
  return (
    <div className="space-y-1">
      <div className="flex justify-between text-xs">
        <span>{label}</span>
        <span className="text-zinc-500">{format(used)} / {max > 0 ? format(max) : "unlimited"}</span>
      </div>
      <div className="h-2 rounded bg-zinc-200 dark:bg-zinc-800">
        <div className={`h-2 rounded ${pct >= 100 ? "bg-rose-500" : pct >= 80 ? "bg-amber-500" : "bg-emerald-500"}`} style={{ width: `${max > 0 ? pct : 0}%` }} />
      </div>
    </div>
  );
}

function BillingTab({ role }: { role: Role }) {
  const [billing, setBilling] = useState<BillingView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const isOwner = atLeast(role, "Owner");

  useEffect(() => {
    getBilling().then(setBilling).catch((err) => setError(apiErrorMessage(err)));
  }, []);

  async function go(fn: () => Promise<{ url: string }>) {
    setError(null);
    try {
      window.location.assign((await fn()).url);
    } catch (err) {
      setError(apiErrorMessage(err));
    }
  }

  if (!billing) return <div className="text-sm text-zinc-500">{error ?? "Loading…"}</div>;
  const { plan, usage } = billing;

  return (
    <div className="space-y-4">
      {error && <div className="text-sm text-rose-600">{error}</div>}
      {!billing.quota.allowed && (
        <div className="rounded border border-rose-300 bg-rose-50 p-3 text-sm text-rose-800 dark:border-rose-800 dark:bg-rose-950/50 dark:text-rose-200">
          Agents are paused: {billing.quota.reason}. {billing.paused_agents} agent(s) will carry on when the quota renews next month or the plan is upgraded.
        </div>
      )}
      <Section title={`This month (${usage.period || "—"})`}
        right={billing.billing_enabled && isOwner && billing.subscription.has_billing_account
          ? <button className={subtle} onClick={() => go(openBillingPortal)}>Manage billing</button>
          : undefined}>
        <Meter label="Model tokens" used={usage.tokens} max={plan.monthly_token_limit} format={fmt} />
        <Meter label="Model spend" used={usage.cost_usd} max={plan.monthly_cost_limit_usd} format={(n) => `$${n.toFixed(2)}`} />
        <div className="grid grid-cols-3 gap-2 text-center text-xs">
          <div className="rounded bg-zinc-100 p-2 dark:bg-zinc-900"><div className="text-lg font-semibold">{fmt(usage.llm_calls)}</div>LLM calls</div>
          <div className="rounded bg-zinc-100 p-2 dark:bg-zinc-900"><div className="text-lg font-semibold">{fmt(usage.tool_calls)}</div>tool calls</div>
          <div className="rounded bg-zinc-100 p-2 dark:bg-zinc-900"><div className="text-lg font-semibold">{fmt(usage.agents_created)}</div>agents created</div>
        </div>
      </Section>

      <Section title="Plans">
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {billing.plans.map((p: Plan) => {
            const current = p.id === plan.id;
            return (
              <div key={p.id} className={`space-y-2 rounded border p-3 text-xs ${current ? "border-blue-500" : "border-zinc-200 dark:border-zinc-800"}`}>
                <div className="flex items-baseline justify-between">
                  <span className="text-sm font-semibold">{p.name}</span>
                  <span>{p.price_monthly_usd > 0 ? `$${p.price_monthly_usd}/mo` : current ? "" : "free"}</span>
                </div>
                {p.description && <p className="text-zinc-500">{p.description}</p>}
                <ul className="space-y-0.5 text-zinc-600 dark:text-zinc-300">
                  <li>{limit(p.monthly_token_limit)} tokens / month</li>
                  <li>{p.monthly_cost_limit_usd > 0 ? `$${p.monthly_cost_limit_usd}` : "unlimited"} model spend / month</li>
                  <li>{limit(p.max_workspaces)} workspaces · {limit(p.max_active_agents)} active agents · {limit(p.max_members)} members</li>
                </ul>
                {current ? (
                  <div className="font-medium text-blue-600">Current plan</div>
                ) : billing.billing_enabled && p.purchasable && isOwner ? (
                  <button className={button} onClick={() => go(() => startCheckout(p.id))}>Choose {p.name}</button>
                ) : null}
              </div>
            );
          })}
        </div>
        {!billing.billing_enabled && (
          <p className="text-xs text-zinc-500">Plans on this server are set by its operator.</p>
        )}
      </Section>

      {billing.history.length > 0 && (
        <Section title="Previous months">
          <table className="w-full text-xs">
            <thead className="text-left text-zinc-500"><tr><th>Month</th><th>Tokens</th><th>Spend</th><th>LLM calls</th></tr></thead>
            <tbody>
              {billing.history.map((h) => (
                <tr key={h.period}><td>{h.period}</td><td>{fmt(h.tokens)}</td><td>${h.cost_usd.toFixed(2)}</td><td>{fmt(h.llm_calls)}</td></tr>
              ))}
            </tbody>
          </table>
        </Section>
      )}
    </div>
  );
}

function AccountTab() {
  const { me } = useAuth();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [message, setMessage] = useState<string | null>(null);

  if (me?.via !== "session") {
    return <div className="text-sm text-zinc-500">Accounts are disabled on this server.</div>;
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    try {
      await changePassword(current, next);
      setMessage("Password changed. Other browsers were signed out.");
      setCurrent("");
      setNext("");
    } catch (err) {
      setMessage(apiErrorMessage(err));
    }
  }

  return (
    <Section title={`Account: ${me.user?.email ?? ""}`}>
      <form onSubmit={submit} className="flex max-w-sm flex-col gap-2">
        <input type="password" value={current} onChange={(e) => setCurrent(e.target.value)} placeholder="Current password" autoComplete="current-password" className={field} />
        <input type="password" value={next} onChange={(e) => setNext(e.target.value)} placeholder="New password (10+ characters)" autoComplete="new-password" className={field} />
        <button type="submit" className={button} disabled={!current || !next}>Change password</button>
        {message && <div className="text-xs">{message}</div>}
      </form>
    </Section>
  );
}

export default function SettingsPage() {
  return <Suspense><Settings /></Suspense>;
}

function Settings() {
  const { me } = useAuth();
  // ?tab=api-keys and the like pick the tab; useSearchParams stays current on in-app navigation.
  const urlTab = (useSearchParams().get("tab") as Tab | null) ?? "organization";
  const [tab, setTab] = useState<Tab>(urlTab);
  const [seenUrlTab, setSeenUrlTab] = useState(urlTab);
  if (urlTab !== seenUrlTab) {
    setSeenUrlTab(urlTab);
    setTab(urlTab);
  }
  const role = (me?.role ?? "Viewer") as Role;

  const tabs: [Tab, string, boolean][] = [
    ["organization", "Organization", true],
    ["model", "AI model", true],
    ["api-keys", "API keys", atLeast(role, "Admin")],
    ["billing", "Usage & billing", true],
    ["account", "Account", true],
  ];

  return (
    <div className="flex flex-col">
      <PageHeader title="Settings" description="Your organization and its members, the AI model your agents use, API keys, usage and billing, and your account." />
      <div className="mx-auto flex w-full max-w-5xl flex-1 gap-8 px-6 py-6">
        <aside className="w-40 shrink-0 space-y-1 text-sm">
          {tabs.filter(([, , visible]) => visible).map(([t, label]) => (
            <button key={t} onClick={() => setTab(t)}
              className={`block w-full rounded-lg px-3 py-2 text-left ${tab === t ? "bg-zinc-100 font-medium text-zinc-900 dark:bg-zinc-900 dark:text-zinc-50" : "text-zinc-600 hover:bg-zinc-50 dark:text-zinc-400 dark:hover:bg-zinc-900/60"}`}>
              {label}
            </button>
          ))}
        </aside>
        <main className="min-w-0 flex-1">
          {tab === "organization" && <OrganizationTab role={role} />}
          {tab === "model" && <ModelSettings canEdit={atLeast(role, "Admin")} />}
          {tab === "api-keys" && <ApiKeysTab role={role} />}
          {tab === "billing" && <BillingTab role={role} />}
          {tab === "account" && <AccountTab />}
        </main>
      </div>
    </div>
  );
}
