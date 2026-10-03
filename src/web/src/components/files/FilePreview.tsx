"use client";

import { useEffect, useState } from "react";
import { apiErrorMessage, fetchFileBlob, getFilePreview, type FileSource } from "@/lib/api";
import type { FilePreview as Preview } from "@/lib/types";
import { Icons } from "@/components/ui/icons";
import { cx } from "@/components/ui";
import { Markdown } from "./Markdown";

export function formatSize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

const FORMAT_LABEL: Record<string, string> = {
  word: "Word", excel: "Excel", powerpoint: "PowerPoint", pdf: "PDF", csv: "CSV", markdown: "Markdown",
  html: "HTML", image: "Image", text: "Text", binary: "File",
};

/** Short type label from a file name, for chips before a preview has loaded. */
export function fileTypeLabel(fileName: string) {
  const ext = fileName.split(".").pop()?.toLowerCase() ?? "";
  const map: Record<string, string> = {
    docx: "DOCX", doc: "DOC", pdf: "PDF", xlsx: "XLSX", xls: "XLS", csv: "CSV", pptx: "PPTX", ppt: "PPT",
    md: "MD", txt: "TXT", json: "JSON", png: "PNG", jpg: "JPG", jpeg: "JPG", gif: "GIF", svg: "SVG", html: "HTML",
  };
  return map[ext] ?? (ext ? ext.toUpperCase().slice(0, 4) : "FILE");
}

const CHIP_TONE: Record<string, string> = {
  DOCX: "bg-blue-100 text-blue-700 dark:bg-blue-950 dark:text-blue-300",
  PDF: "bg-rose-100 text-rose-700 dark:bg-rose-950 dark:text-rose-300",
  XLSX: "bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300",
  CSV: "bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300",
  PPTX: "bg-orange-100 text-orange-700 dark:bg-orange-950 dark:text-orange-300",
  MD: "bg-zinc-200 text-zinc-700 dark:bg-zinc-800 dark:text-zinc-300",
};

/** A file as a clickable card (chat attachments and results): type, name and size. */
export function FileChip({ fileName, sizeBytes, onOpen, onRemove, busy }: {
  fileName: string;
  sizeBytes?: number;
  onOpen?: () => void;
  onRemove?: () => void;
  busy?: boolean;
}) {
  const label = fileTypeLabel(fileName);
  return (
    <span className="inline-flex max-w-full items-center gap-2 rounded-lg border border-zinc-200 bg-white py-1 pl-1 pr-2 text-xs shadow-sm dark:border-zinc-700 dark:bg-zinc-900">
      <span className={cx("shrink-0 rounded px-1.5 py-0.5 text-[10px] font-bold", CHIP_TONE[label] ?? "bg-zinc-100 text-zinc-600 dark:bg-zinc-800 dark:text-zinc-300")}>{label}</span>
      <button type="button" onClick={onOpen} disabled={!onOpen} className="min-w-0 truncate text-left font-medium text-zinc-800 enabled:hover:underline dark:text-zinc-200" title={fileName}>
        {fileName}
      </button>
      {sizeBytes !== undefined && <span className="shrink-0 text-zinc-400">{formatSize(sizeBytes)}</span>}
      {busy && <span className="h-2 w-2 shrink-0 animate-pulse rounded-full bg-brand-500" />}
      {onRemove && (
        <button type="button" onClick={onRemove} className="shrink-0 rounded p-0.5 text-zinc-400 hover:bg-zinc-100 hover:text-zinc-700 dark:hover:bg-zinc-800" aria-label={`Remove ${fileName}`}>
          <Icons.X className="h-3 w-3" />
        </button>
      )}
    </span>
  );
}

/**
 * Shows a file in place, the way it would look opened: Markdown and Word documents rendered,
 * code with line numbers, CSV and Excel as tables (one tab per sheet), PowerPoint as slides, PDFs
 * in the browser's viewer, images, and HTML in a sandbox that runs no scripts.
 */
export function FilePreviewDialog({ source, onClose }: { source: FileSource | null; onClose: () => void }) {
  // Keyed by file, so opening another one starts from a clean state.
  return source ? <PreviewDialog key={source.previewUrl} source={source} onClose={onClose} /> : null;
}

function PreviewDialog({ source, onClose }: { source: FileSource; onClose: () => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-3 backdrop-blur-sm sm:p-6" onMouseDown={onClose}>
      <div role="dialog" aria-modal onMouseDown={(e) => e.stopPropagation()}
        className="flex h-full max-h-[94vh] w-full max-w-6xl flex-col overflow-hidden rounded-2xl border border-zinc-200 bg-white shadow-2xl dark:border-zinc-800 dark:bg-zinc-900">
        <FileViewer source={source} onClose={onClose} />
      </div>
    </div>
  );
}

/** The viewer on its own (a side panel next to a conversation): a header with the file's name,
 * type, size and download, then the file shown as it would look opened. */
export function FileViewer({ source, onClose }: { source: FileSource; onClose: () => void }) {
  const [preview, setPreview] = useState<Preview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [blobUrl, setBlobUrl] = useState<string | null>(null);
  const [sheet, setSheet] = useState(0);
  const [showSource, setShowSource] = useState(false);

  useEffect(() => {
    let cancelled = false;
    let url: string | null = null;
    getFilePreview(source)
      .then(async (p) => {
        if (cancelled) return;
        setPreview(p);
        if (p.kind === "pdf" || p.kind === "image") {
          const blob = await fetchFileBlob(source, p.content_type);
          if (cancelled) return;
          url = URL.createObjectURL(blob);
          setBlobUrl(url);
        }
      })
      .catch((e) => !cancelled && setError(apiErrorMessage(e)));
    return () => {
      cancelled = true;
      if (url) URL.revokeObjectURL(url);
    };
  }, [source]);

  const canToggleSource = preview?.kind === "markdown" && preview.format === "markdown" || preview?.kind === "html";

  return (
    <div className="flex h-full min-h-0 flex-col bg-white dark:bg-zinc-900">
      <header className="flex items-center gap-3 border-b border-zinc-200 px-4 py-3 dark:border-zinc-800">
        <span className={cx("rounded px-1.5 py-0.5 text-[10px] font-bold", CHIP_TONE[fileTypeLabel(preview?.file_name ?? "")] ?? "bg-zinc-100 text-zinc-600 dark:bg-zinc-800 dark:text-zinc-300")}>
          {fileTypeLabel(preview?.file_name ?? "")}
        </span>
        <div className="min-w-0 flex-1">
          <div className="truncate text-sm font-semibold text-zinc-900 dark:text-zinc-50">{preview?.file_name ?? "Loading…"}</div>
          {preview && (
            <div className="truncate text-[11px] text-zinc-500">
              {FORMAT_LABEL[preview.format] ?? preview.format} · {formatSize(preview.size_bytes)} · {preview.path}
              {" · "}{preview.created_by === "user" ? "attached by you" : `by ${preview.created_by}`}
            </div>
          )}
        </div>
        {canToggleSource && (
          <div className="flex rounded-lg border border-zinc-200 p-0.5 text-xs dark:border-zinc-700">
            {["Preview", "Source"].map((label, i) => (
              <button key={label} onClick={() => setShowSource(i === 1)}
                className={cx("rounded-md px-2 py-1", showSource === (i === 1) ? "bg-zinc-100 font-medium dark:bg-zinc-800" : "text-zinc-500")}>
                {label}
              </button>
            ))}
          </div>
        )}
        <a href={source.contentUrl} download={preview?.file_name}
          className="inline-flex items-center gap-1 rounded-lg border border-zinc-200 px-2.5 py-1.5 text-xs font-medium hover:bg-zinc-50 dark:border-zinc-700 dark:hover:bg-zinc-800">
          <Icons.Download className="h-3.5 w-3.5" /> Download
        </a>
        <button onClick={onClose} className="rounded-md p-1.5 text-zinc-400 hover:bg-zinc-100 hover:text-zinc-700 dark:hover:bg-zinc-800" aria-label="Close">
          <Icons.X />
        </button>
      </header>

      {preview?.note && (
        <div className="border-b border-amber-200 bg-amber-50 px-4 py-2 text-xs text-amber-800 dark:border-amber-900 dark:bg-amber-950/40 dark:text-amber-200">{preview.note}</div>
      )}
      {preview?.truncated && (
        <div className="border-b border-zinc-200 bg-zinc-50 px-4 py-2 text-xs text-zinc-600 dark:border-zinc-800 dark:bg-zinc-950 dark:text-zinc-400">
          Showing the beginning of a long file. Download it to see everything.
        </div>
      )}

      <div className="min-h-0 flex-1 overflow-auto bg-zinc-50 dark:bg-zinc-950">
        {error ? (
          <div className="p-6 text-sm text-rose-600">{error}</div>
        ) : !preview ? (
          <div className="space-y-3 p-8">{[80, 95, 60, 90].map((w, i) => <div key={i} className="h-4 animate-pulse rounded bg-zinc-200 dark:bg-zinc-800" style={{ width: `${w}%` }} />)}</div>
        ) : (
          <PreviewBody preview={preview} blobUrl={blobUrl} sheet={sheet} onSheet={setSheet} showSource={showSource} contentUrl={source.contentUrl} />
        )}
      </div>
    </div>
  );
}

function PreviewBody({ preview, blobUrl, sheet, onSheet, showSource, contentUrl }: {
  preview: Preview; blobUrl: string | null; sheet: number; onSheet: (i: number) => void; showSource: boolean; contentUrl: string;
}) {
  const text = preview.text ?? "";
  if (showSource || preview.kind === "text") {
    return <pre className="whitespace-pre-wrap break-words p-6 font-mono text-[13px] leading-5 text-zinc-800 dark:text-zinc-200">{text}</pre>;
  }

  switch (preview.kind) {
    case "markdown":
      return (
        <article className="mx-auto my-6 max-w-3xl rounded-xl bg-white px-10 py-8 shadow-sm ring-1 ring-zinc-200 dark:bg-zinc-900 dark:ring-zinc-800">
          {text.trim() ? <Markdown text={text} /> : <p className="text-sm text-zinc-500">This document has no text.</p>}
        </article>
      );

    case "code":
      return <CodeView text={text} />;

    case "table": {
      const sheets = preview.sheets ?? [];
      const current = sheets[Math.min(sheet, Math.max(0, sheets.length - 1))];
      return (
        <div className="flex h-full flex-col">
          <div className="min-h-0 flex-1 overflow-auto">
            {current && current.rows.length > 0 ? <SheetTable rows={current.rows} /> : <p className="p-6 text-sm text-zinc-500">This sheet is empty.</p>}
          </div>
          {sheets.length > 1 && (
            <div className="flex gap-1 overflow-x-auto border-t border-zinc-200 bg-white px-2 py-1 dark:border-zinc-800 dark:bg-zinc-900">
              {sheets.map((s, i) => (
                <button key={i} onClick={() => onSheet(i)}
                  className={cx("whitespace-nowrap rounded px-3 py-1 text-xs", i === sheet ? "bg-emerald-100 font-medium text-emerald-800 dark:bg-emerald-950 dark:text-emerald-300" : "text-zinc-500 hover:bg-zinc-100 dark:hover:bg-zinc-800")}>
                  {s.name}{s.truncated && " (partial)"}
                </button>
              ))}
            </div>
          )}
        </div>
      );
    }

    case "slides":
      return (
        <div className="mx-auto max-w-4xl space-y-6 p-6">
          {(preview.slides ?? []).map((s) => (
            <section key={s.number}>
              <div className="mb-1 text-[11px] text-zinc-500">Slide {s.number}</div>
              <div className="flex aspect-video flex-col overflow-hidden rounded-lg bg-white p-[5%] shadow-md ring-1 ring-zinc-200 dark:bg-zinc-900 dark:ring-zinc-800">
                {s.title && <h3 className="mb-[3%] text-[clamp(1rem,2.6vw,1.9rem)] font-bold leading-tight text-zinc-900 dark:text-zinc-50">{s.title}</h3>}
                <ul className="min-h-0 flex-1 list-disc space-y-1 overflow-hidden pl-6 text-[clamp(0.75rem,1.5vw,1.05rem)] text-zinc-700 dark:text-zinc-300">
                  {s.paragraphs.map((p, i) => <li key={i} className="whitespace-pre-wrap">{p}</li>)}
                </ul>
              </div>
              {s.notes && <p className="mt-2 rounded bg-white p-2 text-xs text-zinc-600 ring-1 ring-zinc-200 dark:bg-zinc-900 dark:text-zinc-400 dark:ring-zinc-800">Notes: {s.notes}</p>}
            </section>
          ))}
        </div>
      );

    case "pdf":
      return blobUrl
        ? <iframe src={blobUrl} title={preview.file_name} className="h-full min-h-[70vh] w-full border-0 bg-white" />
        : <div className="p-6 text-sm text-zinc-500">Loading PDF…</div>;

    case "image":
      return (
        <div className="flex min-h-full items-center justify-center bg-[repeating-conic-gradient(#e4e4e7_0%_25%,transparent_0%_50%)] bg-[length:20px_20px] p-6 dark:bg-[repeating-conic-gradient(#27272a_0%_25%,transparent_0%_50%)]">
          {/* A blob URL: next/image can't optimize it. */}
          {/* eslint-disable-next-line @next/next/no-img-element */}
          {blobUrl ? <img src={blobUrl} alt={preview.file_name} className="max-h-[80vh] max-w-full rounded shadow" /> : <span className="text-sm text-zinc-500">Loading image…</span>}
        </div>
      );

    case "html":
      // No allow-scripts and no allow-same-origin: the page renders, but can't run code or reach this app.
      return <iframe sandbox="" srcDoc={text} title={preview.file_name} className="h-full min-h-[70vh] w-full border-0 bg-white" />;

    default:
      return (
        <div className="flex h-full flex-col items-center justify-center gap-3 p-10 text-center text-sm text-zinc-500">
          <p>This file type can&apos;t be shown here.</p>
          <a href={contentUrl} download={preview.file_name} className="rounded-lg bg-brand-500 px-4 py-2 font-medium text-white hover:bg-brand-600">Download {preview.file_name}</a>
        </div>
      );
  }
}

function CodeView({ text }: { text: string }) {
  const lines = text.split("\n");
  return (
    <div className="flex font-mono text-[13px] leading-5">
      <pre aria-hidden className="select-none border-r border-zinc-200 bg-zinc-100 px-3 py-4 text-right text-zinc-400 dark:border-zinc-800 dark:bg-zinc-900">
        {lines.map((_, i) => i + 1).join("\n")}
      </pre>
      <pre className="flex-1 overflow-x-auto px-4 py-4 text-zinc-800 dark:text-zinc-200">{text}</pre>
    </div>
  );
}

function columnName(i: number) {
  let name = "";
  for (let n = i + 1; n > 0; n = Math.floor((n - 1) / 26)) name = String.fromCharCode(65 + ((n - 1) % 26)) + name;
  return name;
}

/** A spreadsheet grid: column letters, row numbers, the first row as the header. */
function SheetTable({ rows }: { rows: string[][] }) {
  const width = Math.max(...rows.map((r) => r.length));
  return (
    <table className="border-collapse bg-white text-xs dark:bg-zinc-900">
      <thead className="sticky top-0 z-10">
        <tr>
          <th className="sticky left-0 z-20 border border-zinc-200 bg-zinc-100 px-2 dark:border-zinc-700 dark:bg-zinc-800" />
          {Array.from({ length: width }, (_, i) => (
            <th key={i} className="border border-zinc-200 bg-zinc-100 px-2 py-1 font-normal text-zinc-500 dark:border-zinc-700 dark:bg-zinc-800">{columnName(i)}</th>
          ))}
        </tr>
      </thead>
      <tbody>
        {rows.map((row, r) => (
          <tr key={r} className={r === 0 ? "font-semibold" : undefined}>
            <td className="sticky left-0 border border-zinc-200 bg-zinc-100 px-2 text-right text-zinc-500 dark:border-zinc-700 dark:bg-zinc-800">{r + 1}</td>
            {Array.from({ length: width }, (_, c) => (
              <td key={c} className={cx("max-w-xs truncate border border-zinc-200 px-2 py-1 dark:border-zinc-700", /^-?\d+(\.\d+)?$/.test(row[c] ?? "") && "text-right tabular-nums")} title={row[c]}>
                {row[c] ?? ""}
              </td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  );
}
