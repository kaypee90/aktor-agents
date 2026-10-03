"use client";

import { Suspense, useEffect, useState } from "react";
import { useAuth } from "@/components/platform/AuthProvider";
import { ScopePicker, useScope } from "@/components/platform/ScopePicker";
import { addKnowledge, addKnowledgeFiles, apiErrorMessage, getMemoryStatus, searchKnowledge, type KnowledgeEntry } from "@/lib/api";
import { AttachButton, DropZone } from "@/components/files/Attachments";
import { FileChip } from "@/components/files/FilePreview";
import { atLeast, type Role } from "@/lib/platformTypes";
import { Badge, Button, Card, EmptyState, ErrorBanner, Field, Modal, PageHeader, ago, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

/**
 * The organization's shared memory: what agents saved for each other with write_memory, and facts
 * people add. Agents find it with search_knowledge; this page searches it the same way.
 */
export default function KnowledgePage() {
  return <Suspense><Knowledge /></Suspense>;
}

function Knowledge() {
  const { me } = useAuth();
  // The organization's knowledge, or one workspace's own (?workspace=).
  const [scope, setScope] = useScope();
  const canAdd = atLeast((me?.role ?? "Viewer") as Role, "Member");
  const [status, setStatus] = useState<{ semantic: boolean; embedding_model: string | null; mode: string } | null>(null);
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<KnowledgeEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [adding, setAdding] = useState<{ key: string; value: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [open, setOpen] = useState<string | null>(null);
  const [mode, setMode] = useState<"write" | "files">("write");
  const [files, setFiles] = useState<File[]>([]);
  const [fileResults, setFileResults] = useState<{ file_name: string; entries: number; error: string | null }[] | null>(null);

  useEffect(() => {
    getMemoryStatus().then(setStatus).catch(() => setStatus(null));
  }, []);

  // Search as you type, a moment after the last keystroke.
  useEffect(() => {
    const timer = setTimeout(() => {
      searchKnowledge(query, 50, scope).then(setResults).catch((e) => setError(apiErrorMessage(e)));
    }, 250);
    return () => clearTimeout(timer);
  }, [query, scope]);

  function close() {
    setAdding(null);
    setFiles([]);
    setFileResults(null);
    setMode("write");
  }

  async function add() {
    if (!adding) return;
    setBusy(true);
    try {
      if (mode === "files") {
        // Their text becomes searchable passages; files with no text are reported, not added.
        const added = await addKnowledgeFiles(files, scope);
        setFiles([]);
        setResults(await searchKnowledge(query, 50, scope));
        if (added.some((r) => r.error)) {
          setFileResults(added);
          return;
        }
        close();
        return;
      }
      await addKnowledge(adding.key, adding.value, scope);
      close();
      setResults(await searchKnowledge(query, 50, scope));
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <PageHeader
        title="Shared memory"
        description="Knowledge your agents share across tasks and workspaces: findings they saved for each other, and facts you add for them. Agents search it with search_knowledge; nothing here is visible to other organizations."
        actions={canAdd && (
          <Button variant="primary" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => setAdding({ key: "", value: "" })}>Add knowledge</Button>
        )}
      />
      <div className="mx-auto max-w-5xl space-y-4 px-6 py-6">
        <ErrorBanner error={error} onClose={() => setError(null)} />
        <ScopePicker value={scope} onChange={(ws) => { setResults(null); setScope(ws); }} what="knowledge entries" />

        {status && (
          <div className="flex items-center gap-2 text-xs text-zinc-500">
            <Badge tone={status.semantic ? "green" : "neutral"}>{status.semantic ? "Semantic search on" : "Keyword search"}</Badge>
            <span>
              {status.semantic
                ? `Matches by meaning (${status.embedding_model}), words and recency.`
                : "Matches by words and recency. Set EMBEDDING_PROVIDER (Ollama or OpenAI) to also match by meaning."}
            </span>
          </div>
        )}

        <div className="relative">
          <Icons.Search className="pointer-events-none absolute left-3.5 top-3 h-4 w-4 text-zinc-400" />
          <input className={`${inputClass} h-11 pl-10 text-base`} placeholder="Search what your agents know, e.g. why do tenants leave?"
            value={query} onChange={(e) => setQuery(e.target.value)} />
        </div>

        {results === null ? (
          <div className="h-32 animate-pulse rounded-xl bg-zinc-100 dark:bg-zinc-900" />
        ) : results.length === 0 ? (
          <EmptyState icon={<Icons.Knowledge className="h-5 w-5" />}
            title={query ? "Nothing matches" : "No shared knowledge yet"}
            description={query ? "Try other words." : "Agents add findings here as they work, and you can add facts for them."} />
        ) : (
          <div className="space-y-2">
            {results.map((r) => (
              <Card key={r.memory_id} className="p-4">
                <button className="w-full text-left" onClick={() => setOpen(open === r.memory_id ? null : r.memory_id)}>
                  <div className="flex items-start justify-between gap-3">
                    <span className="font-mono text-sm font-semibold text-zinc-900 dark:text-zinc-100">{r.key}</span>
                    <span className="flex shrink-0 items-center gap-2 text-[11px] text-zinc-400">
                      {r.score !== null && query && <Badge tone="brand">{Math.round(r.score * 100)}% match</Badge>}
                      {r.agent_id === "user"
                        ? <Badge>{/\.[a-z0-9]{1,5}( \(part \d+ of \d+\))?$/i.test(r.key) ? "From a file" : "Added by a person"}</Badge>
                        : <span className="font-mono">{r.agent_id}</span>}
                      <span>{ago(r.created_at)}</span>
                    </span>
                  </div>
                  <p className={`mt-1.5 whitespace-pre-wrap text-sm text-zinc-600 dark:text-zinc-400 ${open === r.memory_id ? "" : "line-clamp-2"}`}>{r.value}</p>
                </button>
              </Card>
            ))}
          </div>
        )}
      </div>

      <Modal
        open={adding !== null}
        onClose={close}
        wide={mode === "files"}
        title="Add knowledge"
        description={mode === "write"
          ? `A fact ${scope ? "this workspace's agents" : "every agent of your organization"} can find. Saving the same key again replaces it.`
          : (scope ? "Documents this workspace's agents can search:" : "Documents every agent of your organization can search:") + " PDF, Word, Excel, PowerPoint, CSV, Markdown, text and code. Each is split into passages; adding a file with the same name again replaces it."}
        footer={
          <>
            <Button onClick={close}>{fileResults ? "Done" : "Cancel"}</Button>
            <Button variant="primary" onClick={add}
              disabled={busy || (mode === "write" ? !adding?.key.trim() || !adding?.value.trim() : files.length === 0)}>
              {busy ? (mode === "files" ? "Reading files…" : "Saving…") : mode === "files" ? `Add ${files.length || ""} file${files.length === 1 ? "" : "s"}` : "Save"}
            </Button>
          </>
        }
      >
        <div className="mb-4 flex rounded-lg border border-zinc-200 bg-zinc-50 p-0.5 text-xs dark:border-zinc-700 dark:bg-zinc-900">
          {([["write", "Write"], ["files", "From files"]] as const).map(([id, label]) => (
            <button key={id} onClick={() => setMode(id)}
              className={`flex-1 rounded-md px-3 py-1.5 font-medium ${mode === id ? "bg-white text-zinc-900 shadow-sm dark:bg-zinc-800 dark:text-zinc-100" : "text-zinc-500"}`}>
              {label}
            </button>
          ))}
        </div>
        {mode === "files" && (
          <div className="space-y-3">
            <DropZone onFiles={(f) => setFiles((prev) => [...prev, ...f])} label="Drop files to add them">
              <div className="flex flex-col items-center justify-center gap-2 rounded-2xl border-2 border-dashed border-zinc-200 px-6 py-10 text-center dark:border-zinc-700">
                <Icons.Upload className="h-6 w-6 text-zinc-400" />
                <p className="text-sm text-zinc-600 dark:text-zinc-400">Drag files here, or</p>
                <span className="inline-flex items-center gap-1 rounded-lg border border-zinc-200 bg-white text-sm font-medium dark:border-zinc-700 dark:bg-zinc-900">
                  <AttachButton onFiles={(f) => setFiles((prev) => [...prev, ...f])} />
                  <span className="pr-3">Choose files</span>
                </span>
                <p className="text-[11px] text-zinc-400">Up to 10 files, 25 MB each. Images and scanned PDFs have no text to add.</p>
              </div>
            </DropZone>
            {files.length > 0 && (
              <div className="flex flex-wrap gap-1.5">
                {files.map((f, i) => (
                  <FileChip key={`${f.name}-${i}`} fileName={f.name} sizeBytes={f.size} onRemove={() => setFiles((prev) => prev.filter((_, j) => j !== i))} />
                ))}
              </div>
            )}
            {fileResults && (
              <ul className="space-y-1 rounded-lg bg-zinc-50 p-3 text-xs dark:bg-zinc-950">
                {fileResults.map((r) => (
                  <li key={r.file_name} className={r.error ? "text-amber-700 dark:text-amber-300" : "text-emerald-700 dark:text-emerald-400"}>
                    {r.file_name}: {r.error ?? `added as ${r.entries} searchable passage${r.entries === 1 ? "" : "s"}`}
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
        {adding && mode === "write" && (
          <div className="space-y-4">
            <Field label="Key" hint="A short name, e.g. pricing-policy.">
              <input className={`${inputClass} font-mono`} value={adding.key} maxLength={200} onChange={(e) => setAdding({ ...adding, key: e.target.value })} />
            </Field>
            <Field label="Knowledge">
              <textarea className={inputClass} rows={6} value={adding.value} onChange={(e) => setAdding({ ...adding, value: e.target.value })}
                placeholder="We never discount annual plans by more than 20%." />
            </Field>
          </div>
        )}
      </Modal>
    </div>
  );
}
