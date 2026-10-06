"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  BaseEdge,
  Background,
  Controls,
  EdgeLabelRenderer,
  Handle,
  Panel,
  Position,
  ReactFlow,
  ReactFlowProvider,
  getBezierPath,
  useReactFlow,
  type Connection,
  type Edge,
  type EdgeChange,
  type EdgeProps,
  type FinalConnectionState,
  type Node,
  type NodeChange,
  type NodeProps,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { cx } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import type { PipelineDefinition, PipelineStage, StagePosition, StageRunStatus, StageRunView } from "@/lib/pipelineTypes";
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

type InsertEdgeData = { onInsert?: () => void; onDisconnect?: () => void; active: boolean };

/** The drag-and-drop type of the palette's "new agent". */
const NEW_AGENT_DRAG = "application/x-aktor-new-agent";

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

/**
 * Where every stage goes: where a person put it, else the automatic layout. Once some stages are
 * placed by hand, a new one goes between the stages it connects (or beside them), not where the
 * automatic layout would put it, which may be on top of a placed stage.
 */
export function placeStages(stages: PipelineStage[], layout: Record<string, StagePosition> | undefined): Map<string, StagePosition> {
  const auto = layoutStages(stages);
  const placed = new Map(Object.entries(layout ?? {}).filter(([id]) => auto.has(id)));
  if (placed.size === 0) return auto;
  const positions = new Map(placed);
  const unplaced = stages.filter((s) => !placed.has(s.stage_id));
  const rightmost = Math.max(...[...placed.values()].map((p) => p.x));
  unplaced.forEach((stage, i) => {
    const inputs = stage.inputs.map((id) => positions.get(id)).filter((p): p is StagePosition => !!p);
    const outputs = stages.filter((s) => s.inputs.includes(stage.stage_id)).map((s) => positions.get(s.stage_id)).filter((p): p is StagePosition => !!p);
    const avg = (ps: StagePosition[], key: "x" | "y") => ps.reduce((sum, p) => sum + p[key], 0) / ps.length;
    let at: StagePosition;
    if (inputs.length > 0 && outputs.length > 0) at = { x: (avg(inputs, "x") + avg(outputs, "x")) / 2, y: (avg(inputs, "y") + avg(outputs, "y")) / 2 + ROW / 2 };
    else if (inputs.length > 0) at = { x: Math.max(...inputs.map((p) => p.x)) + COLUMN, y: avg(inputs, "y") };
    else if (outputs.length > 0) at = { x: Math.min(...outputs.map((p) => p.x)) - COLUMN, y: avg(outputs, "y") };
    else at = { x: rightmost + COLUMN, y: i * ROW };
    // Step down until it isn't on top of another stage.
    while ([...positions.values()].some((p) => Math.abs(p.x - at.x) < NODE_WIDTH && Math.abs(p.y - at.y) < ROW * 0.7)) at = { ...at, y: at.y + ROW * 0.75 };
    positions.set(stage.stage_id, at);
  });
  return positions;
}

function changed(a: PipelineStage, b: PipelineStage) {
  return a.name !== b.name || a.role !== b.role || a.instructions !== b.instructions || a.max_helpers !== b.max_helpers ||
    a.retries !== b.retries || a.on_failure !== b.on_failure || a.may_message_stages !== b.may_message_stages || a.model_profile_id !== b.model_profile_id ||
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
      <Handle type="target" position={Position.Left} isConnectable={editable} title={editable ? "Drop a connection here: this stage takes that stage's result" : undefined}
        className={editable ? "!h-3.5 !w-3.5 !border-2 !border-white !bg-zinc-400 hover:!bg-brand-500 dark:!border-zinc-900" : "!h-2 !w-2 !border-0 !bg-zinc-400 dark:!bg-zinc-600"} />
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
        {!status && stage.model_profile_id && <span className="rounded-full bg-violet-50 px-1.5 py-0.5 text-violet-700 dark:bg-violet-500/10 dark:text-violet-300" title="Runs on this model">@{stage.model_profile_id === "server" ? "default-model" : stage.model_profile_id}</span>}
        {!status && data.output && <span className="rounded-full bg-brand-50 px-1.5 py-0.5 text-brand-700 dark:bg-brand-500/10 dark:text-brand-300">output</span>}
      </div>

      {editable && (
        <>
          {data.entry && (
            <button onClick={(e) => { e.stopPropagation(); data.onAddBefore?.(); }} title="Add a stage before this one"
              className="absolute -left-3 -top-3 hidden h-6 w-6 items-center justify-center rounded-full border border-zinc-300 bg-white text-zinc-600 shadow-sm hover:border-brand-400 hover:text-brand-600 group-hover:flex dark:border-zinc-700 dark:bg-zinc-900">
              <Icons.Plus className="h-3.5 w-3.5" />
            </button>
          )}
          {data.output && (
            <button onClick={(e) => { e.stopPropagation(); data.onAddAfter?.(); }} title="Add a stage after this one"
              className="absolute -bottom-3 -right-3 hidden h-6 w-6 items-center justify-center rounded-full border border-zinc-300 bg-white text-zinc-600 shadow-sm hover:border-brand-400 hover:text-brand-600 group-hover:flex dark:border-zinc-700 dark:bg-zinc-900">
              <Icons.Plus className="h-3.5 w-3.5" />
            </button>
          )}
          <button onClick={(e) => { e.stopPropagation(); data.onRemove?.(); }} title="Remove this stage"
            className="absolute -right-2 -top-2 hidden h-5 w-5 items-center justify-center rounded-full border border-zinc-300 bg-white text-zinc-500 shadow-sm hover:border-rose-400 hover:text-rose-600 group-hover:flex dark:border-zinc-700 dark:bg-zinc-900">
            <Icons.X className="h-3 w-3" />
          </button>
        </>
      )}
      <Handle type="source" position={Position.Right} isConnectable={editable}
        title={editable ? "Drag to a stage to hand it this result, or to empty space to add a new agent there" : undefined}
        className={editable ? "!h-3.5 !w-3.5 !border-2 !border-white !bg-brand-500 hover:!scale-125 dark:!border-zinc-900" : "!h-2 !w-2 !border-0 !bg-zinc-400 dark:!bg-zinc-600"} />
    </div>
  );
}

/** A connection between stages, with a + in the middle to insert a stage there. */
function InsertEdge({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, style, data, selected }: EdgeProps & { data?: InsertEdgeData }) {
  const [path, labelX, labelY] = getBezierPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition });
  return (
    <>
      <BaseEdge id={id} path={path} style={selected ? { ...style, stroke: "#6366f1", strokeWidth: 2.5 } : style} interactionWidth={18}
        className={data?.active ? "animate-pulse" : undefined} />
      {data?.onInsert && (
        <EdgeLabelRenderer>
          <div
            style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)` }}
            className="nodrag nopan group/edge pointer-events-auto absolute flex items-center gap-0.5"
          >
            <button onClick={data.onInsert} title="Insert a stage here"
              className="flex h-6 w-6 items-center justify-center rounded-full border border-zinc-300 bg-white text-zinc-500 opacity-60 shadow-sm transition hover:border-brand-400 hover:text-brand-600 hover:opacity-100 dark:border-zinc-700 dark:bg-zinc-900">
              <Icons.Plus className="h-3.5 w-3.5" />
            </button>
            {data.onDisconnect && (
              <button onClick={data.onDisconnect} title="Remove this connection"
                className={cx("h-5 w-5 items-center justify-center rounded-full border border-zinc-300 bg-white text-zinc-500 shadow-sm hover:border-rose-400 hover:text-rose-600 dark:border-zinc-700 dark:bg-zinc-900",
                  selected ? "flex" : "hidden group-hover/edge:flex")}>
                <Icons.X className="h-3 w-3" />
              </button>
            )}
          </div>
        </EdgeLabelRenderer>
      )}
    </>
  );
}

const nodeTypes = { stage: StageNode };
const edgeTypes = { insert: InsertEdge };

export type CanvasEditing = {
  /** Insert a stage between `after` and `before` (either may be missing at the ends). */
  onInsert: (where: { after?: string; before?: string }) => void;
  onRemove: (stageId: string) => void;
  /** `to` takes `from`'s result. */
  onConnect: (from: string, to: string) => void;
  onDisconnect: (from: string, to: string) => void;
  /** Stages were moved: every stage's position. Empty: back to the automatic layout. */
  onMove: (layout: Record<string, StagePosition>) => void;
  /** A new agent dropped on the canvas at `position`, taking `inputs`' results. */
  onAddAt: (position: StagePosition, inputs: string[]) => void;
};

/**
 * A workspace's pipeline as a left-to-right graph. Editable (with `editing`):
 * - drag stages to arrange them, and drag from a stage's right handle to another stage to hand it
 *   that result, or to empty space to add a new agent there;
 * - drag "New agent" from the palette onto the canvas, or click it;
 * - + on a connection inserts a stage there, × (or Delete) removes it; × on a stage removes it.
 * With `baseline`, the pipeline is a proposal's preview, and stages are marked as added, changed
 * or removed against it. With `runStages`, each stage shows that run's status.
 */
export function PipelineCanvas(props: {
  pipeline: PipelineDefinition;
  baseline?: PipelineDefinition | null;
  runStages?: StageRunView[] | null;
  editing?: CanvasEditing | null;
  selectedStageId: string | null;
  onSelectStage: (stageId: string | null) => void;
}) {
  return (
    <ReactFlowProvider>
      <Canvas {...props} />
    </ReactFlowProvider>
  );
}

function Canvas({
  pipeline,
  baseline,
  runStages,
  editing,
  selectedStageId,
  onSelectStage,
}: Parameters<typeof PipelineCanvas>[0]) {
  const editable = !!editing;
  const flow = useReactFlow();
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

  // Positions while dragging (and until the saved layout comes back), for this saved layout only.
  const layoutKey = JSON.stringify(pipeline.layout ?? {});
  const [drag, setDrag] = useState<{ key: string; positions: Record<string, StagePosition> }>({ key: "", positions: {} });
  const local = drag.key === layoutKey ? drag.positions : null;
  const [selectedEdge, setSelectedEdge] = useState<string | null>(null);
  // Sizes React Flow measured: nodes are controlled here, and one passed without its size stays
  // hidden until it's measured again.
  const [measured, setMeasured] = useState<Record<string, { width: number; height: number }>>({});

  const positions = useMemo(() => {
    const placed = placeStages(pipeline.stages, pipeline.layout);
    if (local) for (const [id, p] of Object.entries(local)) if (placed.has(id)) placed.set(id, p);
    return placed;
  }, [pipeline, local]);

  const { nodes, edges } = useMemo(() => {
    const removed = baseline ? baseline.stages.filter((b) => !pipeline.stages.some((s) => s.stage_id === b.stage_id)) : [];
    const all = new Map(positions);
    const lastColumn = Math.max(0, ...[...all.values()].map((p) => p.x));
    removed.forEach((s, i) => all.set(s.stage_id, baseline?.layout?.[s.stage_id] ?? { x: lastColumn + COLUMN, y: i * ROW }));
    const runs = new Map((runStages ?? []).map((r) => [r.stage_id, r]));
    const hasDependents = new Set(pipeline.stages.flatMap((s) => s.inputs));

    const nodes: Node<StageNodeData>[] = [
      ...pipeline.stages.map((stage) => {
        const before = baseline?.stages.find((b) => b.stage_id === stage.stage_id);
        const change: Change = !baseline ? null : !before ? "added" : changed(before, stage) ? "changed" : null;
        return {
          id: stage.stage_id,
          type: "stage",
          position: all.get(stage.stage_id)!,
          measured: measured[stage.stage_id],
          selected: stage.stage_id === selectedStageId,
          data: {
            stage,
            change,
            run: runs.get(stage.stage_id),
            editable,
            entry: stage.inputs.length === 0,
            output: !hasDependents.has(stage.stage_id),
            onAddBefore: () => editing?.onInsert({ before: stage.stage_id }),
            onAddAfter: () => editing?.onInsert({ after: stage.stage_id }),
            onRemove: () => editing?.onRemove(stage.stage_id),
          },
        };
      }),
      ...removed.map((stage) => ({
        id: stage.stage_id,
        type: "stage",
        position: all.get(stage.stage_id)!,
        measured: measured[stage.stage_id],
        selectable: false,
        draggable: false,
        data: { stage, change: "removed" as Change, editable: false, entry: false, output: false },
      })),
    ];

    const edges: Edge<InsertEdgeData>[] = pipeline.stages.flatMap((stage) =>
      stage.inputs.filter((i) => all.has(i)).map((input) => {
        const from = runs.get(input);
        const to = runs.get(stage.stage_id);
        const flowing = from?.status === "Completed" && to?.status === "Running";
        const id = `${input}->${stage.stage_id}`;
        return {
          id,
          source: input,
          target: stage.stage_id,
          type: "insert",
          selected: id === selectedEdge,
          data: {
            onInsert: editing ? () => editing.onInsert({ after: input, before: stage.stage_id }) : undefined,
            onDisconnect: editing ? () => editing.onDisconnect(input, stage.stage_id) : undefined,
            active: flowing,
          },
          style: {
            stroke: flowing || (from?.status === "Completed" && to?.status === "Completed") ? "#10b981" : "#94a3b8",
            strokeWidth: flowing ? 2.5 : 1.5,
          },
        };
      }));

    return { nodes, edges };
  }, [pipeline, baseline, runStages, editing, editable, selectedStageId, selectedEdge, positions, measured]);

  const onNodesChange = useCallback((changes: NodeChange<Node<StageNodeData>>[]) => {
    const sizes = changes.filter((c) => c.type === "dimensions" && c.dimensions);
    if (sizes.length > 0) {
      setMeasured((m) => {
        const next = { ...m };
        for (const c of sizes) if (c.type === "dimensions" && c.dimensions) next[c.id] = c.dimensions;
        return next;
      });
    }
    const moves = changes.filter((c) => c.type === "position" && c.position);
    if (moves.length === 0) return;
    const next = { ...Object.fromEntries(positions), ...(local ?? {}) };
    for (const c of moves) if (c.type === "position" && c.position) next[c.id] = c.position;
    setDrag({ key: layoutKey, positions: next });
    // Saved when the drag ends: every stage's position, so the others stay where they are.
    if (moves.some((c) => c.type === "position" && !c.dragging)) editing?.onMove(next);
  }, [positions, local, layoutKey, editing]);

  const onEdgesChange = useCallback((changes: EdgeChange<Edge<InsertEdgeData>>[]) => {
    for (const c of changes) if (c.type === "select") setSelectedEdge(c.selected ? c.id : null);
  }, []);

  const connect = useCallback((c: Connection) => {
    if (c.source && c.target && c.source !== c.target) editing?.onConnect(c.source, c.target);
  }, [editing]);

  // A connection dropped on empty space adds a new agent there, taking the source's result.
  const connectEnd = useCallback((event: MouseEvent | TouchEvent, state: FinalConnectionState) => {
    if (!editing || state.isValid || state.toNode || !state.fromNode) return;
    const point = "changedTouches" in event ? event.changedTouches[0] : event;
    const at = flow.screenToFlowPosition({ x: point.clientX, y: point.clientY });
    editing.onAddAt({ x: at.x, y: at.y - 40 }, [state.fromNode.id]);
  }, [editing, flow]);

  const freeSpot = useCallback((): StagePosition => {
    const all = [...positions.values()];
    return all.length === 0 ? { x: 0, y: 0 } : { x: Math.max(...all.map((p) => p.x)) + COLUMN, y: Math.min(...all.map((p) => p.y)) };
  }, [positions]);

  return (
    <div
      ref={container}
      className="h-full w-full"
      onDragOver={(e) => { if (editing && e.dataTransfer.types.includes(NEW_AGENT_DRAG)) { e.preventDefault(); e.dataTransfer.dropEffect = "copy"; } }}
      onDrop={(e) => {
        if (!editing || !e.dataTransfer.types.includes(NEW_AGENT_DRAG)) return;
        e.preventDefault();
        const at = flow.screenToFlowPosition({ x: e.clientX, y: e.clientY });
        editing.onAddAt({ x: at.x - NODE_WIDTH / 2, y: at.y - 40 }, []);
      }}
    >
    <ReactFlow
      key={`${size}|${pipeline.stages.map((s) => s.stage_id).join(",")}`}
      nodes={nodes}
      edges={edges}
      nodeTypes={nodeTypes}
      edgeTypes={edgeTypes}
      onNodeClick={(_, node) => onSelectStage(node.id)}
      onPaneClick={() => { onSelectStage(null); setSelectedEdge(null); }}
      onNodesChange={onNodesChange}
      onEdgesChange={onEdgesChange}
      onConnect={connect}
      onConnectEnd={connectEnd}
      isValidConnection={(c) => c.source !== c.target && !pipeline.stages.find((s) => s.stage_id === c.target)?.inputs.includes(c.source)}
      onEdgesDelete={(deleted) => deleted.forEach((e) => editing?.onDisconnect(e.source, e.target))}
      onNodesDelete={(deleted) => deleted.slice(0, 1).forEach((n) => editing?.onRemove(n.id))}
      deleteKeyCode={editable ? ["Backspace", "Delete"] : null}
      nodesDraggable={editable}
      nodesConnectable={editable}
      edgesFocusable={editable}
      connectionRadius={36}
      fitView
      fitViewOptions={{ padding: 0.25, maxZoom: 1.1 }}
      minZoom={0.3}
      proOptions={{ hideAttribution: true }}
    >
      <Background gap={20} size={1} className="!bg-zinc-50 dark:!bg-zinc-950" />
      <Controls showInteractive={false} position="bottom-left" />
      {editing && (
        <Panel position="top-left" className="!m-2 flex items-center gap-1.5">
          <button
            draggable
            onDragStart={(e) => { e.dataTransfer.setData(NEW_AGENT_DRAG, "1"); e.dataTransfer.effectAllowed = "copy"; }}
            onClick={() => editing.onAddAt(freeSpot(), [])}
            title="Drag onto the canvas to add an agent there, or click to add one"
            className="inline-flex cursor-grab items-center gap-1.5 rounded-lg border border-dashed border-brand-300 bg-white px-2.5 py-1.5 text-xs font-medium text-brand-700 shadow-sm hover:border-brand-500 active:cursor-grabbing dark:border-brand-700 dark:bg-zinc-900 dark:text-brand-300"
          >
            <BotIcon className="h-4 w-4" /> New agent
          </button>
          {pipeline.layout && Object.keys(pipeline.layout).length > 0 && (
            <button onClick={() => editing.onMove({})} title="Lay the stages out automatically again"
              className="rounded-lg border border-zinc-200 bg-white px-2.5 py-1.5 text-xs text-zinc-600 shadow-sm hover:border-zinc-300 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-300">
              Tidy up
            </button>
          )}
          <span className="hidden text-[11px] text-zinc-400 xl:inline">Drag stages to move them · drag from a stage&apos;s right dot to connect</span>
        </Panel>
      )}
    </ReactFlow>
    </div>
  );
}
