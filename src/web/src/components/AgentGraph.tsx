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
import type { AgentListItem, AgentSpend } from "@/lib/types";
import { AgentNode, type AgentNodeData } from "./AgentNode";

const nodeTypes = { agent: AgentNode };

export function AgentGraph({
  agents,
  spend = {},
  selectedId,
  onSelect,
  modelNames = {},
}: {
  agents: AgentListItem[];
  /** Model names by id, to label agents given their own model at spawn. */
  modelNames?: Record<string, string>;
  /** Spend per agent and branch, when known (tasks only). */
  spend?: Record<string, AgentSpend>;
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
        data: {
          agent,
          spend: spend[agent.agent_id],
          model: agent.model_profile_id ? modelNames[agent.model_profile_id] ?? agent.model_profile_id : undefined,
        },
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
  }, [agents, spend, selectedId, modelNames]);

  if (agents.length === 0) {
    return (
      <div className="flex h-full items-center justify-center text-sm text-zinc-500">
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
      {/* A small tree fits on screen; the overview only helps once it doesn't. */}
      {agents.length > 8 && (
        <MiniMap pannable zoomable nodeBorderRadius={6} className="!rounded-lg !border !border-zinc-200 !bg-white dark:!border-zinc-800 dark:!bg-zinc-950" />
      )}
    </ReactFlow>
  );
}
