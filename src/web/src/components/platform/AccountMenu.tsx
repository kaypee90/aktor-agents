"use client";

import Link from "next/link";
import { useState } from "react";
import { signOut, switchOrganization } from "@/lib/api";
import { useAuth } from "./AuthProvider";

/** Organization switcher, settings and sign-out, for every page's header. */
export function AccountMenu() {
  const { me } = useAuth();
  const [busy, setBusy] = useState(false);
  if (!me?.authenticated) return null;

  const orgs = me.organizations ?? [];
  const current = orgs.find((o) => o.tenant_id === me.tenant_id);

  async function switchTo(tenantId: string) {
    setBusy(true);
    try {
      await switchOrganization(tenantId);
      // Everything on screen belongs to the old organization: start clean.
      // eslint-disable-next-line @next/next/no-location-assign-relative-destination -- a full reload drops everything loaded for the previous organization
      window.location.assign("/workspaces");
    } finally {
      setBusy(false);
    }
  }

  async function logout() {
    setBusy(true);
    try {
      await signOut();
    } finally {
      // eslint-disable-next-line @next/next/no-location-assign-relative-destination -- a full reload drops everything loaded for the previous organization
      window.location.assign("/login");
    }
  }

  return (
    <div className="flex items-center gap-2 border-l border-neutral-200 pl-2 text-xs dark:border-neutral-800">
      {orgs.length > 1 ? (
        <select
          aria-label="Organization"
          value={me.tenant_id}
          disabled={busy}
          onChange={(e) => switchTo(e.target.value)}
          className="max-w-40 rounded border border-neutral-300 bg-white px-1.5 py-1 dark:border-neutral-700 dark:bg-neutral-900"
        >
          {orgs.map((o) => <option key={o.tenant_id} value={o.tenant_id}>{o.name}</option>)}
        </select>
      ) : (
        <span className="max-w-40 truncate font-medium" title={current?.name}>{current?.name ?? "Organization"}</span>
      )}
      <span className="rounded bg-neutral-100 px-1.5 py-0.5 text-[10px] uppercase tracking-wide text-neutral-500 dark:bg-neutral-800">{me.role}</span>
      <Link href="/settings" className="rounded px-2 py-1 text-neutral-600 hover:bg-neutral-100 dark:text-neutral-300 dark:hover:bg-neutral-800">
        Settings
      </Link>
      {me.via === "session" && (
        <button onClick={logout} disabled={busy} title={me.user?.email ?? undefined}
          className="rounded px-2 py-1 text-neutral-600 hover:bg-neutral-100 disabled:opacity-50 dark:text-neutral-300 dark:hover:bg-neutral-800">
          Sign out
        </button>
      )}
    </div>
  );
}
