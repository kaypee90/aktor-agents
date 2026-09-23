"use client";

import { useMemo } from "react";
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  type Edge,
  type Node,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { computeTreeLayout } from "@/lib/layout";
import type { AgentListItem } from "@/lib/types";
import { AgentNode, type AgentNodeData } from "./AgentNode";

const nodeTypes = { agent: AgentNode };

export function AgentGraph({
  agents,
  selectedId,
  onSelect,
}: {
  agents: AgentListItem[];
  selectedId: string | null;
  onSelect: (agentId: string) => void;
}) {
  const { nodes, edges } = useMemo(() => {
    const layout = computeTreeLayout(agents);

    const nodes: Node<AgentNodeData>[] = agents.map((agent) => {
      const pos = layout.get(agent.agent_id) ?? { x: 0, y: agent.depth * 140 };
      return {
        id: agent.agent_id,
        type: "agent",
        position: { x: pos.x, y: pos.y },
        data: { agent },
        selected: agent.agent_id === selectedId,
      };
    });

    const edges: Edge[] = agents
      .filter((a) => a.parent_agent_id)
      .map((a) => ({
        id: `${a.parent_agent_id}->${a.agent_id}`,
        source: a.parent_agent_id!,
        target: a.agent_id,
        animated: a.status === "Executing" || a.status === "Thinking" || a.status === "Spawning",
        style: { stroke: "#94a3b8", strokeWidth: 1.5 },
      }));

    return { nodes, edges };
  }, [agents, selectedId]);

  if (agents.length === 0) {
    return (
      <div className="flex h-full items-center justify-center text-sm text-neutral-500">
        No agents yet. Submit a goal to create the root agent.
      </div>
    );
  }

  return (
    <ReactFlow
      nodes={nodes}
      edges={edges}
      nodeTypes={nodeTypes}
      onNodeClick={(_, node) => onSelect(node.id)}
      fitView
      fitViewOptions={{ padding: 0.3 }}
      proOptions={{ hideAttribution: true }}
    >
      <Background gap={24} />
      <Controls showInteractive={false} />
      <MiniMap pannable zoomable className="!bg-neutral-100 dark:!bg-neutral-900" />
    </ReactFlow>
  );
}
