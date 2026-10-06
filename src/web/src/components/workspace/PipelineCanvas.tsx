"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import {
  BaseEdge,
  Background,
  Controls,
  EdgeLabelRenderer,
  Handle,
  Position,
  ReactFlow,
  getBezierPath,
  type Edge,
  type EdgeProps,
  type Node,
  type NodeProps,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { cx } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import type { PipelineDefinition, PipelineStage, StageRunStatus, StageRunView } from "@/lib/pipelineTypes";
import { BotIcon } from "../BotIcon";

const COLUMN = 290;
const ROW = 150;
const NODE_WIDTH = 220;

/** How a stage differs from the current pipeline, in a proposal's preview. */
type Change = "added" | "changed" | "removed" | null;

type StageNodeData = {
  stage: PipelineStage;
  change: Change;
  run?: StageRunView;
  editable: boolean;
  entry: boolean;
  output: boolean;
  onAddBefore?: () => void;
  onAddAfter?: () => void;
  onRemove?: () => void;
};

type InsertEdgeData = { onInsert?: () => void; active: boolean };

const RUN_STYLE: Record<StageRunStatus, { label: string; className: string; dot: string }> = {
  Pending: { label: "Waiting", className: "text-zinc-500", dot: "bg-zinc-300 dark:bg-zinc-600" },
  Running: { label: "Working", className: "text-emerald-700 dark:text-emerald-300", dot: "bg-emerald-500" },
  Completed: { label: "Done", className: "text-emerald-700 dark:text-emerald-300", dot: "bg-emerald-500" },
  Failed: { label: "Failed", className: "text-rose-700 dark:text-rose-300", dot: "bg-rose-500" },
  Skipped: { label: "Skipped", className: "text-zinc-500", dot: "bg-zinc-400" },
};

/** Columns by how many stages come before: entry stages first, the output last. */
export function layoutStages(stages: PipelineStage[]): Map<string, { x: number; y: number }> {
  const byId = new Map(stages.map((s) => [s.stage_id, s]));
  const depth = new Map<string, number>();
  const visiting = new Set<string>();
  const depthOf = (id: string): number => {
    if (depth.has(id)) return depth.get(id)!;
    if (visiting.has(id)) return 0; // A loop (only in a broken pipeline): don't recurse forever.
    visiting.add(id);
    const inputs = byId.get(id)?.inputs.filter((i) => byId.has(i)) ?? [];
    const d = inputs.length === 0 ? 0 : Math.max(...inputs.map(depthOf)) + 1;
    visiting.delete(id);
    depth.set(id, d);
    return d;
  };
  stages.forEach((s) => depthOf(s.stage_id));

  const columns = new Map<number, string[]>();
  for (const s of stages) columns.set(depth.get(s.stage_id)!, [...(columns.get(depth.get(s.stage_id)!) ?? []), s.stage_id]);
  const tallest = Math.max(1, ...[...columns.values()].map((c) => c.length));
  const positions = new Map<string, { x: number; y: number }>();
  for (const [d, ids] of columns) {
    const offset = ((tallest - ids.length) * ROW) / 2;
    ids.forEach((id, i) => positions.set(id, { x: d * COLUMN, y: offset + i * ROW }));
  }

  return positions;
}

function changed(a: PipelineStage, b: PipelineStage) {
  return a.name !== b.name || a.role !== b.role || a.instructions !== b.instructions || a.max_helpers !== b.max_helpers ||
    a.retries !== b.retries || a.on_failure !== b.on_failure || a.may_message_stages !== b.may_message_stages ||
    [...a.inputs].sort().join() !== [...b.inputs].sort().join() || [...a.capabilities].sort().join() !== [...b.capabilities].sort().join();
}

function StageNode({ data, selected }: NodeProps & { data: StageNodeData }) {
  const { stage, change, run, editable } = data;
  const status = run ? RUN_STYLE[run.status] : null;
  const working = run?.status === "Running";
  return (
    <div
      className={cx(
        "group relative rounded-xl border bg-white px-3 py-2.5 shadow-sm transition dark:bg-zinc-900",
        selected ? "border-brand-500 ring-2 ring-brand-500/30"
          : change === "added" ? "border-emerald-400 ring-2 ring-emerald-400/30"
          : change === "changed" ? "border-amber-400 ring-2 ring-amber-400/30"
          : change === "removed" ? "border-dashed border-rose-300 opacity-60 dark:border-rose-800"
          : working ? "border-emerald-300 ring-2 ring-emerald-400/25 dark:border-emerald-500/40"
          : run?.status === "Failed" ? "border-rose-300 dark:border-rose-800"
          : "border-zinc-200 hover:border-zinc-300 dark:border-zinc-800 dark:hover:border-zinc-700",
      )}
      style={{ width: NODE_WIDTH }}
    >
      <Handle type="target" position={Position.Left} className="!h-2 !w-2 !border-0 !bg-zinc-400 dark:!bg-zinc-600" />
      <div className="flex items-center gap-2.5">
        <span className="relative flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-zinc-100 text-zinc-600 dark:bg-zinc-800 dark:text-zinc-300">
          <BotIcon className="h-4.5 w-4.5" />
          {working && (
            <span className="absolute -right-1 -top-1 flex h-2.5 w-2.5" title="Working on this run">
              <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-emerald-400 opacity-75" />
              <span className="relative inline-flex h-2.5 w-2.5 rounded-full border-2 border-white bg-emerald-500 dark:border-zinc-900" />
            </span>
          )}
        </span>
        <div className="min-w-0 flex-1">
          <div className={cx("truncate text-[13px] font-semibold text-zinc-900 dark:text-zinc-100", change === "removed" && "line-through")}>{stage.name}</div>
          <div className="truncate text-[10px] text-zinc-500">{stage.role || stage.name}</div>
        </div>
      </div>
      <p className="mt-1.5 line-clamp-2 text-[11px] leading-snug text-zinc-600 dark:text-zinc-400" title={stage.instructions}>{stage.instructions}</p>
      <div className="mt-2 flex flex-wrap items-center gap-1 text-[10px]">
        {change && (
          <span className={cx("rounded-full px-1.5 py-0.5 font-medium uppercase",
            change === "added" ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-500/15 dark:text-emerald-300"
              : change === "changed" ? "bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-300"
              : "bg-rose-100 text-rose-700 dark:bg-rose-500/15 dark:text-rose-300")}>{change}</span>
        )}
        {status && (
          <span className={cx("inline-flex items-center gap-1 font-medium", status.className)}>
            <span className={cx("h-1.5 w-1.5 rounded-full", status.dot, working && "animate-pulse")} />
            {status.label}{run && run.attempts > 1 ? ` · attempt ${run.attempts}` : ""}
          </span>
        )}
        {!status && stage.max_helpers > 0 && <span className="rounded-full bg-zinc-100 px-1.5 py-0.5 text-zinc-600 dark:bg-zinc-800 dark:text-zinc-400">{stage.max_helpers} helper{stage.max_helpers > 1 ? "s" : ""}</span>}
        {!status && data.output && <span className="rounded-full bg-brand-50 px-1.5 py-0.5 text-brand-700 dark:bg-brand-500/10 dark:text-brand-300">output</span>}
      </div>

      {editable && (
        <>
          {data.entry && (
            <button onClick={(e) => { e.stopPropagation(); data.onAddBefore?.(); }} title="Add a stage before this one"
              className="absolute -left-3.5 top-1/2 hidden h-6 w-6 -translate-y-1/2 items-center justify-center rounded-full border border-zinc-300 bg-white text-zinc-600 shadow-sm hover:border-brand-400 hover:text-brand-600 group-hover:flex dark:border-zinc-700 dark:bg-zinc-900">
              <Icons.Plus className="h-3.5 w-3.5" />
            </button>
          )}
          {data.output && (
            <button onClick={(e) => { e.stopPropagation(); data.onAddAfter?.(); }} title="Add a stage after this one"
              className="absolute -right-3.5 top-1/2 hidden h-6 w-6 -translate-y-1/2 items-center justify-center rounded-full border border-zinc-300 bg-white text-zinc-600 shadow-sm hover:border-brand-400 hover:text-brand-600 group-hover:flex dark:border-zinc-700 dark:bg-zinc-900">
              <Icons.Plus className="h-3.5 w-3.5" />
            </button>
          )}
          <button onClick={(e) => { e.stopPropagation(); data.onRemove?.(); }} title="Remove this stage"
            className="absolute -right-2 -top-2 hidden h-5 w-5 items-center justify-center rounded-full border border-zinc-300 bg-white text-zinc-500 shadow-sm hover:border-rose-400 hover:text-rose-600 group-hover:flex dark:border-zinc-700 dark:bg-zinc-900">
            <Icons.X className="h-3 w-3" />
          </button>
        </>
      )}
      <Handle type="source" position={Position.Right} className="!h-2 !w-2 !border-0 !bg-zinc-400 dark:!bg-zinc-600" />
    </div>
  );
}

/** A connection between stages, with a + in the middle to insert a stage there. */
function InsertEdge({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, style, data }: EdgeProps & { data?: InsertEdgeData }) {
  const [path, labelX, labelY] = getBezierPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition });
  return (
    <>
      <BaseEdge id={id} path={path} style={style} className={data?.active ? "animate-pulse" : undefined} />
      {data?.onInsert && (
        <EdgeLabelRenderer>
          <button
            onClick={data.onInsert}
            title="Insert a stage here"
            style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)` }}
            className="nodrag nopan pointer-events-auto absolute flex h-6 w-6 items-center justify-center rounded-full border border-zinc-300 bg-white text-zinc-500 opacity-60 shadow-sm transition hover:border-brand-400 hover:text-brand-600 hover:opacity-100 dark:border-zinc-700 dark:bg-zinc-900"
          >
            <Icons.Plus className="h-3.5 w-3.5" />
          </button>
        </EdgeLabelRenderer>
      )}
    </>
  );
}

const nodeTypes = { stage: StageNode };
const edgeTypes = { insert: InsertEdge };

/**
 * A workspace's pipeline as a left-to-right graph. Editable: a + on every connection inserts a
 * stage there, + before an entry stage or after an output stage adds one at the ends, and × on
 * a stage removes it. With `baseline`, the pipeline is a proposal's preview, and stages are marked
 * as added, changed or removed against it. With `runStages`, each stage shows that run's status.
 */
export function PipelineCanvas({
  pipeline,
  baseline,
  runStages,
  editable,
  selectedStageId,
  onSelectStage,
  onInsert,
  onRemove,
}: {
  pipeline: PipelineDefinition;
  baseline?: PipelineDefinition | null;
  runStages?: StageRunView[] | null;
  editable: boolean;
  selectedStageId: string | null;
  onSelectStage: (stageId: string | null) => void;
  /** Insert a stage between `after` and `before` (either may be missing at the ends). */
  onInsert?: (where: { after?: string; before?: string }) => void;
  onRemove?: (stageId: string) => void;
}) {
  // Re-fit when the canvas is resized (a divider dragged, a panel opened) or its stages change.
  const container = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState("");
  useEffect(() => {
    const el = container.current;
    if (!el) return;
    const observer = new ResizeObserver(([entry]) =>
      setSize(`${Math.round(entry.contentRect.width / 40)}x${Math.round(entry.contentRect.height / 40)}`));
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const { nodes, edges } = useMemo(() => {
    const removed = baseline ? baseline.stages.filter((b) => !pipeline.stages.some((s) => s.stage_id === b.stage_id)) : [];
    const positions = layoutStages(pipeline.stages);
    const lastColumn = Math.max(0, ...[...positions.values()].map((p) => p.x));
    removed.forEach((s, i) => positions.set(s.stage_id, { x: lastColumn + COLUMN, y: i * ROW }));
    const runs = new Map((runStages ?? []).map((r) => [r.stage_id, r]));
    const hasDependents = new Set(pipeline.stages.flatMap((s) => s.inputs));

    const nodes: Node<StageNodeData>[] = [
      ...pipeline.stages.map((stage) => {
        const before = baseline?.stages.find((b) => b.stage_id === stage.stage_id);
        const change: Change = !baseline ? null : !before ? "added" : changed(before, stage) ? "changed" : null;
        return {
          id: stage.stage_id,
          type: "stage",
          position: positions.get(stage.stage_id)!,
          selected: stage.stage_id === selectedStageId,
          data: {
            stage,
            change,
            run: runs.get(stage.stage_id),
            editable,
            entry: stage.inputs.length === 0,
            output: !hasDependents.has(stage.stage_id),
            onAddBefore: () => onInsert?.({ before: stage.stage_id }),
            onAddAfter: () => onInsert?.({ after: stage.stage_id }),
            onRemove: () => onRemove?.(stage.stage_id),
          },
        };
      }),
      ...removed.map((stage) => ({
        id: stage.stage_id,
        type: "stage",
        position: positions.get(stage.stage_id)!,
        selectable: false,
        data: { stage, change: "removed" as Change, editable: false, entry: false, output: false },
      })),
    ];

    const edges: Edge<InsertEdgeData>[] = pipeline.stages.flatMap((stage) =>
      stage.inputs.filter((i) => positions.has(i)).map((input) => {
        const from = runs.get(input);
        const to = runs.get(stage.stage_id);
        const flowing = from?.status === "Completed" && to?.status === "Running";
        return {
          id: `${input}->${stage.stage_id}`,
          source: input,
          target: stage.stage_id,
          type: "insert",
          data: { onInsert: editable ? () => onInsert?.({ after: input, before: stage.stage_id }) : undefined, active: flowing },
          style: {
            stroke: flowing || (from?.status === "Completed" && to?.status === "Completed") ? "#10b981" : "#94a3b8",
            strokeWidth: flowing ? 2.5 : 1.5,
          },
        };
      }));

    return { nodes, edges };
  }, [pipeline, baseline, runStages, editable, selectedStageId, onInsert, onRemove]);

  return (
    <div ref={container} className="h-full w-full">
    <ReactFlow
      key={`${size}|${pipeline.stages.map((s) => s.stage_id).join(",")}`}
      nodes={nodes}
      edges={edges}
      nodeTypes={nodeTypes}
      edgeTypes={edgeTypes}
      onNodeClick={(_, node) => onSelectStage(node.id)}
      onPaneClick={() => onSelectStage(null)}
      nodesDraggable={false}
      nodesConnectable={false}
      fitView
      fitViewOptions={{ padding: 0.25, maxZoom: 1.1 }}
      minZoom={0.3}
      proOptions={{ hideAttribution: true }}
    >
      <Background gap={20} size={1} className="!bg-zinc-50 dark:!bg-zinc-950" />
      <Controls showInteractive={false} position="bottom-left" />
    </ReactFlow>
    </div>
  );
}
