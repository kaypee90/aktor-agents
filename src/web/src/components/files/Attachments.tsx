"use client";

import { useCallback, useRef, useState } from "react";
import { apiErrorMessage } from "@/lib/api";
import { cx } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import { FileChip } from "./FilePreview";

/** A file picked for the next message: uploading, uploaded (with its id), or failed. */
export type PendingFile = { key: string; name: string; size: number; id?: string; error?: string };

/**
 * Files attached to a message being written. Each one uploads as soon as it's picked, so sending
 * doesn't wait; `upload` sends one file and returns its id.
 */
export function useAttachments(upload: (file: File) => Promise<string>) {
  const [pending, setPending] = useState<PendingFile[]>([]);

  const add = useCallback(async (files: File[]) => {
    const added = files.map((f) => ({ key: `${f.name}-${f.size}-${Math.random().toString(36).slice(2)}`, name: f.name, size: f.size }));
    setPending((p) => [...p, ...added]);
    await Promise.all(files.map(async (file, i) => {
      const key = added[i].key;
      try {
        const id = await upload(file);
        setPending((p) => p.map((x) => (x.key === key ? { ...x, id } : x)));
      } catch (e) {
        setPending((p) => p.map((x) => (x.key === key ? { ...x, error: apiErrorMessage(e) } : x)));
      }
    }));
  }, [upload]);

  return {
    pending,
    add,
    remove: (key: string) => setPending((p) => p.filter((x) => x.key !== key)),
    clear: () => setPending([]),
    uploading: pending.some((p) => !p.id && !p.error),
    ids: pending.filter((p) => p.id).map((p) => p.id!),
    errors: pending.filter((p) => p.error),
  };
}

/** The paperclip: opens the file picker (any type, several at once). */
export function AttachButton({ onFiles, className }: { onFiles: (files: File[]) => void; className?: string }) {
  const input = useRef<HTMLInputElement>(null);
  return (
    <>
      <input ref={input} type="file" multiple hidden onChange={(e) => { onFiles(Array.from(e.target.files ?? [])); e.target.value = ""; }} />
      <button type="button" onClick={() => input.current?.click()}
        title="Attach files: documents, spreadsheets, PDFs, slides, images, code, data"
        className={cx("inline-flex items-center justify-center rounded-lg p-2 text-zinc-500 hover:bg-zinc-100 hover:text-zinc-800 dark:hover:bg-zinc-800 dark:hover:text-zinc-200", className)}>
        <Icons.Paperclip className="h-[18px] w-[18px]" />
      </button>
    </>
  );
}

/** A labelled button that opens the file picker: the whole button is clickable, not just its icon. */
export function ChooseFilesButton({ onFiles, children, accept, disabled, className }: {
  onFiles: (files: File[]) => void; children: React.ReactNode; accept?: string; disabled?: boolean; className?: string;
}) {
  return (
    <label className={cx("inline-flex items-center gap-1.5 rounded-lg border border-zinc-200 bg-white px-3 py-1.5 text-sm font-medium text-zinc-700 shadow-sm transition dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-200",
      disabled ? "cursor-not-allowed opacity-60" : "cursor-pointer hover:bg-zinc-50 dark:hover:bg-zinc-800", className)}>
      <Icons.Paperclip className="h-4 w-4 text-zinc-500" />
      {children}
      <input type="file" multiple accept={accept} disabled={disabled} className="sr-only"
        onChange={(e) => { onFiles(Array.from(e.target.files ?? [])); e.target.value = ""; }} />
    </label>
  );
}

/** A dashed area that takes files both ways: dropped on it, or picked after a click anywhere on it. */
export function FilePickArea({ onFiles, title, hint, accept, disabled, label = "Drop files to add them" }: {
  onFiles: (files: File[]) => void; title: React.ReactNode; hint?: React.ReactNode; accept?: string; disabled?: boolean; label?: string;
}) {
  return (
    <DropZone onFiles={(f) => { if (!disabled) onFiles(f); }} label={label}>
      <label className={cx("flex flex-col items-center justify-center gap-2 rounded-2xl border-2 border-dashed border-zinc-200 px-6 py-10 text-center transition dark:border-zinc-700",
        disabled ? "cursor-not-allowed opacity-60" : "cursor-pointer hover:border-brand-300 hover:bg-brand-50/40 dark:hover:border-brand-800 dark:hover:bg-brand-950/20")}>
        <Icons.Upload className="h-6 w-6 text-zinc-400" />
        <span className="text-sm text-zinc-600 dark:text-zinc-400">{title}</span>
        <span className="inline-flex items-center gap-1.5 rounded-lg border border-zinc-200 bg-white px-3 py-1.5 text-sm font-medium text-zinc-700 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-200">
          <Icons.Paperclip className="h-4 w-4 text-zinc-500" /> Choose files
        </span>
        {hint && <span className="text-[11px] text-zinc-400">{hint}</span>}
        <input type="file" multiple accept={accept} disabled={disabled} className="sr-only"
          onChange={(e) => { onFiles(Array.from(e.target.files ?? [])); e.target.value = ""; }} />
      </label>
    </DropZone>
  );
}

/** Wraps an area that accepts files dropped on it, with a hint while dragging. */
export function DropZone({ onFiles, children, className, label = "Drop files to attach them" }: {
  onFiles: (files: File[]) => void; children: React.ReactNode; className?: string; label?: string;
}) {
  const [dragging, setDragging] = useState(false);
  return (
    <div
      className={cx("relative", className)}
      onDragOver={(e) => { if (e.dataTransfer.types.includes("Files")) { e.preventDefault(); setDragging(true); } }}
      onDragLeave={(e) => { if (!e.currentTarget.contains(e.relatedTarget as Node)) setDragging(false); }}
      onDrop={(e) => { if (e.dataTransfer.files.length === 0) return; e.preventDefault(); setDragging(false); onFiles(Array.from(e.dataTransfer.files)); }}
    >
      {children}
      {dragging && (
        <div className="pointer-events-none absolute inset-0 z-30 flex items-center justify-center rounded-2xl border-2 border-dashed border-brand-400 bg-brand-50/85 text-sm font-medium text-brand-700 dark:bg-brand-950/85 dark:text-brand-300">
          <Icons.Upload className="mr-2 h-4 w-4" /> {label}
        </div>
      )}
    </div>
  );
}

/** The files picked so far, removable, with upload progress and errors. */
export function PendingFiles({ pending, onRemove, onOpen }: {
  pending: PendingFile[]; onRemove: (key: string) => void; onOpen?: (p: PendingFile) => void;
}) {
  if (pending.length === 0) return null;
  return (
    <div className="flex flex-wrap gap-1.5">
      {pending.map((p) => (
        <span key={p.key} title={p.error}>
          <FileChip fileName={p.name} sizeBytes={p.size} busy={!p.id && !p.error}
            onOpen={p.id && onOpen ? () => onOpen(p) : undefined} onRemove={() => onRemove(p.key)} />
          {p.error && <span className="ml-1 text-[11px] text-rose-600">failed</span>}
        </span>
      ))}
    </div>
  );
}
