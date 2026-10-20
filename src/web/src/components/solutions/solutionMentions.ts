import type { GlobalConnector } from "@/lib/api";
import type { BuilderSkill, Json } from "@/lib/solutionPackage";
import { knowledgeHandle, mentionsIn, type Mentionable } from "@/components/ui/MentionTextarea";

type Obj = { [key: string]: Json };
const obj = (v: Json | undefined): Obj => (v && typeof v === "object" && !Array.isArray(v) ? v : {});
const list = (v: Json | undefined): Json[] => (Array.isArray(v) ? v : []);
const str = (v: Json | undefined): string => (typeof v === "string" ? v : "");

/**
 * Where a text box in the builder is, which decides what its agents can reach (docs/solutions.md#mentions):
 * a workspace's goal, stages, triggers and evals reach its stages and the skills and knowledge it gets; the
 * assistant runs outside the workspaces, so it gets only the organization's; the designer can name anything.
 */
export type MentionScope =
  | { in: "workspace"; key: string }
  | { in: "assistant" }
  | { in: "skill"; workspace: string | null }
  | { in: "designer" };

/** What the organization already has: its own skills and knowledge, and its global connectors. */
export type OrgMentionables = { skills: Mentionable[]; knowledge: Mentionable[]; globals: GlobalConnector[] };

export const CONNECTOR_PREFIX = "connector:";

/** What a text box in the builder can @mention, the solution's own first. */
export function solutionMentionables(manifest: Obj, skills: BuilderSkill[], scope: MentionScope, org: OrgMentionables): Mentionable[] {
  const workspaces = list(manifest.workspaces).map(obj);
  const connectors = list(manifest.connectors).map(obj).filter((c) => str(c.name));
  // A workspace key, null for "every workspace", or undefined where the solution's own know-how doesn't go.
  const where = scope.in === "workspace" ? scope.key : scope.in === "skill" ? scope.workspace : scope.in === "designer" ? null : undefined;
  const reaches = (w: Json | undefined) => where !== undefined && (where === null || !str(w) || str(w) === where);
  const out: Mentionable[] = [];

  for (const w of workspaces) {
    if (scope.in === "designer") out.push({ kind: "workspace", handle: `workspace:${str(w.key)}`, label: str(w.name) || str(w.key), detail: "workspace" });
    if (scope.in !== "designer" && !(scope.in === "workspace" && scope.key === str(w.key))) continue;
    for (const s of list(obj(w.pipeline).stages).map(obj)) {
      if (!str(s.stage_id)) continue;
      out.push({ kind: "agent", handle: str(s.stage_id), label: str(s.name) || str(s.stage_id),
        detail: [str(s.role), scope.in === "designer" ? `stage in ${str(w.name) || str(w.key)}` : "stage"].filter(Boolean).join(" · ") });
    }
  }
  if (scope.in === "designer" && manifest.assistant) {
    out.push({ kind: "agent", handle: "assistant", label: str(obj(manifest.assistant).name) || "Assistant", detail: "what your customers talk to" });
  }

  for (const c of connectors) {
    const used = scope.in === "workspace" ? list(workspaces.find((w) => str(w.key) === scope.key)?.connectors).map(str)
      : scope.in === "assistant" ? list(obj(manifest.assistant).connectors).map(str) : null;
    const name = str(c.name);
    out.push({ kind: "connector", handle: `${CONNECTOR_PREFIX}${name}`, label: name,
      detail: [str(c.plugin), used && !used.includes(name) ? "mentioning it connects it here" : str(c.global) ? `global ${str(c.global)}` : ""].filter(Boolean).join(" · ") });
  }
  if (scope.in === "designer") {
    for (const g of org.globals) {
      out.push({ kind: "connector", handle: `${CONNECTOR_PREFIX}${g.name}`, label: g.name, detail: `global connector · ${g.plugin_name} · not in this solution yet` });
    }
  }

  for (const s of skills) {
    if (s.name.trim() && reaches(s.workspace)) out.push({ kind: "skill", handle: `skill:${s.name}`, label: s.name, detail: s.description || "in this solution" });
  }
  for (const k of list(manifest.knowledge).map(obj)) {
    if (str(k.key).trim() && reaches(k.workspace)) out.push({ kind: "knowledge", handle: `knowledge:${knowledgeHandle(str(k.key))}`, label: str(k.key), detail: "in this solution" });
  }
  out.push(...org.skills.map((m) => ({ ...m, detail: m.detail ? `organization · ${m.detail}` : "organization" })), ...org.knowledge);

  const seen = new Set<string>();
  return out.filter((m) => !seen.has(m.handle.toLowerCase()) && seen.add(m.handle.toLowerCase()));
}

/** The connectors a text mentions (`@connector:name`), by name. */
export function connectorsMentioned(text: string): string[] {
  return [...new Set(mentionsIn(text).filter((h) => h.toLowerCase().startsWith(CONNECTOR_PREFIX)).map((h) => h.slice(CONNECTOR_PREFIX.length).toLowerCase()))];
}

const HANDLE = /(?<![\w.@])@([A-Za-z0-9][A-Za-z0-9._:/-]*[A-Za-z0-9]|[A-Za-z0-9])/g;

/** Every `@from` in the strings of a value written as `@to` (Mentions.RenameHandle on the server): a
 * renamed skill, connector, knowledge or stage keeps the mentions of it. */
export function renameMentions<T extends Json>(value: T, from: string, to: string): T {
  if (!from || !to || from.toLowerCase() === to.toLowerCase()) return value;
  const walk = (v: Json): Json => {
    if (typeof v === "string") return v.includes("@") ? v.replace(HANDLE, (m, h: string) => (h.toLowerCase() === from.toLowerCase() ? `@${to}` : m)) : v;
    if (Array.isArray(v)) return v.map(walk);
    if (v && typeof v === "object") return Object.fromEntries(Object.entries(v).map(([k, x]) => [k, walk(x)]));
    return v;
  };
  return walk(value) as T;
}
