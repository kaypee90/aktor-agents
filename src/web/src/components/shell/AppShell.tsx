"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useState } from "react";
import { signOut, switchOrganization } from "@/lib/api";
import { useAuth } from "@/components/platform/AuthProvider";
import { Icons } from "@/components/ui/icons";
import { cx } from "@/components/ui";
import { ApprovalToasts, ApprovalsBell, useApprovals } from "./ApprovalsCenter";
import { useTheme, type ThemeChoice } from "./theme";

type NavItem = { href: string; label: string; icon: (p: { className?: string }) => React.ReactNode; match?: string[] };

const NAV: { section: string; items: NavItem[] }[] = [
  {
    section: "Build",
    items: [
      { href: "/", label: "Tasks", icon: Icons.Tasks },
      { href: "/workspaces", label: "Workspaces", icon: Icons.Workspaces },
      { href: "/templates", label: "Templates", icon: Icons.Templates },
    ],
  },
  {
    section: "Knowledge",
    items: [
      { href: "/skills", label: "Skills", icon: Icons.Skills },
      { href: "/knowledge", label: "Shared memory", icon: Icons.Knowledge },
    ],
  },
  {
    section: "Observe",
    items: [
      { href: "/analytics", label: "Analytics", icon: Icons.Analytics },
      { href: "/runs", label: "Run history", icon: Icons.Runs, match: ["/replay"] },
      { href: "/studies", label: "Studies", icon: Icons.Simulation, match: ["/simulation"] },
    ],
  },
  {
    section: "Connect",
    items: [{ href: "/connect", label: "Integrations & API", icon: Icons.Connect }],
  },
];

const PUBLIC = ["/login", "/welcome"];
const COLLAPSED_KEY = "aktor:navCollapsed";

function readCollapsed() {
  try {
    return typeof window !== "undefined" && localStorage.getItem(COLLAPSED_KEY) === "1";
  } catch {
    return false;
  }
}

/**
 * The frame around every signed-in page: a sidebar with the product's sections, the organization,
 * the account and the theme; the page fills the rest. Collapsible on desktop, a drawer on phones.
 */
export function AppShell({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const [collapsed, setCollapsed] = useState(readCollapsed);
  const [mobileOpen, setMobileOpen] = useState(false);
  const approvals = useApprovals();

  if (PUBLIC.includes(pathname)) return <>{children}</>;

  const toggleCollapsed = () => {
    setCollapsed((c) => {
      try {
        localStorage.setItem(COLLAPSED_KEY, c ? "0" : "1");
      } catch {
        // Not remembered; fine.
      }
      return !c;
    });
  };

  const isActive = (item: NavItem) =>
    item.href === "/" ? pathname === "/" : pathname.startsWith(item.href) || (item.match ?? []).some((m) => pathname.startsWith(m));

  const sidebar = (
    <nav className={cx("flex h-full flex-col border-r border-zinc-200 bg-zinc-50/80 dark:border-zinc-800/80 dark:bg-zinc-950", collapsed ? "w-16" : "w-60")}>
      <div className="flex h-14 items-center gap-2.5 px-4">
        <Icons.Logo className="h-7 w-7 shrink-0" />
        {!collapsed && (
          <div className="min-w-0">
            <div className="text-sm font-semibold tracking-tight text-zinc-900 dark:text-zinc-50">Aktor</div>
            <div className="text-[10px] uppercase tracking-wider text-zinc-500">Governed agent teams</div>
          </div>
        )}
      </div>

      <div className="flex-1 space-y-5 overflow-y-auto px-2 py-3">
        {NAV.map((group) => (
          <div key={group.section}>
            {!collapsed && <div className="px-2.5 pb-1.5 text-[10px] font-semibold uppercase tracking-wider text-zinc-400 dark:text-zinc-500">{group.section}</div>}
            <ul className="space-y-0.5">
              {group.items.map((item) => {
                const active = isActive(item);
                return (
                  <li key={item.href}>
                    <Link
                      href={item.href}
                      onClick={() => setMobileOpen(false)}
                      title={collapsed ? item.label : undefined}
                      className={cx(
                        "group flex items-center gap-2.5 rounded-lg px-2.5 py-2 text-sm transition-colors",
                        active
                          ? "bg-white font-medium text-zinc-900 shadow-sm ring-1 ring-zinc-200 dark:bg-zinc-900 dark:text-zinc-50 dark:ring-zinc-800"
                          : "text-zinc-600 hover:bg-zinc-100 hover:text-zinc-900 dark:text-zinc-400 dark:hover:bg-zinc-900/70 dark:hover:text-zinc-100",
                        collapsed && "justify-center",
                      )}
                    >
                      <item.icon className={cx("h-4 w-4 shrink-0", active ? "text-brand-500" : "text-zinc-400 group-hover:text-zinc-600 dark:group-hover:text-zinc-300")} />
                      {!collapsed && item.label}
                    </Link>
                  </li>
                );
              })}
            </ul>
          </div>
        ))}
      </div>

      <div className="space-y-1 border-t border-zinc-200 p-2 dark:border-zinc-800/80">
        <ApprovalsBell state={approvals} collapsed={collapsed} />
        <Link
          href="/settings"
          onClick={() => setMobileOpen(false)}
          title={collapsed ? "Settings" : undefined}
          className={cx(
            "flex items-center gap-2.5 rounded-lg px-2.5 py-2 text-sm",
            pathname.startsWith("/settings")
              ? "bg-white font-medium text-zinc-900 ring-1 ring-zinc-200 dark:bg-zinc-900 dark:text-zinc-50 dark:ring-zinc-800"
              : "text-zinc-600 hover:bg-zinc-100 dark:text-zinc-400 dark:hover:bg-zinc-900/70",
            collapsed && "justify-center",
          )}
        >
          <Icons.Settings className="h-4 w-4 shrink-0 text-zinc-400" />
          {!collapsed && "Settings"}
        </Link>
        {!collapsed && <ThemeSwitch />}
        <AccountCard collapsed={collapsed} />
        <button
          onClick={toggleCollapsed}
          className="hidden w-full items-center justify-center rounded-lg py-1.5 text-zinc-400 hover:bg-zinc-100 hover:text-zinc-600 lg:flex dark:hover:bg-zinc-900"
          title={collapsed ? "Expand the sidebar" : "Collapse the sidebar"}
        >
          <Icons.ChevronRight className={cx("h-4 w-4 transition-transform", !collapsed && "rotate-180")} />
        </button>
      </div>
    </nav>
  );

  return (
    <div className="flex h-full min-h-0 flex-1">
      <div className="hidden h-full shrink-0 lg:block">{sidebar}</div>

      {mobileOpen && (
        <div className="fixed inset-0 z-40 flex lg:hidden">
          <div className="h-full">{sidebar}</div>
          <button className="flex-1 bg-black/40" aria-label="Close the menu" onClick={() => setMobileOpen(false)} />
        </div>
      )}

      <div className="flex min-w-0 flex-1 flex-col">
        <div className="flex h-12 items-center gap-2 border-b border-zinc-200 px-3 lg:hidden dark:border-zinc-800">
          <button onClick={() => setMobileOpen(true)} className="relative rounded-md p-1.5 text-zinc-500 hover:bg-zinc-100 dark:hover:bg-zinc-900" aria-label="Open the menu">
            <Icons.Menu className="h-5 w-5" />
            {approvals.pending.length > 0 && (
              <span className="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-rose-600 px-1 text-[10px] font-bold text-white">
                {approvals.pending.length}
              </span>
            )}
          </button>
          <Icons.Logo className="h-6 w-6" />
          <span className="text-sm font-semibold">Aktor</span>
        </div>
        <main className="min-h-0 flex-1 overflow-y-auto">{children}</main>
      </div>
      <ApprovalToasts state={approvals} />
    </div>
  );
}

function ThemeSwitch() {
  const [theme, setTheme] = useTheme();
  const options: [ThemeChoice, React.ReactNode, string][] = [
    ["light", <Icons.Sun key="l" className="h-3.5 w-3.5" />, "Light"],
    ["dark", <Icons.Moon key="d" className="h-3.5 w-3.5" />, "Dark"],
    ["system", <Icons.Monitor key="s" className="h-3.5 w-3.5" />, "System"],
  ];
  return (
    <div className="flex items-center justify-between rounded-lg px-2.5 py-1">
      <span className="text-xs text-zinc-500">Theme</span>
      <div className="flex rounded-md bg-zinc-200/60 p-0.5 dark:bg-zinc-900">
        {options.map(([value, icon, label]) => (
          <button
            key={value}
            title={label}
            onClick={() => setTheme(value)}
            className={cx(
              "rounded p-1 transition-colors",
              theme === value ? "bg-white text-zinc-900 shadow-sm dark:bg-zinc-700 dark:text-zinc-50" : "text-zinc-500 hover:text-zinc-800 dark:hover:text-zinc-200",
            )}
          >
            {icon}
          </button>
        ))}
      </div>
    </div>
  );
}

/** Who's signed in, which organization, a switcher when there are several, and sign-out. */
function AccountCard({ collapsed }: { collapsed: boolean }) {
  const { me } = useAuth();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  if (!me) return null;

  const orgs = me.organizations ?? [];
  const current = orgs.find((o) => o.tenant_id === me.tenant_id);
  const name = me.user?.email || (me.via === "api_key" ? "API key" : "Local user");
  const initials = name.split(/[\s@.]+/).filter(Boolean).slice(0, 2).map((p) => p[0]?.toUpperCase()).join("") || "A";

  async function switchTo(tenantId: string) {
    setBusy(true);
    try {
      await switchOrganization(tenantId);
      // eslint-disable-next-line @next/next/no-location-assign-relative-destination -- a full reload drops everything loaded for the previous organization
      window.location.assign("/");
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
    <div className="relative">
      <button
        onClick={() => setOpen((o) => !o)}
        className={cx("flex w-full items-center gap-2.5 rounded-lg p-2 text-left hover:bg-zinc-100 dark:hover:bg-zinc-900", collapsed && "justify-center")}
      >
        <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-gradient-to-br from-brand-400 to-brand-700 text-[11px] font-semibold text-white">
          {initials}
        </span>
        {!collapsed && (
          <span className="min-w-0 flex-1">
            <span className="block truncate text-xs font-medium text-zinc-800 dark:text-zinc-200">{current?.name ?? "Organization"}</span>
            <span className="block truncate text-[11px] text-zinc-500">{name} · {me.role}</span>
          </span>
        )}
        {!collapsed && <Icons.ChevronDown className="h-3.5 w-3.5 text-zinc-400" />}
      </button>

      {open && (
        <div className="absolute bottom-full left-0 z-50 mb-1 w-60 rounded-xl border border-zinc-200 bg-white p-1.5 shadow-xl dark:border-zinc-800 dark:bg-zinc-900">
          {orgs.length > 1 && (
            <>
              <div className="px-2 pb-1 pt-1.5 text-[10px] font-semibold uppercase tracking-wider text-zinc-400">Organizations</div>
              {orgs.map((o) => (
                <button key={o.tenant_id} disabled={busy || o.tenant_id === me.tenant_id} onClick={() => switchTo(o.tenant_id)}
                  className="flex w-full items-center justify-between rounded-md px-2 py-1.5 text-left text-sm hover:bg-zinc-100 disabled:cursor-default dark:hover:bg-zinc-800">
                  <span className="truncate">{o.name}</span>
                  {o.tenant_id === me.tenant_id && <Icons.Check className="h-3.5 w-3.5 text-brand-500" />}
                </button>
              ))}
              <div className="my-1 border-t border-zinc-200 dark:border-zinc-800" />
            </>
          )}
          <Link href="/settings" onClick={() => setOpen(false)} className="flex items-center gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-zinc-100 dark:hover:bg-zinc-800">
            <Icons.Settings className="h-3.5 w-3.5 text-zinc-400" /> Organization settings
          </Link>
          {me.via === "session" && (
            <button onClick={logout} disabled={busy} className="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm text-rose-600 hover:bg-zinc-100 dark:text-rose-400 dark:hover:bg-zinc-800">
              <Icons.LogOut className="h-3.5 w-3.5" /> Sign out
            </button>
          )}
        </div>
      )}
    </div>
  );
}
