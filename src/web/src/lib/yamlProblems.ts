/**
 * Finds where in aktor.yaml a validation problem is, so the builder and the deploy dialog can
 * highlight it. The server reports problems in words ("Workspace 'intake': pipeline: Stage id
 * 'triage_step' isn't valid…"), a YAML line ("Line: 12"), or a JSON path ("at
 * $.workspaces[0].pipeline"); this maps each to a line. It reads the block-style YAML the builder
 * writes and most people type; when it can't place a problem exactly it points at the section.
 */

/** A problem's place in the YAML: the line to highlight (1-based) and the block it sits in. */
export interface YamlLocation {
  line: number;
  from: number;
  to: number;
  /** False when only the section was found, not the exact line. */
  exact: boolean;
}

/** The top-level key each kind of problem lives under, from how the server words it. */
const SECTIONS: [RegExp, string][] = [
  [/^Workspace '/, "workspaces"],
  [/^Input '|isn't an input/, "inputs"],
  [/^Connector '/, "connectors"],
  [/^(Entity|Field) '/, "entities"],
  [/^Skill '/, "skills"],
  [/^Knowledge '|^Every knowledge/, "knowledge"],
  [/^Eval '|^Every eval/, "evals"],
  [/^assistant/, "assistant"],
  [/^end_users/, "end_users"],
  [/^seed/, "seed"],
  [/^webhook/, "webhooks"],
];

const indentOf = (line: string) => line.length - line.trimStart().length;
const blank = (line: string) => line.trim() === "" || line.trimStart().startsWith("#");
const isItem = (line: string) => /^\s*- /.test(line) || /^\s*-$/.test(line);
/** Where a line's key starts: `- key:` puts it two past the dash. */
const keyIndent = (line: string) => indentOf(line) + (isItem(line) ? 2 : 0);
const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

/** The lines [from, to) a line heads: a key and what's nested under it, or a whole list item
 * when the line is one of the item's keys. */
function blockOf(lines: string[], i: number): [number, number] {
  let head = i;
  if (!isItem(lines[i])) {
    const ind = indentOf(lines[i]);
    for (let j = i - 1; j >= 0; j--) {
      if (blank(lines[j])) continue;
      if (indentOf(lines[j]) < ind) {
        if (isItem(lines[j]) && indentOf(lines[j]) + 2 === ind) head = j;
        break;
      }
    }
  }
  const ind = indentOf(lines[head]);
  let end = head + 1;
  while (end < lines.length && (blank(lines[end]) || indentOf(lines[end]) > ind)) end++;
  return [head, end];
}

/** The first line in [from, to), other than skip, where token is a key or a value. */
function findToken(lines: string[], token: string, from: number, to: number, skip: number): number {
  const re = new RegExp(`(^|[\\s:\\[,"'-])${escape(token)}($|[\\s,\\]"':])`);
  for (let i = from; i < to; i++) if (i !== skip && !blank(lines[i]) && re.test(lines[i])) return i;
  return -1;
}

/** The line a key starts in [from, to), as `key:` or `- key:`, at the shallowest indent there. */
function findKey(lines: string[], key: string, from: number, to: number): number {
  const re = new RegExp(`^\\s*(- )?["']?${escape(key)}["']?\\s*:`, "i");
  let best = -1;
  for (let i = from; i < to; i++) {
    if (blank(lines[i]) || !re.test(lines[i])) continue;
    if (best < 0 || keyIndent(lines[i]) < keyIndent(lines[best])) best = i;
  }
  return best;
}

/** Follows a JSON path ($.workspaces[0].pipeline.stages[1].kind) through the YAML. */
function walkPath(lines: string[], path: string): YamlLocation | null {
  const steps = [...path.replace(/^\$/, "").matchAll(/\.([^.[\]]+)|\[(\d+)\]|\['([^']+)'\]/g)].map((m) => m[2] !== undefined ? Number(m[2]) : (m[1] ?? m[3]));
  let [from, to] = [0, lines.length];
  let line = -1;
  for (const step of steps) {
    let next = -1;
    if (typeof step === "number") {
      // The nth item at the shallowest list indent inside the current block.
      const items = [];
      for (let i = line + 1; i < to; i++) if (isItem(lines[i])) items.push(i);
      const shallowest = Math.min(...items.map((i) => indentOf(lines[i])));
      next = items.filter((i) => indentOf(lines[i]) === shallowest)[step] ?? -1;
    } else {
      next = findKey(lines, step, line < 0 ? from : line + (isItem(lines[line]) ? 0 : 1), to);
    }
    if (next < 0) break;
    line = next;
    [from, to] = blockOf(lines, next);
  }
  return line < 0 ? null : { line: line + 1, from: from + 1, to, exact: true };
}

/** Where the problem is in yaml, or null when it isn't in this file at all. */
export function locateYamlProblem(yaml: string, problem: string): YamlLocation | null {
  const lines = yaml.split("\n");
  // A YAML syntax error names its line.
  const syntax = /Line: (\d+)/.exec(problem);
  if (syntax) {
    const line = Math.min(Number(syntax[1]), lines.length);
    return { line, from: line, to: line, exact: true };
  }

  // A value that couldn't be read names its JSON path.
  const path = /\bat (\$[^\s:]*)/.exec(problem);
  if (path) return walkPath(lines, path[1]);

  // Otherwise: narrow to the section, then to each name the problem quotes, in order.
  let [from, to] = [0, lines.length];
  let line = -1;
  const section = SECTIONS.find(([re]) => re.test(problem))?.[1];
  if (section) {
    const top = findKey(lines, section, 0, lines.length);
    if (top < 0 || indentOf(lines[top]) > 0) return null;
    line = top;
    [from, to] = blockOf(lines, top);
  }
  let exact = false;
  // Quoted names, not apostrophes ("isn't 'x'" quotes x).
  const tokens = [...problem.matchAll(/(?<![\w])'([^']+?)'(?![\w])/g)].map((m) => m[1]);
  const find = (token: string) => findToken(lines, token, from, to, line);
  for (const token of tokens) {
    // Field 'Ticket.status' is the field status under the entity Ticket.
    for (const part of find(token) < 0 ? token.split(".") : [token]) {
      const found = find(part);
      if (found < 0) continue;
      line = found;
      exact = true;
      [from, to] = blockOf(lines, found);
    }
  }
  if (line < 0) return null;
  const [blockFrom, blockTo] = blockOf(lines, line);
  return { line: line + 1, from: blockFrom + 1, to: blockTo, exact };
}
