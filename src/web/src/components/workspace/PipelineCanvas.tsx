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
import { kindOf, type PipelineDefinition, type PipelineStage, type StageKind, type StagePosition, type StageRunStatus, type StageRunView } from "@/lib/pipelineTypes";
import type { ConnectionView, TriggerView } from "@/lib/workspaceTypes";
import type { ModelChoice } from "@/lib/api";
import { mentionsIn } from "@/components/ui/MentionTextarea";
import { PartIcon, StageKindIcon, TriggerIcon } from "./StageKindIcon";
import { colorOf } from "./stageColors";
import { ConnectorIcon, connectionOfTool } from "./ConnectorIcon";

const COLUMN = 290;
const ROW = 150;
/** Rows are further apart when agents show their model, tools and memory underneath. */
const ROW_WITH_PARTS = 250;
const NODE_WIDTH = 220;
const PART_WIDTH = 64;
const TRIGGER_WIDTH = 170;

/** The parts attached to an agent, as in n8n: what it thinks with, acts with, and remembers. */
export type AgentPart = "model" | "tools" | "memory";

/** A connection shown by its mark: on an action that calls it, and under an agent's tools. */
type ConnectorMark = { pluginId: string; name: string };

type PartNodeData = { part: AgentPart; label: string; detail: string; title: string; connectors?: ConnectorMark[] };
type TriggerNodeData = { kind: string; name: string; detail: string };

const KIND_STYLE: Record<StageKind, { icon: string; label: string }> = {
  Agent: { icon: "bg-zinc-100 text-zinc-600 dark:bg-zinc-800 dark:text-zinc-300", label: "" },
  Action: { icon: "bg-sky-100 text-sky-700 dark:bg-sky-500/15 dark:text-sky-300", label: "Action" },
  Script: { icon: "bg-violet-100 text-violet-700 dark:bg-violet-500/15 dark:text-violet-300", label: "Script" },
};

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
  /** Whether the agent's model, tools and memory hang below it. */
  parts?: boolean;
  /** For an action: the connection whose tool it calls. */
  connector?: ConnectorMark;
  /** The model the stage's profile runs on (a profile keeps its id when its model changes). */
  model?: { label: string; title: string };
};

type InsertEdgeData = { onInsert?: () => void; onDisconnect?: () => void; active: boolean };

/** The drag-and-drop type of the palette's new stages; the data is the stage's kind. */
const NEW_AGENT_DRAG = "application/x-aktor-new-agent";

const RUN_STYLE: Record<StageRunStatus, { label: string; className: string; dot: string }> = {
  Pending: { label: "Waiting", className: "text-zinc-500", dot: "bg-zinc-300 dark:bg-zinc-600" },
  Running: { label: "Working", className: "text-emerald-700 dark:text-emerald-300", dot: "bg-emerald-500" },
  Completed: { label: "Done", className: "text-emerald-700 dark:text-emerald-300", dot: "bg-emerald-500" },
  Failed: { label: "Failed", className: "text-rose-700 dark:text-rose-300", dot: "bg-rose-500" },
  Skipped: { label: "Skipped", className: "text-zinc-500", dot: "bg-zinc-400" },
};

/** Columns by how many stages come before: entry stages first, the output last. */
export function layoutStages(stages: PipelineStage[], row = ROW): Map<string, { x: number; y: number }> {
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
    const offset = ((tallest - ids.length) * row) / 2;
    ids.forEach((id, i) => positions.set(id, { x: d * COLUMN, y: offset + i * row }));
  }

  return positions;
}

/**
 * Where every stage goes: where a person put it, else the automatic layout. Once some stages are
 * placed by hand, a new one goes between the stages it connects (or beside them), not where the
 * automatic layout would put it, which may be on top of a placed stage.
 */
export function placeStages(stages: PipelineStage[], layout: Record<string, StagePosition> | undefined, row = ROW): Map<string, StagePosition> {
  const auto = layoutStages(stages, row);
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
    kindOf(a) !== kindOf(b) || (a.tool ?? "") !== (b.tool ?? "") || (a.arguments ?? "") !== (b.arguments ?? "") || (a.for_each ?? "") !== (b.for_each ?? "") ||
    (a.language ?? "") !== (b.language ?? "") || (a.code ?? "") !== (b.code ?? "") || (a.color ?? "") !== (b.color ?? "") ||
    [...a.inputs].sort().join() !== [...b.inputs].sort().join() || [...a.capabilities].sort().join() !== [...b.capabilities].sort().join();
}

/** What a stage does, in a line: an agent's instructions, the tool an action calls, a script's language. */
function describe(stage: PipelineStage): { subtitle: string; body: string } {
  const kind = kindOf(stage);
  if (kind === "Action") {
    return {
      subtitle: stage.tool ? stage.tool.replace("__", " · ").replaceAll("_", " ") : "No tool chosen",
      body: stage.for_each ? `For each item of ${stage.for_each}` : "Calls the tool once per run",
    };
  }
  if (kind === "Script") {
    const lines = (stage.code ?? "").split("\n");
    const first = lines.find((l) => l.trim() && !/^\s*(#|\/\/)/.test(l))?.trim() ?? "";
    return { subtitle: `${stage.language === "javascript" ? "JavaScript" : "Python"} · ${lines.length} line${lines.length === 1 ? "" : "s"}`, body: first };
  }
  return { subtitle: stage.role || stage.name, body: stage.instructions };
}

function StageNode({ data, selected }: NodeProps & { data: StageNodeData }) {
  const { stage, change, run, editable } = data;
  const status = run ? RUN_STYLE[run.status] : null;
  const working = run?.status === "Running";
  const kind = kindOf(stage);
  const { subtitle, body } = describe(stage);
  const color = colorOf(stage.color);
  return (
    <div
      className={cx(
        "group relative rounded-xl border px-3 py-2.5 shadow-sm transition",
        color ? cx(color.tint, "pl-4") : "bg-white dark:bg-zinc-900",
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
      {color && <span aria-hidden className={cx("pointer-events-none absolute inset-y-0 left-0 w-1.5 rounded-l-xl", color.bar)} />}
      <Handle type="target" id="in" position={Position.Left} isConnectable={editable} title={editable ? "Drop a connection here: this stage takes that stage's result" : undefined}
        className={editable ? "!h-3.5 !w-3.5 !border-2 !border-white !bg-zinc-400 hover:!bg-brand-500 dark:!border-zinc-900" : "!h-2 !w-2 !border-0 !bg-zinc-400 dark:!bg-zinc-600"} />
      <div className="flex items-center gap-2.5">
        <span className={cx("relative flex h-8 w-8 shrink-0 items-center justify-center rounded-lg",
          data.connector ? "border border-zinc-200 bg-white dark:border-zinc-700 dark:bg-zinc-100" : KIND_STYLE[kind].icon)}
          title={data.connector ? `${data.connector.name} connection` : undefined}>
          {data.connector ? <ConnectorIcon pluginId={data.connector.pluginId} name={data.connector.name} className="h-5 w-5" /> : <StageKindIcon kind={kind} className="h-4.5 w-4.5" />}
          {working && (
            <span className="absolute -right-1 -top-1 flex h-2.5 w-2.5" title="Working on this run">
              <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-emerald-400 opacity-75" />
              <span className="relative inline-flex h-2.5 w-2.5 rounded-full border-2 border-white bg-emerald-500 dark:border-zinc-900" />
            </span>
          )}
        </span>
        <div className="min-w-0 flex-1">
          <div className={cx("truncate text-[13px] font-semibold text-zinc-900 dark:text-zinc-100", change === "removed" && "line-through")}>{stage.name}</div>
          <div className="truncate text-[10px] text-zinc-500" title={subtitle}>{KIND_STYLE[kind].label && <span className="font-semibold uppercase tracking-wide">{KIND_STYLE[kind].label} · </span>}{subtitle}</div>
        </div>
      </div>
      <p className={cx("mt-1.5 line-clamp-2 text-[11px] leading-snug text-zinc-600 dark:text-zinc-400", kind === "Script" && "font-mono")} title={body}>{body}</p>
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
        {!status && data.model && <span className="max-w-full truncate rounded-full bg-violet-50 px-1.5 py-0.5 text-violet-700 dark:bg-violet-500/10 dark:text-violet-300" title={data.model.title}>{data.model.label}</span>}
        {!status && mentionsIn(stage.instructions).filter((h) => h.startsWith("skill:")).map((h) => (
          <span key={h} className="rounded-full bg-emerald-50 px-1.5 py-0.5 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300" title="Loads and follows this skill">{h.slice(6)}</span>
        ))}
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
      {data.parts && <Handle type="source" id="parts" position={Position.Bottom} isConnectable={false} className="!h-1.5 !w-1.5 !border-0 !bg-zinc-400 dark:!bg-zinc-600" />}
      <Handle type="source" id="out" position={Position.Right} isConnectable={editable}
        title={editable ? "Drag to a stage to hand it this result, or to empty space to add a new agent there" : undefined}
        className={editable ? "!h-3.5 !w-3.5 !border-2 !border-white !bg-brand-500 hover:!scale-125 dark:!border-zinc-900" : "!h-2 !w-2 !border-0 !bg-zinc-400 dark:!bg-zinc-600"} />
    </div>
  );
}

/** One of an agent's parts (model, tools, memory), hanging below it as in n8n. */
function PartNode({ data }: NodeProps & { data: PartNodeData }) {
  return (
    <div className="group flex flex-col items-center text-center" style={{ width: PART_WIDTH }} title={data.title}>
      <Handle type="target" position={Position.Top} isConnectable={false} className="!h-1 !w-1 !border-0 !bg-transparent" />
      <span className="flex h-9 w-9 items-center justify-center rounded-full border border-dashed border-zinc-300 bg-white text-zinc-500 shadow-sm transition group-hover:border-brand-400 group-hover:text-brand-600 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-400">
        <PartIcon part={data.part} className="h-4 w-4" />
      </span>
      <span className="mt-1 text-[10px] font-medium text-zinc-700 dark:text-zinc-300">{data.label}</span>
      {data.connectors && data.connectors.length > 0 ? (
        <span className="mt-0.5 flex items-center -space-x-1">
          {data.connectors.slice(0, 3).map((c) => (
            <span key={c.name} className="flex h-4 w-4 items-center justify-center rounded-full border border-zinc-200 bg-white dark:border-zinc-700 dark:bg-zinc-100">
              <ConnectorIcon pluginId={c.pluginId} name={c.name} className="h-2.5 w-2.5" />
            </span>
          ))}
          {data.connectors.length > 3 && <span className="pl-1.5 text-[9px] text-zinc-500">+{data.connectors.length - 3}</span>}
        </span>
      ) : (
        <span className="w-full truncate text-[9px] leading-tight text-zinc-500">{data.detail}</span>
      )}
    </div>
  );
}

/** What starts runs: a form, webhook, schedule or watch, or a person (the chat or Run). */
function TriggerNode({ data }: NodeProps & { data: TriggerNodeData }) {
  return (
    <div className="flex items-center gap-2 rounded-full border border-zinc-200 bg-white py-1.5 pl-1.5 pr-3 shadow-sm hover:border-brand-300 dark:border-zinc-800 dark:bg-zinc-900" style={{ width: TRIGGER_WIDTH }}>
      <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-300">
        <TriggerIcon kind={data.kind} className="h-3.5 w-3.5" />
      </span>
      <span className="min-w-0">
        <span className="block truncate text-[11px] font-semibold text-zinc-800 dark:text-zinc-200">{data.name}</span>
        <span className="block truncate text-[9px] uppercase tracking-wide text-zinc-500">{data.detail}</span>
      </span>
      <Handle type="source" position={Position.Right} isConnectable={false} className="!h-1.5 !w-1.5 !border-0 !bg-amber-400" />
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

const nodeTypes = { stage: StageNode, part: PartNode, trigger: TriggerNode };
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
  /** A new stage dropped on the canvas at `position`, taking `inputs`' results. */
  onAddAt: (position: StagePosition, inputs: string[], kind?: StageKind) => void;
};

const PART_ID = "part:";
const TRIGGER_ID = "trigger:";
const isStageNode = (id: string) => !id.startsWith(PART_ID) && !id.startsWith(TRIGGER_ID);

/** An agent's chosen model as its stage shows it: the profile's model, not its id. */
function modelOf(stage: PipelineStage, models: ModelChoice[]): StageNodeData["model"] {
  const id = stage.model_profile_id;
  if (!id) return undefined;
  const profile = models.find((m) => m.id === id);
  if (!profile) return { label: id === "server" ? "default model" : `@${id}`, title: id === "server" ? "Runs on the server's default model" : `Model profile '${id}' (no longer exists)` };
  return { label: profile.model, title: `Runs on ${profile.name} (${profile.provider} · ${profile.model}). Mention: @${id === "server" ? "default-model" : id}` };
}

const markOf = (c?: ConnectionView): ConnectorMark | undefined => c && { pluginId: c.plugin_id, name: c.name };

/** What an agent's parts say: which model it runs on, which tools it has, what it remembers. */
function partsOf(stage: PipelineStage, models: ModelChoice[], connections: ConnectionView[]): PartNodeData[] {
  const chosen = stage.model_profile_id ? models.find((m) => m.id === stage.model_profile_id) : undefined;
  const fallback = models.find((m) => m.is_default) ?? models[0];
  const model = chosen ?? fallback;
  const withTools = connections.filter((c) => c.supports_tools);
  const tools = stage.capabilities.length + withTools.length;
  return [
    {
      part: "model",
      label: "Model",
      detail: model ? model.model : stage.model_profile_id ?? "default",
      title: model ? `${chosen ? "" : "Workspace default: "}${model.name} (${model.provider} · ${model.model}). Click to change.` : "The workspace's model.",
    },
    {
      part: "tools",
      label: "Tools",
      detail: `${tools} + files`,
      title: `Capabilities: ${stage.capabilities.join(", ") || "none"}. Connections: ${withTools.map((c) => c.name).join(", ") || "none"}. Click to change.`,
      connectors: withTools.map((c) => ({ pluginId: c.plugin_id, name: c.name })),
    },
    {
      part: "memory",
      label: "Memory",
      detail: "workspace",
      title: "The workspace's shared memory and knowledge (read_memory, write_memory, search_knowledge), plus the run's files.",
    },
  ];
}

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
  /** Agents show their model, tools and memory below them. */
  showParts?: boolean;
  /** Shows the switch for the agents' parts on the canvas; without it, there's no switch. */
  onToggleParts?: () => void;
  models?: ModelChoice[];
  connections?: ConnectionView[];
  /** Shown at the left, feeding the entry stages. */
  triggers?: TriggerView[];
  onSelectPart?: (stageId: string, part: AgentPart) => void;
  onSelectTrigger?: (triggerId: string) => void;
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
  showParts = false,
  onToggleParts,
  models = [],
  connections = [],
  triggers,
  onSelectPart,
  onSelectTrigger,
}: Parameters<typeof PipelineCanvas>[0]) {
  const editable = !!editing;
  const row = showParts ? ROW_WITH_PARTS : ROW;
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
    const placed = placeStages(pipeline.stages, pipeline.layout, row);
    if (local) for (const [id, p] of Object.entries(local)) if (placed.has(id)) placed.set(id, p);
    return placed;
  }, [pipeline, local, row]);

  const { nodes, edges } = useMemo(() => {
    const removed = baseline ? baseline.stages.filter((b) => !pipeline.stages.some((s) => s.stage_id === b.stage_id)) : [];
    const all = new Map(positions);
    const lastColumn = Math.max(0, ...[...all.values()].map((p) => p.x));
    removed.forEach((s, i) => all.set(s.stage_id, baseline?.layout?.[s.stage_id] ?? { x: lastColumn + COLUMN, y: i * row }));
    const runs = new Map((runStages ?? []).map((r) => [r.stage_id, r]));
    const hasDependents = new Set(pipeline.stages.flatMap((s) => s.inputs));

    const nodes: Node<StageNodeData | PartNodeData | TriggerNodeData>[] = [
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
            parts: showParts && kindOf(stage) === "Agent",
            connector: kindOf(stage) === "Action" ? markOf(connectionOfTool(stage.tool, connections)) : undefined,
            model: modelOf(stage, models),
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
          sourceHandle: "out",
          target: stage.stage_id,
          targetHandle: "in",
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

    // An agent's parts hang below it and move with it.
    if (showParts) {
      for (const stage of pipeline.stages.filter((s) => kindOf(s) === "Agent")) {
        const at = all.get(stage.stage_id)!;
        const height = measured[stage.stage_id]?.height ?? 118;
        partsOf(stage, models, connections).forEach((part, i) => {
          const id = `${PART_ID}${stage.stage_id}:${part.part}`;
          nodes.push({
            id,
            type: "part",
            position: { x: at.x + 6 + i * ((NODE_WIDTH - PART_WIDTH - 12) / 2), y: at.y + height + 42 },
            measured: measured[id],
            draggable: false,
            selectable: false,
            deletable: false,
            data: part,
          });
          edges.push({
            id: `${stage.stage_id}->${id}`,
            source: stage.stage_id,
            sourceHandle: "parts",
            target: id,
            selectable: false,
            deletable: false,
            style: { stroke: "#a1a1aa", strokeDasharray: "4 4", strokeWidth: 1.25 },
          });
        });
      }
    }

    // Triggers feed the entry stages from the left.
    const entries = pipeline.stages.filter((s) => s.inputs.length === 0);
    if (triggers && entries.length > 0) {
      const left = Math.min(...entries.map((s) => all.get(s.stage_id)!.x)) - TRIGGER_WIDTH - 90;
      const top = Math.min(...entries.map((s) => all.get(s.stage_id)!.y));
      const shown: (TriggerNodeData & { id: string })[] = [
        { id: "manual", kind: "Manual", name: "You", detail: "chat or Run" },
        ...triggers.map((t) => ({
          id: t.trigger_id,
          kind: t.kind,
          name: t.name,
          detail: t.kind === "Schedule" ? (t.cron ? `cron ${t.cron}` : `every ${Math.round((t.interval_seconds ?? 0) / 60)} min`) : t.kind.toLowerCase(),
        })),
      ];
      shown.forEach((t, i) => {
        const id = `${TRIGGER_ID}${t.id}`;
        nodes.push({ id, type: "trigger", position: { x: left, y: top + i * 64 }, measured: measured[id], draggable: false, selectable: false, deletable: false, data: t });
        for (const entry of entries) {
          edges.push({
            id: `${id}->${entry.stage_id}`,
            source: id,
            target: entry.stage_id,
            targetHandle: "in",
            selectable: false,
            deletable: false,
            style: { stroke: "#fbbf24", strokeDasharray: "2 4", strokeWidth: 1.25 },
          });
        }
      });
    }

    return { nodes, edges };
  }, [pipeline, baseline, runStages, editing, editable, selectedStageId, selectedEdge, positions, measured, row, showParts, models, connections, triggers]);

  const onNodesChange = useCallback((changes: NodeChange<Node<StageNodeData | PartNodeData | TriggerNodeData>>[]) => {
    const sizes = changes.filter((c) => c.type === "dimensions" && c.dimensions);
    if (sizes.length > 0) {
      setMeasured((m) => {
        const next = { ...m };
        for (const c of sizes) if (c.type === "dimensions" && c.dimensions) next[c.id] = c.dimensions;
        return next;
      });
    }
    const moves = changes.filter((c) => c.type === "position" && c.position && isStageNode(c.id));
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
    if (c.source && c.target && c.source !== c.target && isStageNode(c.source) && isStageNode(c.target)) editing?.onConnect(c.source, c.target);
  }, [editing]);

  // A connection dropped on empty space adds a new agent there, taking the source's result.
  const connectEnd = useCallback((event: MouseEvent | TouchEvent, state: FinalConnectionState) => {
    if (!editing || state.isValid || state.toNode || !state.fromNode || !isStageNode(state.fromNode.id)) return;
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
        const kind = e.dataTransfer.getData(NEW_AGENT_DRAG);
        editing.onAddAt({ x: at.x - NODE_WIDTH / 2, y: at.y - 40 }, [], kind === "Action" || kind === "Script" ? kind : "Agent");
      }}
    >
    <ReactFlow
      key={`${size}|${pipeline.stages.map((s) => s.stage_id).join(",")}|${showParts}|${triggers?.length ?? -1}`}
      nodes={nodes}
      edges={edges}
      nodeTypes={nodeTypes}
      edgeTypes={edgeTypes}
      onNodeClick={(_, node) => {
        if (node.id.startsWith(PART_ID)) {
          const [stageId, part] = node.id.slice(PART_ID.length).split(":");
          onSelectPart?.(stageId, part as AgentPart);
        } else if (node.id.startsWith(TRIGGER_ID)) {
          onSelectTrigger?.(node.id.slice(TRIGGER_ID.length));
        } else {
          onSelectStage(node.id);
        }
      }}
      onPaneClick={() => { onSelectStage(null); setSelectedEdge(null); }}
      onNodesChange={onNodesChange}
      onEdgesChange={onEdgesChange}
      onConnect={connect}
      onConnectEnd={connectEnd}
      isValidConnection={(c) => c.source !== c.target && isStageNode(c.source) && isStageNode(c.target) &&
        !pipeline.stages.find((s) => s.stage_id === c.target)?.inputs.includes(c.source)}
      onEdgesDelete={(deleted) => deleted.filter((e) => isStageNode(e.source) && isStageNode(e.target)).forEach((e) => editing?.onDisconnect(e.source, e.target))}
      onNodesDelete={(deleted) => deleted.filter((n) => isStageNode(n.id)).slice(0, 1).forEach((n) => editing?.onRemove(n.id))}
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
      {onToggleParts && pipeline.stages.some((s) => kindOf(s) === "Agent") && (
        <Panel position="top-right" className="!m-2">
          <button
            onClick={onToggleParts}
            role="switch"
            aria-checked={showParts}
            title={showParts ? "Hide each agent's model, tools and memory" : "Show each agent's model, tools and memory"}
            className="inline-flex items-center gap-2 rounded-lg border border-zinc-200 bg-white px-2.5 py-1.5 text-xs text-zinc-600 shadow-sm hover:border-zinc-300 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-300"
          >
            <PartIcon part="model" className="h-3.5 w-3.5" />
            Model, tools &amp; memory
            <span className={cx("relative inline-flex h-4 w-7 shrink-0 rounded-full transition-colors", showParts ? "bg-brand-500" : "bg-zinc-300 dark:bg-zinc-600")}>
              <span className={cx("absolute top-0.5 h-3 w-3 rounded-full bg-white shadow transition-all", showParts ? "left-3.5" : "left-0.5")} />
            </span>
          </button>
        </Panel>
      )}
      {pipeline.stages.length === 0 && (
        <Panel position="top-center" className="!mt-24">
          <div className="max-w-sm rounded-xl border border-dashed border-zinc-300 bg-white/90 p-4 text-center text-xs text-zinc-500 shadow-sm dark:border-zinc-700 dark:bg-zinc-900/90">
            <div className="mb-1 text-sm font-semibold text-zinc-700 dark:text-zinc-200">A blank canvas</div>
            {editing
              ? <>Drag a <b>New agent</b>, <b>Action</b> or <b>Script</b> from the top left onto the canvas (or click one), then connect stages by dragging between their dots. Or describe the pipeline you want above.</>
              : "This pipeline has no stages yet."}
          </div>
        </Panel>
      )}
      {editing && (
        <Panel position="top-left" className="!m-2 flex items-center gap-1.5">
          {([
            { kind: "Agent", label: "New agent", title: "An AI agent with instructions and tools" },
            { kind: "Action", label: "Action", title: "Call a connected service (Sheets, Airtable, BigQuery, Zoho CRM, an API) with data from the run. No model." },
            { kind: "Script", label: "Script", title: "Python or JavaScript that turns the run's data into new data. No model." },
          ] as const).map((item) => (
            <button
              key={item.kind}
              draggable
              onDragStart={(e) => { e.dataTransfer.setData(NEW_AGENT_DRAG, item.kind); e.dataTransfer.effectAllowed = "copy"; }}
              onClick={() => editing.onAddAt(freeSpot(), [], item.kind)}
              title={`${item.title}. Drag onto the canvas, or click to add.`}
              className={cx("inline-flex cursor-grab items-center gap-1.5 rounded-lg border border-dashed bg-white px-2.5 py-1.5 text-xs font-medium shadow-sm active:cursor-grabbing dark:bg-zinc-900",
                item.kind === "Agent" ? "border-brand-300 text-brand-700 hover:border-brand-500 dark:border-brand-700 dark:text-brand-300"
                  : item.kind === "Action" ? "border-sky-300 text-sky-700 hover:border-sky-500 dark:border-sky-700 dark:text-sky-300"
                  : "border-violet-300 text-violet-700 hover:border-violet-500 dark:border-violet-700 dark:text-violet-300")}
            >
              <StageKindIcon kind={item.kind} className="h-4 w-4" /> {item.label}
            </button>
          ))}
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
