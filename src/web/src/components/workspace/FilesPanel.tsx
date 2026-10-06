"use client";

import { useState } from "react";
import { workspaceFileSource, workspaceFileUrl, workspaceFilesZipUrl, type FileSource } from "@/lib/api";
import { FilePreviewDialog } from "../files/FilePreview";
import type { WorkspaceFile, WorkspaceSnapshot } from "@/lib/workspaceTypes";

function formatSize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/** A file is final once the agent that wrote it has finished; while it's still working the file may
 * change again. Agents of finished runs aren't listed, so their files are final. */
function fileState(file: WorkspaceFile, workspace: WorkspaceSnapshot) {
  const author = workspace.agents.find((a) => a.agent_id === file.created_by_agent);
  if (!author || author.status === "Completed") return { label: "final", className: "text-emerald-700 dark:text-emerald-400" };
  if (["Failed", "Terminated", "TimedOut"].includes(author.status)) return { label: "unfinished", className: "text-amber-700 dark:text-amber-300" };
  return { label: "in progress", className: "text-zinc-500" };
}

export function FilesPanel({ workspace, files }: { workspace: WorkspaceSnapshot; files: WorkspaceFile[] }) {
  const agentName = (id: string) => workspace.agents.find((a) => a.agent_id === id)?.role ?? id;
  const [preview, setPreview] = useState<FileSource | null>(null);

  if (files.length === 0) {
    return (
      <div className="p-3 text-xs text-zinc-500">
        No files yet. When agents produce a report, document or data, they save it here for you to download.
      </div>
    );
  }

  return (
    <div className="text-xs">
      <div className="flex items-center justify-between border-b border-zinc-200 p-3 dark:border-zinc-800">
        <span className="text-zinc-500">{files.length} file{files.length === 1 ? "" : "s"}</span>
        {files.length > 1 && (
          <a
            href={workspaceFilesZipUrl(workspace.workspace_id)}
            className="rounded border border-zinc-300 px-2 py-0.5 hover:bg-zinc-100 dark:border-zinc-700 dark:hover:bg-zinc-800"
          >
            Download all (.zip)
          </a>
        )}
      </div>
      <ul className="divide-y divide-zinc-200 dark:divide-zinc-800">
        {files.map((f) => {
          const state = fileState(f, workspace);
          return (
            <li key={f.artifact_id} className="flex items-start gap-2 p-3">
              <span className="min-w-0 flex-1">
                <span className="flex items-center gap-1.5">
                  <button onClick={() => setPreview(workspaceFileSource(workspace.workspace_id, f.artifact_id))}
                    className="truncate text-left font-semibold hover:underline" title={`Preview ${f.path}`}>{f.path}</button>
                  <span className={`ml-auto shrink-0 text-[10px] ${state.className}`}>{state.label}</span>
                </span>
                <span className="block text-[10px] text-zinc-400">
                  {formatSize(f.size_bytes)} · by {agentName(f.created_by_agent)} · {new Date(f.updated_at).toLocaleString()}
                  {f.versions > 1 && ` · ${f.versions} versions`}
                </span>
              </span>
              <a
                href={workspaceFileUrl(workspace.workspace_id, f.artifact_id)}
                className="shrink-0 rounded border border-zinc-300 px-2 py-0.5 hover:bg-zinc-100 dark:border-zinc-700 dark:hover:bg-zinc-800"
              >
                Download
              </a>
            </li>
          );
        })}
      </ul>
      <FilePreviewDialog source={preview} onClose={() => setPreview(null)} />
    </div>
  );
}
