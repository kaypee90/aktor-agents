"use client";

import { Suspense, useEffect, useRef, useState } from "react";
import { useAuth } from "@/components/platform/AuthProvider";
import { ScopePicker, useScope } from "@/components/platform/ScopePicker";
import {
  apiErrorMessage,
  createSkill,
  deleteSkill,
  getSkill,
  listSkills,
  setSkillEnabled,
  skillDownloadUrl,
  updateSkill,
  uploadSkill,
  type SkillFile,
  type SkillSummary,
} from "@/lib/api";
import { atLeast } from "@/lib/platformTypes";
import type { Role } from "@/lib/platformTypes";
import { Badge, Button, Card, EmptyState, ErrorBanner, Field, Modal, PageHeader, Toggle, ago, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

const TEMPLATE = `## When to use this
Describe the situations this skill is for.

## Steps
1. …
2. …

## Output
What a good result looks like.`;

type Draft = { original: string | null; name: string; description: string; instructions: string; files: SkillFile[] };

/**
 * The organization's skills: write one in the editor, or upload a SKILL.md / .zip. Every agent
 * sees the enabled skills' names and descriptions and loads one when its work matches.
 */
export default function SkillsPage() {
  return <Suspense><Skills /></Suspense>;
}

function Skills() {
  const { me } = useAuth();
  // The organization's skills, or one workspace's own (?workspace=).
  const [scope, setScope] = useScope();
  const canEdit = atLeast((me?.role ?? "Viewer") as Role, "Admin");
  const [skills, setSkills] = useState<SkillSummary[] | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [query, setQuery] = useState("");
  const fileInput = useRef<HTMLInputElement>(null);

  const load = () => listSkills(scope).then(setSkills).catch((e) => setError(apiErrorMessage(e)));
  useEffect(() => {
    listSkills(scope).then(setSkills).catch((e) => setError(apiErrorMessage(e)));
  }, [scope]);

  async function act(fn: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try {
      await fn();
      await load();
      return true;
    } catch (e) {
      setError(apiErrorMessage(e));
      return false;
    } finally {
      setBusy(false);
    }
  }

  async function edit(name: string) {
    const s = await getSkill(name, scope);
    setDraft({ original: s.name, name: s.name, description: s.description, instructions: s.instructions, files: s.files });
  }

  async function save() {
    if (!draft) return;
    const ok = await act(() =>
      draft.original
        ? updateSkill(draft.original, { description: draft.description, instructions: draft.instructions, files: draft.files }, scope)
        : createSkill({ name: draft.name, description: draft.description, instructions: draft.instructions, files: draft.files }, scope),
    );
    if (ok) setDraft(null);
  }

  async function upload(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (file) await act(() => uploadSkill(file, scope));
  }

  const setFile = (i: number, patch: Partial<SkillFile>) =>
    setDraft((d) => (d ? { ...d, files: d.files.map((f, j) => (j === i ? { ...f, ...patch } : f)) } : d));

  const shown = (skills ?? []).filter((s) => !query || `${s.name} ${s.description}`.toLowerCase().includes(query.toLowerCase()));

  return (
    <div>
      <PageHeader
        title="Skills"
        description="Teach your agents how your organization does particular work. Agents see each skill's name and description, and load the full instructions when their work matches. Skills are instructions only: they never grant tools, budget or access."
        actions={canEdit && (
          <>
            <input ref={fileInput} type="file" accept=".md,.zip,text/markdown,application/zip" className="hidden" onChange={upload} />
            <Button icon={<Icons.Upload className="h-3.5 w-3.5" />} disabled={busy} onClick={() => fileInput.current?.click()}>Upload .md / .zip</Button>
            <Button variant="primary" icon={<Icons.Plus className="h-3.5 w-3.5" />} disabled={busy}
              onClick={() => setDraft({ original: null, name: "", description: "", instructions: TEMPLATE, files: [] })}>
              Write a skill
            </Button>
          </>
        )}
      />

      <div className="mx-auto max-w-5xl space-y-4 px-6 py-6">
        <ErrorBanner error={error} onClose={() => setError(null)} />
        <ScopePicker value={scope} onChange={(ws) => { setSkills(null); setScope(ws); }} what="skills" />
        {!canEdit && <div className="text-xs text-zinc-500">You can read skills; Admins can add and change them.</div>}

        {skills && skills.length > 0 && (
          <div className="relative max-w-sm">
            <Icons.Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-zinc-400" />
            <input className={`${inputClass} pl-9`} placeholder="Filter skills" value={query} onChange={(e) => setQuery(e.target.value)} />
          </div>
        )}

        {skills === null ? (
          <div className="h-32 animate-pulse rounded-xl bg-zinc-100 dark:bg-zinc-900" />
        ) : skills.length === 0 ? (
          <EmptyState
            icon={<Icons.Skills className="h-5 w-5" />}
            title={scope ? "No skills for this workspace yet" : "No skills yet"}
            description="A skill is a SKILL.md: a name, a description of when to use it, and instructions. Write one here or upload one you already have."
            action={canEdit && <Button variant="primary" onClick={() => setDraft({ original: null, name: "", description: "", instructions: TEMPLATE, files: [] })}>Write your first skill</Button>}
          />
        ) : (
          <div className="grid gap-3 md:grid-cols-2">
            {shown.map((s) => (
              <Card key={s.name} className="flex flex-col p-4">
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0">
                    <div className="flex items-center gap-2">
                      <Icons.Skills className="h-4 w-4 text-brand-500" />
                      <span className="truncate font-mono text-sm font-semibold text-zinc-900 dark:text-zinc-100">{s.name}</span>
                    </div>
                    <div className="mt-1 flex flex-wrap gap-1.5">
                      <Badge>v{s.version}</Badge>
                      {s.file_count > 0 && <Badge>{s.file_count} file{s.file_count === 1 ? "" : "s"}</Badge>}
                      {!s.enabled && <Badge tone="amber">Off</Badge>}
                    </div>
                  </div>
                  {canEdit && (
                    <Toggle checked={s.enabled} label="" onChange={(v) => act(() => setSkillEnabled(s.name, v, scope))} disabled={busy} />
                  )}
                </div>
                <p className="mt-3 line-clamp-3 flex-1 text-sm text-zinc-600 dark:text-zinc-400">{s.description}</p>
                <div className="mt-4 flex items-center justify-between border-t border-zinc-100 pt-3 dark:border-zinc-800">
                  <span className="text-[11px] text-zinc-400">Updated {ago(s.updated_at)}</span>
                  <div className="flex gap-1">
                    <a href={skillDownloadUrl(s.name, scope)}><Button size="sm" variant="ghost" icon={<Icons.Download className="h-3.5 w-3.5" />}>Download</Button></a>
                    {canEdit && <Button size="sm" variant="ghost" onClick={() => edit(s.name)}>Edit</Button>}
                    {canEdit && (
                      <Button size="sm" variant="ghost" className="text-rose-600 dark:text-rose-400" disabled={busy}
                        onClick={() => confirm(`Delete the skill '${s.name}'?`) && act(() => deleteSkill(s.name, scope))}>
                        Delete
                      </Button>
                    )}
                  </div>
                </div>
              </Card>
            ))}
          </div>
        )}
      </div>

      <Modal
        open={draft !== null}
        onClose={() => setDraft(null)}
        wide
        title={draft?.original ? `Edit ${draft.original}` : "Write a skill"}
        description="The same format as a SKILL.md. Saving an existing skill keeps the old one as history and makes this the new version."
        footer={
          <>
            <Button onClick={() => setDraft(null)}>Cancel</Button>
            <Button variant="primary" onClick={save}
              disabled={busy || !draft?.name.trim() || !draft?.description.trim() || !draft?.instructions.trim()}>
              {busy ? "Saving…" : draft?.original ? "Save new version" : "Create skill"}
            </Button>
          </>
        }
      >
        {draft && (
          <div className="space-y-4">
            <div className="grid gap-4 md:grid-cols-3">
              <Field label="Name" hint="Lowercase letters, digits, hyphens.">
                <input className={`${inputClass} font-mono`} value={draft.name} disabled={draft.original !== null} placeholder="incident-postmortems"
                  onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
              </Field>
              <Field label="Description" hint="What it does and when to use it: agents decide from this alone." className="md:col-span-2">
                <input className={inputClass} value={draft.description} maxLength={1024}
                  placeholder="How we write incident postmortems. Use when asked for a postmortem or incident write-up."
                  onChange={(e) => setDraft({ ...draft, description: e.target.value })} />
              </Field>
            </div>
            <Field label="Instructions (Markdown)">
              <textarea className={`${inputClass} font-mono text-xs`} rows={14} value={draft.instructions}
                onChange={(e) => setDraft({ ...draft, instructions: e.target.value })} />
            </Field>
            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <span className="text-xs font-medium text-zinc-700 dark:text-zinc-300">Resource files <span className="font-normal text-zinc-500">(templates, reference notes the instructions point to)</span></span>
                <Button size="sm" icon={<Icons.Plus className="h-3 w-3" />} onClick={() => setDraft({ ...draft, files: [...draft.files, { path: "", content: "" }] })}>Add file</Button>
              </div>
              {draft.files.map((f, i) => (
                <div key={i} className="space-y-2 rounded-lg border border-zinc-200 p-3 dark:border-zinc-800">
                  <div className="flex gap-2">
                    <input className={`${inputClass} font-mono text-xs`} value={f.path} placeholder="reference/template.md" onChange={(e) => setFile(i, { path: e.target.value })} />
                    <Button size="sm" variant="ghost" onClick={() => setDraft({ ...draft, files: draft.files.filter((_, j) => j !== i) })}><Icons.X className="h-3.5 w-3.5" /></Button>
                  </div>
                  <textarea className={`${inputClass} font-mono text-xs`} rows={5} value={f.content} onChange={(e) => setFile(i, { content: e.target.value })} />
                </div>
              ))}
            </div>
          </div>
        )}
      </Modal>
    </div>
  );
}
