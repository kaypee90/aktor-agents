"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { Background, Controls, MarkerType, ReactFlow, type Edge, type FitViewOptions, type Node } from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import type { RuntimeEvent } from "@/lib/types";
import type { WorkspaceAgentView, WorkspaceSnapshot } from "@/lib/workspaceTypes";
import { TEAM_NODE_HEIGHT, TEAM_NODE_WIDTH, TeamAgentNode, TeamUserNode, USER_NODE_HEIGHT, USER_NODE_WIDTH, type Bubble } from "./TeamNode";
import { CHAT_WIDGET_WIDTH } from "./WorkspaceChatWidget";

const nodeTypes = { agent: TeamAgentNode, user: TeamUserNode };

const USER_ID = "user";
/** Arrows and speech bubbles stay on the canvas this long after the event. */
const RECENT_MS = 20_000;
/** Finished workers stay visible this long; older ones are hidden unless asked for. */
const FINISHED_VISIBLE_MS = 30 * 60_000;
const H_GAP = 36;
// Headroom above each node for its speech bubble.
const V_GAP = 72;
const TERMINAL = new Set(["Completed", "Failed", "TimedOut", "Terminated"]);
/** Below this much free width beside the open chat, fitting the team into it would shrink it to
 * nothing: the chat overlays the canvas instead, and minimising it shows the team. */
const MIN_TEAM_WIDTH = 280;

type Link = { from: string; to: string; label: string; color: string };

const MESSAGE_KINDS: Record<string, { label: string; color: string }> = {
  TaskRequest: { label: "task", color: "#2563eb" },
  DelegationRequest: { label: "task", color: "#2563eb" },
  CompletionNotification: { label: "done", color: "#059669" },
  FailureNotification: { label: "failed", color: "#e11d48" },
  InformationRequest: { label: "question", color: "#7c3aed" },
  InformationResponse: { label: "answer", color: "#7c3aed" },
  TaskResponse: { label: "result", color: "#059669" },
  StatusUpdate: { label: "update", color: "#64748b" },
};
const SPAWN = { label: "started", color: "#0d9488" };
const TO_YOU = { label: "to you", color: "#2563eb" };
const FROM_YOU = { label: "you", color: "#2563eb" };

/** Tool calls worth a bubble; talking and waiting already show as arrows or status. */
const QUIET_TOOLS = new Set(["send_message", "spawn_agent", "notify_user", "wait_for_events", "complete_task", "find_agents"]);

function excerpt(text: string, max = 90) {
  const flat = text.replace(/\s+/g, " ").trim();
  return flat.length > max ? `${flat.slice(0, max)}…` : flat;
}

function toolBubble(tool: string, argumentsJson: string | undefined): string {
  let args: Record<string, unknown> = {};
  try {
    args = JSON.parse(argumentsJson ?? "{}") as Record<string, unknown>;
  } catch {
    // Unreadable arguments: fall back to the tool name.
  }
  if (tool === "plan_request") return "🧭 planning the work";
  if (tool === "filesystem_write") return `📄 saving ${String(args.path ?? "a file")}`;
  if (tool === "web_search") return `🔎 ${String(args.query ?? "searching")}`;
  if (tool.startsWith("create_")) return `⏰ ${tool.replace("create_", "setting up a ")}`;
  return `🔧 ${tool}`;
}

/** Recent communication, as arrows between agents and speech bubbles on them. */
function recentActivity(events: RuntimeEvent[], now: number, coordinatorId: string, visible: Set<string>) {
  const links = new Map<string, Link>();
  const bubbles = new Map<string, Bubble>();
  const on = (id: string | null | undefined): id is string => !!id && visible.has(id);

  for (const e of events) {
    if (now - Date.parse(e.timestamp) > RECENT_MS) continue;
    switch (e.type) {
      case "AgentMessageSent": {
        if (!on(e.agent_id) || !on(e.target_agent_id)) break;
        const kind = MESSAGE_KINDS[e.data.messageType] ?? { label: (e.data.messageType ?? "message").toLowerCase(), color: "#7c3aed" };
        links.set(`${e.agent_id}>${e.target_agent_id}`, { from: e.agent_id, to: e.target_agent_id, ...kind });
        if (e.data.payload) bubbles.set(e.agent_id, { text: excerpt(e.data.payload), tone: "message" });
        break;
      }
      case "AgentSpawned":
        if (on(e.agent_id) && on(e.target_agent_id)) links.set(`${e.agent_id}>${e.target_agent_id}`, { from: e.agent_id, to: e.target_agent_id, ...SPAWN });
        break;
      case "WorkspaceMessage":
        if (e.data.author_kind === "User") {
          links.set(`${USER_ID}>${coordinatorId}`, { from: USER_ID, to: coordinatorId, ...FROM_YOU });
          bubbles.set(USER_ID, { text: excerpt(e.data.text ?? ""), tone: "user" });
        } else if (e.data.author_kind === "Agent" && on(e.data.author_id)) {
          links.set(`${e.data.author_id}>${USER_ID}`, { from: e.data.author_id, to: USER_ID, ...TO_YOU });
          bubbles.set(e.data.author_id, { text: excerpt(e.data.text ?? ""), tone: "message" });
        }
        break;
      case "AgentToolCalled":
        if (on(e.agent_id) && e.data.tool && !QUIET_TOOLS.has(e.data.tool)) {
          bubbles.set(e.agent_id, { text: toolBubble(e.data.tool, e.data.arguments), tone: "tool" });
        }
        break;
    }
  }

  return { links: [...links.values()], bubbles };
}

/** Tidy tree: each node is centred over its children, siblings side by side. */
function layout(rootId: string, childrenOf: Map<string, string[]>) {
  const widths = new Map<string, number>();
  const width = (id: string): number => {
    const kids = childrenOf.get(id) ?? [];
    const w = Math.max(TEAM_NODE_WIDTH, kids.reduce((sum, k) => sum + width(k), 0) + H_GAP * Math.max(0, kids.length - 1));
    widths.set(id, w);
    return w;
  };
  width(rootId);

  const positions = new Map<string, { x: number; y: number }>();
  const place = (id: string, left: number, depth: number) => {
    const w = widths.get(id)!;
    positions.set(id, { x: left + (w - TEAM_NODE_WIDTH) / 2, y: depth * (TEAM_NODE_HEIGHT + V_GAP) });
    let x = left;
    for (const k of childrenOf.get(id) ?? []) {
      place(k, x, depth + 1);
      x += widths.get(k)! + H_GAP;
    }
  };
  place(rootId, 0, 0);
  return positions;
}

export function WorkspaceTeamView({ workspace, events, selectedId, onSelect, chatOpen }: {
  workspace: WorkspaceSnapshot;
  events: RuntimeEvent[];
  selectedId: string | null;
  onSelect: (agentId: string) => void;
  /** The chat widget floats over the canvas's right side; the team is fitted beside it. */
  chatOpen: boolean;
}) {
  const [now, setNow] = useState(() => Date.now());
  const [showAllFinished, setShowAllFinished] = useState(false);
  const container = useRef<HTMLDivElement>(null);
  const [canvasWidth, setCanvasWidth] = useState(0);

  // The canvas narrows when the agent details panel opens; the team is re-fitted to the new width.
  useEffect(() => {
    const el = container.current;
    if (!el) return;
    const observer = new ResizeObserver(([entry]) => setCanvasWidth(Math.round(entry.contentRect.width)));
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const chatGap = CHAT_WIDGET_WIDTH + 32;
  const besideChat = chatOpen && canvasWidth - chatGap >= MIN_TEAM_WIDTH;
  const fitViewOptions = useMemo<FitViewOptions>(() => ({
    maxZoom: 1.1,
    padding: { top: "56px", left: "24px", bottom: "24px", right: besideChat ? `${chatGap}px` : "24px" },
  }), [besideChat, chatGap]);

  // Arrows and bubbles age out on their own, without waiting for the next event.
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);

  const coordinatorId = workspace.coordinator_agent_id;

  const { visibleAgents, hiddenFinished } = useMemo(() => {
    const recentlyFinished = (a: WorkspaceAgentView) =>
      !a.completed_at || now - Date.parse(a.completed_at) < FINISHED_VISIBLE_MS;
    const shown = workspace.agents.filter((a) =>
      a.agent_id === coordinatorId || !TERMINAL.has(a.status) || showAllFinished || recentlyFinished(a));
    return { visibleAgents: shown, hiddenFinished: workspace.agents.length - shown.length };
  }, [workspace.agents, coordinatorId, showAllFinished, now]);

  const visibleIds = useMemo(() => new Set([USER_ID, ...visibleAgents.map((a) => a.agent_id)]), [visibleAgents]);

  const { nodes, edges } = useMemo(() => {
    // The user sits above the coordinator; an agent whose parent is hidden hangs off the coordinator.
    const childrenOf = new Map<string, string[]>([[USER_ID, [coordinatorId]]]);
    for (const a of visibleAgents) {
      if (a.agent_id === coordinatorId) continue;
      const parent = a.parent_agent_id && visibleIds.has(a.parent_agent_id) ? a.parent_agent_id : coordinatorId;
      childrenOf.set(parent, [...(childrenOf.get(parent) ?? []), a.agent_id]);
    }

    const positions = layout(USER_ID, childrenOf);
    const { links, bubbles } = recentActivity(events, now, coordinatorId, visibleIds);

    const nodes: Node[] = [
      {
        id: USER_ID,
        type: "user",
        // Centre the pill over the coordinator: it's narrower than an agent node.
        position: { x: positions.get(USER_ID)!.x + (TEAM_NODE_WIDTH - USER_NODE_WIDTH) / 2, y: positions.get(USER_ID)!.y + 28 },
        measured: { width: USER_NODE_WIDTH, height: USER_NODE_HEIGHT },
        data: { bubble: bubbles.get(USER_ID) ?? null },
        draggable: false,
        selectable: false,
      },
      ...visibleAgents.filter((a) => positions.has(a.agent_id)).map((a) => ({
        id: a.agent_id,
        type: "agent",
        position: positions.get(a.agent_id)!,
        // Already measured (the size is fixed), so rebuilding the node never hides it.
        measured: { width: TEAM_NODE_WIDTH, height: TEAM_NODE_HEIGHT },
        data: { agent: a, bubble: bubbles.get(a.agent_id) ?? null },
        selected: a.agent_id === selectedId,
        draggable: false,
      })),
    ];

    const edges: Edge[] = [];
    // The team's structure: who started whom.
    for (const [parent, kids] of childrenOf) {
      for (const kid of kids) {
        edges.push({ id: `tree-${parent}-${kid}`, source: parent, target: kid, style: { stroke: "#cbd5e1", strokeDasharray: "4 4" } });
      }
    }
    // What just happened between them.
    for (const l of links) {
      edges.push({
        id: `live-${l.from}-${l.to}`,
        source: l.from,
        target: l.to,
        animated: true,
        label: l.label,
        labelStyle: { fontSize: 10, fill: l.color },
        labelBgStyle: { fillOpacity: 0.85 },
        style: { stroke: l.color, strokeWidth: 2 },
        markerEnd: { type: MarkerType.ArrowClosed, color: l.color },
      });
    }

    return { nodes, edges };
  }, [visibleAgents, visibleIds, coordinatorId, events, now, selectedId]);

  return (
    <div ref={container} className="relative h-full">
      {/* Re-fit when the team changes shape, the chat opens or closes, or the canvas is resized;
          not on every activity tick. */}
      <ReactFlow
        key={`${[...visibleIds].join(",")}|${besideChat}|${Math.round(canvasWidth / 50)}`}
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        onNodeClick={(_, node) => node.type === "agent" && onSelect(node.id)}
        nodesDraggable={false}
        nodesConnectable={false}
        fitView
        fitViewOptions={fitViewOptions}
        minZoom={0.2}
        proOptions={{ hideAttribution: true }}
      >
        <Background gap={24} />
        <Controls showInteractive={false} position="bottom-left" fitViewOptions={fitViewOptions} />
      </ReactFlow>

      <div className="pointer-events-none absolute left-3 top-3 flex flex-wrap gap-x-3 gap-y-1 rounded-md bg-white/85 px-2 py-1 text-[10px] text-neutral-500 shadow-sm dark:bg-neutral-900/85">
        <span><span className="text-blue-600">━</span> task / chat</span>
        <span><span className="text-emerald-600">━</span> done</span>
        <span><span className="text-violet-600">━</span> question / answer</span>
        <span><span className="text-teal-600">━</span> started</span>
        <span><span className="text-slate-400">┅</span> team</span>
      </div>

      {hiddenFinished > 0 && (
        <button
          onClick={() => setShowAllFinished(true)}
          className="absolute right-3 top-3 rounded-md border border-neutral-300 bg-white px-2 py-1 text-[11px] text-neutral-600 shadow-sm hover:bg-neutral-50 dark:border-neutral-700 dark:bg-neutral-900 dark:text-neutral-300"
        >
          Show {hiddenFinished} earlier finished agent{hiddenFinished === 1 ? "" : "s"}
        </button>
      )}
      {showAllFinished && (
        <button
          onClick={() => setShowAllFinished(false)}
          className="absolute right-3 top-3 rounded-md border border-neutral-300 bg-white px-2 py-1 text-[11px] text-neutral-600 shadow-sm hover:bg-neutral-50 dark:border-neutral-700 dark:bg-neutral-900 dark:text-neutral-300"
        >
          Hide earlier finished agents
        </button>
      )}
    </div>
  );
}
