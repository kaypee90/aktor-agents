"use client";

import { useMemo } from "react";
import { Background, Controls, MarkerType, ReactFlow, type Edge, type Node } from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import type { WorldSnapshot } from "@/lib/worldTypes";
import { LocationNode, type LocationNodeData } from "./LocationNode";
import { RESIDENT_NODE_HEIGHT, RESIDENT_NODE_WIDTH, ResidentNode, type ResidentNodeData } from "./ResidentNode";
import { isLiving } from "./worldUi";

const nodeTypes = { resident: ResidentNode, location: LocationNode };

const COLS_PER_LOCATION = 2;
const SLOT_W = RESIDENT_NODE_WIDTH + 20;
// Extra headroom above each resident for its speech bubble.
const SLOT_H = RESIDENT_NODE_HEIGHT + 44;
const HEADER_H = 52;
const PAD = 14;
const LOCATION_W = COLS_PER_LOCATION * SLOT_W + PAD * 2 - 20;
const LOCATIONS_PER_ROW = 3;
const GAP = 60;

/** Speech bubbles and message arrows stay on the map for this many ticks. */
const RECENT_TICKS = 1;

export function WorldMap({
  world,
  selectedId,
  onSelect,
}: {
  world: WorldSnapshot;
  selectedId: string | null;
  onSelect: (agentId: string) => void;
}) {
  const { nodes, edges } = useMemo(() => {
    const living = world.residents.filter(isLiving);
    const recent = world.activity.filter((a) => a.tick >= world.tick - RECENT_TICKS);

    // Latest thing each resident said out loud, privately, or on the board.
    const bubbles = new Map<string, { text: string; private: boolean }>();
    for (const a of recent) {
      if (!a.actor_id || !a.quote) continue;
      if (a.kind === "said" || a.kind === "posted") bubbles.set(a.actor_id, { text: a.quote, private: false });
      if (a.kind === "talked") bubbles.set(a.actor_id, { text: `→ ${a.quote}`, private: true });
    }

    const nodes: Node[] = [];
    let rowY = 0;
    const rows: string[][] = [];
    world.locations.forEach((l, i) => {
      if (i % LOCATIONS_PER_ROW === 0) rows.push([]);
      rows[rows.length - 1].push(l.name);
    });

    for (const row of rows) {
      const heights = row.map((name) => {
        const count = living.filter((r) => r.location === name).length;
        return HEADER_H + Math.max(1, Math.ceil(count / COLS_PER_LOCATION)) * SLOT_H + PAD;
      });
      const rowHeight = Math.max(...heights);

      row.forEach((name, col) => {
        const location = world.locations.find((l) => l.name === name)!;
        const here = living.filter((r) => r.location === name);
        const groupId = `loc:${name}`;

        // Parents must precede their children in the node array.
        nodes.push({
          id: groupId,
          type: "location",
          position: { x: col * (LOCATION_W + GAP), y: rowY },
          style: { width: LOCATION_W, height: rowHeight },
          data: { name, description: location.description, count: here.length } satisfies LocationNodeData,
          selectable: false,
          draggable: false,
        });

        here.forEach((r, i) => {
          nodes.push({
            id: r.agent_id,
            type: "resident",
            parentId: groupId,
            extent: "parent",
            position: {
              x: PAD + (i % COLS_PER_LOCATION) * SLOT_W,
              y: HEADER_H + Math.floor(i / COLS_PER_LOCATION) * SLOT_H + 30,
            },
            data: {
              resident: r,
              maxEnergy: world.max_energy || 100,
              bubble: bubbles.get(r.agent_id) ?? null,
              busy: r.agent_status === "Thinking" || r.agent_status === "Executing",
              worldEnded: world.status === "Ended",
            } satisfies ResidentNodeData,
            selected: r.agent_id === selectedId,
            draggable: false,
          });
        });
      });

      rowY += rowHeight + GAP;
    }

    const onMap = new Set(living.map((r) => r.agent_id));
    const edges: Edge[] = [];

    // Recent private messages and energy gifts, drawn as arrows between residents.
    for (const a of recent) {
      if (!a.actor_id || !a.target_id || !onMap.has(a.actor_id) || !onMap.has(a.target_id)) continue;
      if (a.kind !== "talked" && a.kind !== "gave") continue;
      const color = a.kind === "talked" ? "#c026d3" : "#d97706";
      edges.push({
        id: `act-${a.seq}`,
        source: a.actor_id,
        target: a.target_id,
        animated: true,
        label: a.kind === "talked" ? "message" : "energy",
        labelStyle: { fontSize: 10, fill: color },
        style: { stroke: color, strokeWidth: 2 },
        markerEnd: { type: MarkerType.ArrowClosed, color },
      });
    }

    // Lineage: who brought whom into the world.
    for (const r of living) {
      if (r.parent_agent_id && onMap.has(r.parent_agent_id)) {
        edges.push({
          id: `kin-${r.parent_agent_id}-${r.agent_id}`,
          source: r.parent_agent_id,
          target: r.agent_id,
          style: { stroke: "#94a3b8", strokeDasharray: "4 4" },
        });
      }
    }

    return { nodes, edges };
  }, [world, selectedId]);

  return (
    <ReactFlow
      nodes={nodes}
      edges={edges}
      nodeTypes={nodeTypes}
      onNodeClick={(_, node) => node.type === "resident" && onSelect(node.id)}
      nodesDraggable={false}
      nodesConnectable={false}
      fitView
      fitViewOptions={{ padding: 0.15 }}
      minZoom={0.2}
      proOptions={{ hideAttribution: true }}
    >
      <Background gap={24} />
      <Controls showInteractive={false} />
    </ReactFlow>
  );
}
