import type { AgentListItem } from "./types";

export interface LayoutNode {
  id: string;
  x: number;
  y: number;
}

const COL_WIDTH = 220;
const ROW_HEIGHT = 140;

/**
 * Simple tree layout: y comes from depth, x comes from left-to-right leaf order in a DFS walk so
 * siblings and their descendants never overlap. Good enough for the agent trees this runtime
 * produces (tens, not thousands, of nodes).
 */
export function computeTreeLayout(agents: AgentListItem[]): Map<string, LayoutNode> {
  const byId = new Map(agents.map((a) => [a.agent_id, a]));
  const childrenOf = new Map<string, string[]>();
  for (const agent of agents) {
    const key = agent.parent_agent_id ?? "__root__";
    if (!childrenOf.has(key)) childrenOf.set(key, []);
    childrenOf.get(key)!.push(agent.agent_id);
  }

  const roots = (childrenOf.get("__root__") ?? []).filter((id) => byId.has(id));
  const positions = new Map<string, LayoutNode>();
  let nextLeafSlot = 0;

  function visit(id: string): number {
    const agent = byId.get(id);
    if (!agent) return nextLeafSlot;

    const kids = (childrenOf.get(id) ?? []).filter((k) => byId.has(k));
    let x: number;

    if (kids.length === 0) {
      x = nextLeafSlot * COL_WIDTH;
      nextLeafSlot += 1;
    } else {
      const childXs = kids.map((k) => visit(k));
      x = (Math.min(...childXs) + Math.max(...childXs)) / 2;
    }

    positions.set(id, { id, x, y: agent.depth * ROW_HEIGHT });
    return x;
  }

  for (const rootId of roots) visit(rootId);

  return positions;
}
