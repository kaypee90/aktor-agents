"use client";

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { Markdown } from "@/components/files/Markdown";
import { Icons } from "@/components/ui/icons";
import { cx } from "@/components/ui";
import { DOCS } from "./content";

/**
 * Public documentation: what the platform does and how to use it, from a first task to shipping
 * your own solution. One page with a section index and search; the repository's docs/ folder has
 * the full reference for each area.
 */
export default function DocsPage() {
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(DOCS[0].id);

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    return q ? DOCS.filter((d) => `${d.title} ${d.summary} ${d.body}`.toLowerCase().includes(q)) : DOCS;
  }, [query]);
  const groups = [...new Set(shown.map((d) => d.group))];

  // Highlight the section being read.
  useEffect(() => {
    const observer = new IntersectionObserver(
      (entries) => {
        const visible = entries.filter((e) => e.isIntersecting).sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top)[0];
        if (visible) setActive(visible.target.id);
      },
      { root: document.getElementById("docs-scroll"), rootMargin: "-80px 0px -70% 0px" },
    );
    for (const d of shown) {
      const el = document.getElementById(d.id);
      if (el) observer.observe(el);
    }
    return () => observer.disconnect();
  }, [shown]);

  return (
    <div className="h-full overflow-y-auto bg-white dark:bg-zinc-950" id="docs-scroll">
      <header className="sticky top-0 z-20 border-b border-zinc-200/70 bg-white/85 backdrop-blur dark:border-zinc-800/70 dark:bg-zinc-950/85">
        <div className="mx-auto flex max-w-7xl items-center gap-3 px-4 py-3 sm:px-6">
          <Link href="/welcome" className="flex items-center gap-2.5">
            <Icons.Logo className="h-8 w-8" />
            <span className="text-base font-semibold tracking-tight">Aktor</span>
          </Link>
          <span className="rounded-md bg-zinc-100 px-2 py-0.5 text-xs font-medium text-zinc-600 dark:bg-zinc-800 dark:text-zinc-300">Docs</span>
          <div className="relative ml-auto w-full max-w-xs">
            <Icons.Search className="pointer-events-none absolute left-2.5 top-2.5 h-4 w-4 text-zinc-400" />
            <input
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Search the docs"
              aria-label="Search the docs"
              className="w-full rounded-lg border border-zinc-200 bg-white py-2 pl-8 pr-3 text-sm outline-none focus:border-brand-400 focus:ring-2 focus:ring-brand-500/20 dark:border-zinc-800 dark:bg-zinc-950"
            />
          </div>
          <Link href="/" className="hidden rounded-lg bg-brand-500 px-3.5 py-2 text-sm font-medium text-white shadow-sm hover:bg-brand-600 sm:inline-flex">
            Open dashboard
          </Link>
        </div>
      </header>

      <div className="mx-auto grid max-w-7xl gap-10 px-4 py-10 sm:px-6 lg:grid-cols-[15rem_minmax(0,1fr)]">
        <nav aria-label="Sections" className="lg:sticky lg:top-20 lg:max-h-[calc(100vh-6rem)] lg:self-start lg:overflow-y-auto">
          {groups.map((g) => (
            <div key={g} className="mb-6">
              <div className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-zinc-400">{g}</div>
              <ul className="space-y-0.5">
                {shown.filter((d) => d.group === g).map((d) => (
                  <li key={d.id}>
                    <a href={`#${d.id}`}
                      className={cx("block rounded-md px-2.5 py-1.5 text-sm transition-colors",
                        active === d.id ? "bg-brand-50 font-medium text-brand-700 dark:bg-brand-500/10 dark:text-brand-300"
                          : "text-zinc-600 hover:bg-zinc-100 hover:text-zinc-900 dark:text-zinc-400 dark:hover:bg-zinc-900 dark:hover:text-zinc-100")}>
                      {d.title}
                    </a>
                  </li>
                ))}
              </ul>
            </div>
          ))}
          {shown.length === 0 && <p className="text-sm text-zinc-500">Nothing matches “{query}”.</p>}
        </nav>

        <main className="min-w-0 max-w-3xl">
          {!query && (
            <div className="mb-12 rounded-2xl border border-zinc-200 bg-gradient-to-br from-brand-50 to-white p-6 dark:border-zinc-800 dark:from-brand-500/10 dark:to-zinc-950">
              <h1 className="text-3xl font-semibold tracking-tight text-zinc-900 dark:text-zinc-50">Aktor documentation</h1>
              <p className="mt-2 text-zinc-600 dark:text-zinc-400">
                Autonomous agent teams on a runtime that governs them, and a platform to build your own agentic products on.
                Start with <a href="#overview" className="text-brand-600 underline dark:text-brand-400">what it is</a>, run{" "}
                <a href="#getting-started" className="text-brand-600 underline dark:text-brand-400">your first task</a>, or jump to{" "}
                <a href="#solutions" className="text-brand-600 underline dark:text-brand-400">building a solution</a>.
              </p>
              <div className="mt-5 grid gap-3 sm:grid-cols-3">
                {[
                  { href: "#tasks", icon: Icons.Tasks, title: "Use it", body: "Tasks, workspaces, studies" },
                  { href: "#solutions", icon: Icons.Solutions, title: "Build on it", body: "Solutions, end users, registry" },
                  { href: "#api", icon: Icons.Book, title: "Reference", body: "API, guarantees, settings" },
                ].map((c) => (
                  <a key={c.title} href={c.href} className="rounded-xl border border-zinc-200 bg-white p-4 transition hover:border-brand-300 dark:border-zinc-800 dark:bg-zinc-900 dark:hover:border-brand-700">
                    <c.icon className="h-4 w-4 text-brand-600 dark:text-brand-400" />
                    <div className="mt-2 text-sm font-semibold text-zinc-900 dark:text-zinc-100">{c.title}</div>
                    <div className="text-xs text-zinc-500">{c.body}</div>
                  </a>
                ))}
              </div>
            </div>
          )}

          {shown.map((d) => (
            <section key={d.id} id={d.id} className="mb-14 scroll-mt-24">
              <div className="mb-1 text-[11px] font-semibold uppercase tracking-wider text-brand-600 dark:text-brand-400">{d.group}</div>
              <h2 className="text-2xl font-semibold tracking-tight text-zinc-900 dark:text-zinc-50">
                <a href={`#${d.id}`} className="hover:underline">{d.title}</a>
              </h2>
              <p className="mt-1 text-sm text-zinc-500">{d.summary}</p>
              <div className="mt-5 [&_pre]:border [&_pre]:border-zinc-200 dark:[&_pre]:border-zinc-800 dark:[&_pre]:bg-zinc-900">
                <Markdown text={d.body.trim()} />
              </div>
            </section>
          ))}

          <footer className="border-t border-zinc-200 pt-6 text-sm text-zinc-500 dark:border-zinc-800">
            The full reference for each area is in the repository’s <code className="rounded bg-zinc-100 px-1 font-mono text-xs dark:bg-zinc-800">docs/</code> folder,
            and the API description at <code className="rounded bg-zinc-100 px-1 font-mono text-xs dark:bg-zinc-800">/openapi/v1.json</code>.
          </footer>
        </main>
      </div>
    </div>
  );
}
