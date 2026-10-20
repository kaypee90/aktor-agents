"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useMemo, useRef, useState } from "react";
import { useAuth } from "@/components/platform/AuthProvider";
import { DeployDialog } from "@/components/solutions/DeployDialog";
import { DesignChat } from "@/components/solutions/DesignChat";
import { connectorsMentioned, renameMentions, solutionMentionables, type MentionScope } from "@/components/solutions/solutionMentions";
import { MentionTextarea, knowledgeHandle, useKnowledgeMentionables, useSkillMentionables, type Mentionable } from "@/components/ui/MentionTextarea";
import {
  apiErrorMessage,
  bundleFromFolder,
  getRegistryPackage,
  getSolutionPackage,
  listGlobalConnectors,
  listPlugins,
  listSolutions,
  parseSolution,
  validateSolution,
  type GlobalConnector,
  type SolutionBundle,
  type SolutionSummary,
  type SolutionValidation,
} from "@/lib/api";
import { CAPABILITIES } from "@/lib/pipelineTypes";
import { atLeast, type Role } from "@/lib/platformTypes";
import { cleanManifest, download, parseSkillMarkdown, skillMarkdown, toYaml, zipPackage, type BuilderSkill, type Json } from "@/lib/solutionPackage";
import type { PluginInfo } from "@/lib/workspaceTypes";
import { Badge, Button, Card, CardHeader, ErrorBanner, Field, PageHeader, Toggle, cx, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

/**
 * The solution builder (docs/solutions.md#the-builder): everything a package holds, as forms, so a
 * solution can be made, checked, deployed and downloaded without the CLI or YAML. The forms edit
 * the same manifest the CLI reads; the YAML tab shows it, and accepts edits back.
 */
export default function BuilderPage() {
  return <Suspense><Builder /></Suspense>;
}

// ---------------------------------------------------------------- the manifest, loosely typed

type Obj = { [key: string]: Json };
type Path = (string | number)[];

const obj = (v: Json | undefined): Obj => (v && typeof v === "object" && !Array.isArray(v) ? v : {});
const list = (v: Json | undefined): Json[] => (Array.isArray(v) ? v : []);
const str = (v: Json | undefined): string => (typeof v === "string" ? v : v === null || v === undefined ? "" : String(v));
const strs = (v: Json | undefined): string[] => list(v).map(str);
const num = (v: Json | undefined): number | "" => (typeof v === "number" ? v : "");

function setIn(root: Obj, path: Path, value: Json | undefined): Obj {
  const copy = structuredClone(root) as Obj;
  let node: Json = copy;
  for (let i = 0; i < path.length - 1; i++) {
    const k = path[i];
    const next = path[i + 1];
    const container = node as Obj & Json[];
    if (container[k as keyof typeof container] === undefined || container[k as keyof typeof container] === null) {
      (container as unknown as Record<string | number, Json>)[k] = typeof next === "number" ? [] : {};
    }
    node = (container as unknown as Record<string | number, Json>)[k];
  }
  const last = path[path.length - 1];
  if (value === undefined) {
    if (Array.isArray(node) && typeof last === "number") node.splice(last, 1);
    else delete (node as Obj)[last as string];
  } else {
    (node as unknown as Record<string | number, Json>)[last] = value;
  }
  return copy;
}

const slug = (s: string, max = 40) => s.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, max);
// For a box being typed into: keeps a trailing dash, or "code-" could never become "code-quality".
const typedSlug = (s: string, max = 40) => s.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+/, "").slice(0, max);
const pascal = (s: string) => s.replace(/[^A-Za-z0-9]+/g, " ").trim().split(/\s+/).map((w) => w[0]?.toUpperCase() + w.slice(1)).join("").slice(0, 40);
const snake = (s: string) => s.toLowerCase().replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "").replace(/^([0-9])/, "f_$1").slice(0, 40);
const toolSlug = (entity: string) => entity.replace(/([a-z0-9])([A-Z])/g, "$1_$2").toLowerCase();

const FIELD_TYPES = ["string", "text", "integer", "number", "boolean", "date", "datetime", "enum", "ref", "end_user", "json"];
const OPS = ["==", "!=", "<", "<=", ">", ">=", "contains", "exists"];

/** What "New solution" starts from: small, complete, and valid as it is. */
const STARTER: Obj = {
  format: 1,
  name: "my-app",
  title: "My app",
  version: "0.1.0",
  description: "What this solution does, in a sentence or two.",
  inputs: [{ key: "company_name", label: "Company name", default: "Example Co" }],
  entities: {
    Request: {
      description: "Something a customer asked for",
      title_field: "subject",
      fields: {
        subject: { type: "string", required: true },
        details: { type: "text" },
        requester: { type: "end_user" },
        priority: { type: "enum", values: ["low", "normal", "urgent"], default: "normal" },
        status: { type: "enum", values: ["new", "handled"], default: "new" },
        notes: { type: "text" },
      },
    },
  },
  workspaces: [
    {
      key: "requests",
      name: "Requests",
      goal: "Handle every new request: set its priority, note what to do, and mark it handled.",
      data: { Request: "write" },
      daily_cost_usd: 2,
      pipeline: {
        stages: [
          {
            stage_id: "handle",
            name: "Handle",
            role: "Request handler",
            instructions: "A request was created; its record is the run's input. Decide its priority, then call data__update_request with priority, notes and status \"handled\". Summarise what you decided.",
            capabilities: ["research"],
          },
        ],
      },
      triggers: [{ kind: "Record", name: "New request", entity: "Request", on: ["created"], instruction: "Handle this request." }],
    },
  ],
  assistant: {
    name: "Assistant",
    greeting: "Hi! How can I help?",
    instructions: "You help customers of {{ inputs.company_name }}.\n\n- When they ask for something, log it with data__create_request and tell them it's logged.\n- When they ask about a request, find it with data__list_request and tell them its status.",
    data: { Request: "write" },
  },
};

type Section = "basics" | "data" | "workspaces" | "assistant" | "setup" | "knowhow" | "evals" | "review";
const SECTIONS: { id: Section; label: string; icon: (p: { className?: string }) => React.ReactNode; hint: string }[] = [
  { id: "basics", label: "Basics", icon: Icons.Solutions, hint: "Name, version, description" },
  { id: "data", label: "Data model", icon: Icons.Knowledge, hint: "Entities and their fields" },
  { id: "workspaces", label: "Workspaces", icon: Icons.Workspaces, hint: "Pipelines and triggers" },
  { id: "assistant", label: "Assistant", icon: Icons.Chat, hint: "What your customers talk to" },
  { id: "setup", label: "Inputs & connectors", icon: Icons.Connect, hint: "Values and integrations" },
  { id: "knowhow", label: "Knowledge & skills", icon: Icons.Book, hint: "What agents know" },
  { id: "evals", label: "Evals", icon: Icons.Check, hint: "Expected behaviour" },
  { id: "review", label: "Review & deploy", icon: Icons.Upload, hint: "YAML, validate, deploy, download" },
];

/** Which section a validation problem belongs to, from how the server words it. */
function sectionOf(error: string): Section {
  if (/^(Entity|Field) /.test(error) || error.startsWith("seed")) return "data";
  if (error.startsWith("Workspace")) return "workspaces";
  if (error.startsWith("assistant") || error.startsWith("end_users")) return "assistant";
  if (/^(Input|Connector|webhook)/.test(error)) return "setup";
  if (/^(Skill|Knowledge|Every knowledge)/.test(error)) return "knowhow";
  if (/^(Eval|Every eval)/.test(error)) return "evals";
  return "basics";
}

// ---------------------------------------------------------------- the builder

function Builder() {
  const params = useSearchParams();
  const router = useRouter();
  const { me } = useAuth();
  const role = (me?.role ?? "Viewer") as Role;
  const from = params.get("from");
  const packageRef = params.get("package");
  const draftKey = `aktor:solution-builder:${from ?? packageRef ?? "new"}`;

  const [manifest, setManifest] = useState<Obj | null>(null);
  const [skills, setSkills] = useState<BuilderSkill[]>([]);
  const [section, setSection] = useState<Section>("basics");
  const [validation, setValidation] = useState<SolutionValidation | null>(null);
  const [checking, setChecking] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [installs, setInstalls] = useState<SolutionSummary[]>([]);
  const [plugins, setPlugins] = useState<PluginInfo[]>([]);
  const [globals, setGlobals] = useState<GlobalConnector[]>([]);
  const [deploying, setDeploying] = useState<SolutionBundle | null>(null);
  const folderInput = useRef<HTMLInputElement>(null);
  const orgSkills = useSkillMentionables();
  const orgKnowledge = useKnowledgeMentionables();

  // A package (from an install, the registry or a folder) as the builder's state: the server reads
  // the YAML, the builder keeps the structure, and skills and knowledge become editable text.
  async function open(bundle: SolutionBundle, label: string) {
    const parsed = await parseSolution(bundle);
    if (!parsed.manifest) throw new Error(parsed.errors.join(" ") || "That package couldn't be read.");
    const m = parsed.manifest as Obj;
    const files = parsed.files;
    const loadedSkills: BuilderSkill[] = list(m.skills).map((s) => {
      const skill = obj(s);
      return { ...parseSkillMarkdown(files[str(skill.path)] ?? ""), workspace: str(skill.workspace) || null };
    });
    const knowledge = list(m.knowledge).map((k) => {
      const entry = obj(k);
      return { key: str(entry.key), text: entry.path ? files[str(entry.path)] ?? "" : str(entry.text), workspace: entry.workspace ?? null } as Json;
    });
    setManifest({ ...m, knowledge, skills: [] });
    setSkills(loadedSkills);
    setNotice(`Opened ${label}. Changes are saved as a draft in this browser until you deploy or download.`);
  }

  // First load: a saved draft, else what the URL names, else the starter.
  useEffect(() => {
    let draft: { manifest: Obj; skills: BuilderSkill[] } | null = null;
    try {
      const raw = localStorage.getItem(draftKey);
      if (raw) draft = JSON.parse(raw);
    } catch { /* storage blocked: start fresh */ }
    const start = async () => {
      if (draft?.manifest) {
        setManifest(draft.manifest);
        setSkills(draft.skills ?? []);
        setNotice("Restored your unsaved draft. Discard it to start again from the source.");
      } else if (from) {
        await open(await getSolutionPackage(from), "the installed version");
      } else if (packageRef) {
        const [name, version] = packageRef.split("@");
        await open((await getRegistryPackage(name, version ?? "latest")).bundle, packageRef);
      } else {
        setManifest(structuredClone(STARTER));
        setSkills([{ name: "handling-requests", description: "How to prioritise a customer request.", instructions: "- urgent: they can't use the service at all.\n- normal: something doesn't work, but there's a way around it.\n- low: a question or a suggestion.", workspace: null }]);
      }
    };
    start().catch((e) => setError(apiErrorMessage(e)));
    listSolutions().then(setInstalls).catch(() => {});
    listPlugins().then(setPlugins).catch(() => {});
    listGlobalConnectors().then(setGlobals).catch(() => {});
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draftKey]);

  // Autosave the draft (this browser only).
  useEffect(() => {
    if (!manifest) return;
    try { localStorage.setItem(draftKey, JSON.stringify({ manifest, skills })); } catch { /* best effort */ }
  }, [manifest, skills, draftKey]);

  /** The package as files: aktor.yaml written from the forms, plus each skill's SKILL.md. */
  const bundle = useMemo<SolutionBundle | null>(() => {
    if (!manifest) return null;
    const files: Record<string, string> = {};
    const skillRefs: Json[] = skills.filter((s) => s.name.trim()).map((s) => {
      const path = `skills/${slug(s.name) || "skill"}/SKILL.md`;
      files[path] = skillMarkdown(s);
      return { path, workspace: s.workspace || null };
    });
    const cleaned = cleanManifest({ ...manifest, skills: skillRefs });
    const yaml = toYaml(cleaned, `${str(manifest.title) || str(manifest.name)}: built in the Aktor dashboard.\nDeploy with: aktor deploy   (docs/solutions.md)`);
    return { manifest: yaml, files };
  }, [manifest, skills]);

  // Check against the server as you go (debounced): the same validation the CLI and deploy use.
  useEffect(() => {
    if (!bundle) return;
    const t = setTimeout(() => {
      setChecking(true);
      validateSolution(bundle).then(setValidation).catch((e) => setError(apiErrorMessage(e))).finally(() => setChecking(false));
    }, 700);
    return () => clearTimeout(t);
  }, [bundle]);

  if (!manifest || !bundle) {
    return <div className="p-6"><ErrorBanner error={error} />{!error && <div className="text-sm text-zinc-500">Loading…</div>}</div>;
  }

  const set = (path: Path, value: Json | undefined) => setManifest((m) => (m ? setIn(m, path, value) : m));
  const entities = Object.keys(obj(manifest.entities));
  const workspaces = list(manifest.workspaces).map(obj);
  const installed = installs.find((s) => s.name === str(manifest.name) && s.status === "Active");
  const problems = validation?.errors ?? [];
  const bySection = (s: Section) => problems.filter((e) => sectionOf(e) === s);
  const canDeploy = atLeast(role, "Admin");
  const mentionsFor = (scope: MentionScope) => solutionMentionables(manifest, skills, scope, { skills: orgSkills, knowledge: orgKnowledge, globals });
  /** A renamed skill, connector or knowledge: the mentions of it follow, in the manifest and in skills. */
  const renameEverywhere = (from: string, to: string, change?: (m: Obj) => Obj) => {
    setManifest((m) => (m ? renameMentions(change ? change(m) : m, from, to) : m));
    setSkills((list) => list.map((s) => ({ ...s, instructions: renameMentions(s.instructions, from, to) })));
  };

  function discardDraft() {
    if (!window.confirm("Discard your changes and start again from the source?")) return;
    try { localStorage.removeItem(draftKey); localStorage.removeItem(`${draftKey}:chat`); } catch { /* ignore */ }
    window.location.reload();
  }

  async function openFolder(files: FileList | null) {
    if (!files?.length) return;
    try {
      await open(await bundleFromFolder(files), "the folder");
      setSection("basics");
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  return (
    <div>
      <PageHeader
        title={<span className="flex items-center gap-2">{str(manifest.title) || "New solution"} <Badge>{str(manifest.version) || "0.0.0"}</Badge></span>}
        description="Build a solution with forms: its data, workspaces, the assistant your customers talk to, and its evals. It's checked against this server as you go; deploy it from here, or download the package to keep it in git or use the CLI."
        actions={
          <>
            <input ref={folderInput} type="file" multiple className="hidden" onChange={(e) => openFolder(e.target.files)}
              {...{ webkitdirectory: "" }} />
            <Button onClick={() => folderInput.current?.click()} icon={<Icons.Upload className="h-3.5 w-3.5" />}>Open a folder</Button>
            <Button onClick={discardDraft}>Discard draft</Button>
            <Button variant="primary" disabled={!canDeploy || !validation?.ok} title={canDeploy ? undefined : "Admins deploy solutions"}
              onClick={() => setDeploying(bundle)}>
              {!installed ? "Deploy" : installed.version === str(manifest.version) ? `Redeploy ${installed.version}` : `Upgrade ${installed.version} → ${str(manifest.version)}`}
            </Button>
          </>
        }
      >
        <div className="mt-2 text-xs text-zinc-500">
          <Link href="/solutions" className="hover:text-zinc-800 dark:hover:text-zinc-200">Solutions</Link> / builder
        </div>
      </PageHeader>

      <div className="mx-auto grid max-w-7xl gap-6 px-6 pb-24 pt-6 lg:grid-cols-[14rem_minmax(0,1fr)]">
        <nav className="space-y-1 lg:sticky lg:top-4 lg:self-start" aria-label="Builder sections">
          {SECTIONS.map((s) => {
            const count = s.id === "review" ? problems.length : bySection(s.id).length;
            return (
              <button key={s.id} onClick={() => setSection(s.id)}
                className={cx("flex w-full items-start gap-2.5 rounded-lg px-3 py-2 text-left transition-colors",
                  section === s.id ? "bg-brand-50 text-brand-700 dark:bg-brand-500/10 dark:text-brand-300" : "text-zinc-600 hover:bg-zinc-100 dark:text-zinc-400 dark:hover:bg-zinc-900")}>
                <s.icon className="mt-0.5 h-4 w-4 shrink-0" />
                <span className="min-w-0 flex-1">
                  <span className="block text-sm font-medium">{s.label}</span>
                  <span className="block truncate text-[11px] opacity-70">{s.hint}</span>
                </span>
                {count > 0 && <span className="mt-0.5 rounded-full bg-rose-100 px-1.5 text-[10px] font-semibold text-rose-700 dark:bg-rose-950 dark:text-rose-300">{count}</span>}
              </button>
            );
          })}
          <div className="mt-4 rounded-lg border border-zinc-200 px-3 py-2 text-xs dark:border-zinc-800">
            {checking ? <span className="text-zinc-500">Checking…</span>
              : validation?.ok ? <span className="text-emerald-600 dark:text-emerald-400">✓ Valid: ready to deploy</span>
              : <span className="text-rose-600 dark:text-rose-400">{problems.length} problem{problems.length === 1 ? "" : "s"} to fix</span>}
          </div>
        </nav>

        <main className="min-w-0 space-y-4">
          <ErrorBanner error={error} onClose={() => setError(null)} />
          {notice && (
            <div className="flex items-start justify-between gap-3 rounded-lg border border-sky-200 bg-sky-50 px-3 py-2 text-sm text-sky-800 dark:border-sky-900/60 dark:bg-sky-950/40 dark:text-sky-300">
              <span>{notice}</span>
              <button onClick={() => setNotice(null)} className="opacity-70 hover:opacity-100" aria-label="Dismiss"><Icons.X /></button>
            </div>
          )}
          <Problems errors={section === "review" ? [] : bySection(section)} />

          {section === "basics" && <Basics manifest={manifest} set={set} installed={installed} />}
          {section === "data" && <DataModel manifest={manifest} set={set} setManifest={setManifest} />}
          {section === "workspaces" && <Workspaces manifest={manifest} set={set} entities={entities} mentionsFor={mentionsFor} />}
          {section === "assistant" && <Assistant manifest={manifest} set={set} entities={entities} mentionsFor={mentionsFor} />}
          {section === "setup" && <Setup manifest={manifest} set={set} plugins={plugins} globals={globals} renameEverywhere={renameEverywhere} />}
          {section === "knowhow" && <Knowhow manifest={manifest} set={set} skills={skills} setSkills={setSkills} workspaces={workspaces} mentionsFor={mentionsFor} renameEverywhere={renameEverywhere} />}
          {section === "evals" && <Evals manifest={manifest} set={set} entities={entities} workspaces={workspaces} mentionsFor={mentionsFor} />}
          {section === "review" && (
            <Review bundle={bundle} validation={validation} manifestName={str(manifest.name) || "solution"}
              onApply={async (yaml) => {
                try {
                  await open({ manifest: yaml, files: bundle.files }, "your YAML");
                } catch (e) {
                  setError(apiErrorMessage(e));
                }
              }} />
          )}
        </main>
      </div>

      <DesignChat draft={{ manifest, skills }} storageKey={`${draftKey}:chat`} mentionables={mentionsFor({ in: "designer" })}
        onApply={(next) => {
          setManifest(next.manifest);
          setSkills(next.skills);
          setNotice("Applied the designer's changes. Every section is checked again; undo it from the conversation if it's not what you wanted.");
        }} />

      {deploying && (
        <DeployDialog
          initialBundle={deploying}
          installId={installed?.install_id}
          onClose={() => setDeploying(null)}
          onDone={(installId) => {
            setDeploying(null);
            try { localStorage.removeItem(draftKey); } catch { /* ignore */ }
            router.push(`/solutions?id=${encodeURIComponent(installId)}`);
          }}
        />
      )}
    </div>
  );
}

// ---------------------------------------------------------------- shared bits

type SetFn = (path: Path, value: Json | undefined) => void;
type MentionsFor = (scope: MentionScope) => Mentionable[];
type RenameFn = (from: string, to: string, change?: (m: Obj) => Obj) => void;

/** A text box where @ offers what its agents can reach (docs/solutions.md#mentions). */
function MentionArea({ value, onChange, mentionables, placeholder, rows = 4, singleLine, className }: {
  value: string; onChange: (v: string) => void; mentionables: Mentionable[]; placeholder?: string; rows?: number; singleLine?: boolean; className?: string;
}) {
  return <MentionTextarea className={cx(inputClass, className)} rows={rows} value={value} onValueChange={onChange} mentionables={mentionables} placeholder={placeholder} singleLine={singleLine} />;
}

/** The connectors a text mentions that are declared but not yet in <c>used</c>: mentioning one connects it. */
const newlyMentioned = (text: string, declared: string[], used: string[]) => connectorsMentioned(text).filter((c) => declared.includes(c) && !used.includes(c));

function Problems({ errors }: { errors: string[] }) {
  if (errors.length === 0) return null;
  return (
    <div className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-sm text-rose-700 dark:border-rose-900/60 dark:bg-rose-950/40 dark:text-rose-300">
      <div className="mb-1 font-medium">To fix here</div>
      <ul className="list-disc space-y-0.5 pl-5 text-xs">{errors.map((e) => <li key={e}>{e}</li>)}</ul>
    </div>
  );
}

function TextInput({ value, onChange, placeholder, className, mono }: { value: string; onChange: (v: string) => void; placeholder?: string; className?: string; mono?: boolean }) {
  return <input className={cx(inputClass, mono && "font-mono text-xs", className)} value={value} placeholder={placeholder} onChange={(e) => onChange(e.target.value)} />;
}

function TextArea({ value, onChange, placeholder, rows = 4, mono }: { value: string; onChange: (v: string) => void; placeholder?: string; rows?: number; mono?: boolean }) {
  return <textarea className={cx(inputClass, mono && "font-mono text-xs")} rows={rows} value={value} placeholder={placeholder} onChange={(e) => onChange(e.target.value)} />;
}

function NumberInput({ value, onChange, placeholder, step }: { value: number | ""; onChange: (v: number | undefined) => void; placeholder?: string; step?: string }) {
  return <input type="number" step={step ?? "any"} className={inputClass} value={value} placeholder={placeholder}
    onChange={(e) => onChange(e.target.value === "" ? undefined : Number(e.target.value))} />;
}

/** None / read / write for each entity: what a workspace's (or the assistant's) agents may do with it. */
function AccessPicker({ entities, value, onChange }: { entities: string[]; value: Obj; onChange: (v: Obj) => void }) {
  if (entities.length === 0) return <p className="text-xs text-zinc-500">Add entities under Data model first.</p>;
  return (
    <div className="space-y-1.5">
      {entities.map((e) => {
        const current = str(value[e]) || "none";
        return (
          <div key={e} className="flex items-center justify-between gap-3 text-sm">
            <span className="font-medium text-zinc-800 dark:text-zinc-200">{e}</span>
            <div className="inline-flex overflow-hidden rounded-lg border border-zinc-200 text-xs dark:border-zinc-700">
              {["none", "read", "write"].map((a) => (
                <button key={a} type="button"
                  onClick={() => { const next = { ...value }; if (a === "none") delete next[e]; else next[e] = a; onChange(next); }}
                  className={cx("px-2.5 py-1", current === a ? "bg-brand-500 text-white" : "text-zinc-600 hover:bg-zinc-100 dark:text-zinc-400 dark:hover:bg-zinc-800")}>
                  {a}
                </button>
              ))}
            </div>
          </div>
        );
      })}
    </div>
  );
}

function RemoveButton({ onClick, label }: { onClick: () => void; label: string }) {
  return (
    <button type="button" onClick={onClick} title={label} aria-label={label}
      className="rounded-md p-1.5 text-zinc-400 hover:bg-rose-50 hover:text-rose-600 dark:hover:bg-rose-950/40">
      <Icons.Trash className="h-4 w-4" />
    </button>
  );
}

// ---------------------------------------------------------------- sections

function Basics({ manifest, set, installed }: { manifest: Obj; set: SetFn; installed?: SolutionSummary }) {
  const title = str(manifest.title);
  return (
    <Card>
      <CardHeader title="Basics" description="How the solution is named and versioned. The name is its id in your organization; a new version is what an upgrade installs." />
      <div className="grid gap-4 p-5 md:grid-cols-2">
        <Field label="Title" hint="What people see.">
          <TextInput value={title} onChange={(v) => {
            set(["title"], v);
            // Until someone sets a name of their own, it follows the title.
            if (!installed && (str(manifest.name) === "" || str(manifest.name) === slug(title, 50) || str(manifest.name) === "my-app")) set(["name"], slug(v, 50) || "my-app");
          }} placeholder="Property management" />
        </Field>
        <Field label="Name" hint={installed ? `Installed as ${installed.name} ${installed.version}: deploying upgrades it.` : "Lowercase letters, digits and dashes."}>
          <TextInput mono value={str(manifest.name)} onChange={(v) => set(["name"], typedSlug(v, 50))} placeholder="property-mgmt" />
        </Field>
        <Field label="Version" hint={installed ? `Must be higher than ${installed.version} to upgrade.` : "Semantic version, e.g. 1.0.0."}>
          <div className="flex gap-2">
            <TextInput mono value={str(manifest.version)} onChange={(v) => set(["version"], v.trim())} placeholder="1.0.0" />
            {["patch", "minor"].map((part) => (
              <Button key={part} type="button" size="sm" className="h-9 shrink-0" onClick={() => {
                const [a, b, c] = str(manifest.version).split("-")[0].split(".").map((x) => Number(x) || 0);
                set(["version"], part === "patch" ? `${a}.${b}.${c + 1}` : `${a}.${b + 1}.0`);
              }}>+{part}</Button>
            ))}
          </div>
        </Field>
        <Field label="Requires platform (optional)" hint='e.g. ">=1.2.0"; an older server then refuses it with the reason.'>
          <TextInput mono value={str(manifest.requires_platform)} onChange={(v) => set(["requires_platform"], v || undefined)} placeholder=">=1.2.0" />
        </Field>
        <Field label="Description" className="md:col-span-2">
          <TextArea value={str(manifest.description)} onChange={(v) => set(["description"], v)} rows={3} />
        </Field>
      </div>
    </Card>
  );
}

function DataModel({ manifest, set, setManifest }: { manifest: Obj; set: SetFn; setManifest: React.Dispatch<React.SetStateAction<Obj | null>> }) {
  const entities = obj(manifest.entities);
  const names = Object.keys(entities);

  /** Renames an entity everywhere it's named: refs, data access, triggers, evals, seed. */
  function rename(from: string, to: string) {
    if (!to || to === from || names.includes(to)) return;
    setManifest((m) => {
      if (!m) return m;
      const text = JSON.stringify(m);
      const next = JSON.parse(text) as Obj;
      const ents: Obj = {};
      for (const [k, v] of Object.entries(obj(next.entities))) ents[k === from ? to : k] = v;
      next.entities = ents;
      for (const e of Object.values(ents)) for (const f of Object.values(obj(obj(e).fields))) if (obj(f).entity === from) obj(f).entity = to;
      const renameKey = (o: Obj) => { if (from in o) { o[to] = o[from]; delete o[from]; } };
      for (const w of list(next.workspaces)) {
        renameKey(obj(obj(w).data));
        for (const t of list(obj(w).triggers)) if (obj(t).entity === from) obj(t).entity = to;
      }
      renameKey(obj(obj(next.assistant).data));
      renameKey(obj(next.seed));
      for (const e of list(next.evals)) renameKey(obj(obj(obj(e).expect).records));
      return next;
    });
  }

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader title="Data model" description="The records your agents and your customers' assistant work on. Every write is checked against these fields, whoever writes it."
          actions={<Button size="sm" variant="primary" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => {
            let n = "NewEntity", i = 2;
            while (names.includes(n)) n = `NewEntity${i++}`;
            set(["entities", n], { fields: { name: { type: "string", required: true } } });
          }}>Add entity</Button>} />
        {names.length === 0 && <p className="p-5 text-sm text-zinc-500">No entities yet. A solution doesn&apos;t need data, but most do.</p>}
      </Card>
      {names.map((name) => <EntityEditor key={name} name={name} entity={obj(entities[name])} names={names} set={set} rename={rename}
        renameField={(from, to) => setManifest((m) => (m ? renameField(m, name, from, to) : m))}
        removeField={(field) => setManifest((m) => (m ? removeField(m, name, field) : m))} />)}
    </div>
  );
}

/** Record trigger conditions on an entity's fields, in every workspace (to keep them in step with a field rename). */
function triggerConditions(m: Obj, entity: string): Obj[] {
  return list(m.workspaces).flatMap((w) => list(obj(w).triggers).map(obj))
    .filter((t) => str(t.entity) === entity)
    .flatMap((t) => list(obj(t.rule).conditions).map(obj));
}

/** Renames a field everywhere it's named: the field list (keeping its place), the title field,
 * seed records, and record triggers' conditions. */
function renameField(manifest: Obj, entity: string, from: string, to: string): Obj {
  const m = structuredClone(manifest) as Obj;
  const e = obj(obj(m.entities)[entity]);
  const fields: Obj = {};
  for (const [k, v] of Object.entries(obj(e.fields))) fields[k === from ? to : k] = v;
  e.fields = fields;
  if (str(e.title_field) === from) e.title_field = to;
  for (const record of list(obj(m.seed)[entity]).map(obj)) {
    if (from in record) { record[to] = record[from]; delete record[from]; }
  }
  for (const c of triggerConditions(m, entity)) if (str(c.field) === from) c.field = to;
  return m;
}

/** Removes a field, and the title field if it was that one (the first string field is used then). */
function removeField(manifest: Obj, entity: string, field: string): Obj {
  const m = structuredClone(manifest) as Obj;
  const e = obj(obj(m.entities)[entity]);
  const fields = obj(e.fields);
  delete fields[field];
  if (str(e.title_field) === field) delete e.title_field;
  return m;
}

/** A field after its type changes: settings that only fit the old type (enum values, a ref's
 * entity, a default of the old type, an index on text/json) are dropped. */
function retype(field: Obj, type: string): Obj {
  const next: Obj = { ...field, type };
  if (type !== "enum") delete next.values;
  if (type !== "ref") delete next.entity;
  if (type === "text" || type === "json") delete next.indexed;
  if (type === "end_user") delete next.required;
  delete next.default;
  return next;
}

/** An enum's allowed values, typed as "a, b, c". Kept as text while typing (so commas and spaces
 * can be typed) and written to the field as a list; a default no longer allowed is cleared. */
function EnumValues({ field, onChange }: { field: Obj; onChange: (next: Obj) => void }) {
  const values = strs(field.values);
  const parse = (raw: string) => [...new Set(raw.split(",").map((x) => x.trim()).filter(Boolean))];
  const [text, setText] = useState(values.join(", "));
  // Values changed elsewhere (the designer, undo): show them, unless they're what's being typed.
  const joined = values.join("\n");
  const [seen, setSeen] = useState(joined);
  if (seen !== joined) {
    setSeen(joined);
    if (parse(text).join("\n") !== joined) setText(values.join(", "));
  }
  const invalidDefault = field.default !== undefined && field.default !== null && !values.some((v) => v.toLowerCase() === str(field.default).toLowerCase());
  const commit = (raw: string) => {
    const parsed = parse(raw);
    const next: Obj = { ...field, values: parsed };
    if (field.default !== undefined && !parsed.some((v) => v.toLowerCase() === str(field.default).toLowerCase())) delete next.default;
    onChange(next);
  };
  return (
    <div className="space-y-1.5">
      <input className={cx(inputClass, "text-xs")} value={text} placeholder="low, normal, urgent" aria-label="Allowed values"
        onChange={(e) => { setText(e.target.value); commit(e.target.value); }}
        onBlur={() => setText(strs(field.values).join(", "))} />
      <select className={cx(inputClass, "text-xs")} value={str(field.default)} aria-label="Default value"
        onChange={(e) => {
          const next: Obj = { ...field };
          if (e.target.value) next.default = e.target.value;
          else delete next.default;
          onChange(next);
        }}>
        <option value="">No default</option>
        {values.map((v) => <option key={v} value={v}>Default: {v}</option>)}
        {invalidDefault && <option value={str(field.default)}>Default: {str(field.default)} (not allowed: pick another)</option>}
      </select>
    </div>
  );
}

function EntityEditor({ name, entity, names, set, rename, renameField, removeField }: {
  name: string; entity: Obj; names: string[]; set: SetFn; rename: (from: string, to: string) => void;
  renameField: (from: string, to: string) => void; removeField: (field: string) => void;
}) {
  const [draftName, setDraftName] = useState(name);
  const fields = obj(entity.fields);
  const base = ["entities", name];
  return (
    <Card>
      <div className="flex flex-wrap items-center gap-3 border-b border-zinc-200 px-5 py-3 dark:border-zinc-800">
        <input className={cx(inputClass, "w-56 font-semibold")} value={draftName} aria-label="Entity name"
          onChange={(e) => setDraftName(pascal(e.target.value))} onBlur={() => (draftName && draftName !== name ? rename(name, draftName) : setDraftName(name))} />
        <span className="text-[11px] text-zinc-500">agents get data__list_{toolSlug(name)}, get_, create_, update_, delete_</span>
        <div className="ml-auto"><RemoveButton label={`Remove ${name}`} onClick={() => window.confirm(`Remove ${name}?`) && set(base, undefined)} /></div>
      </div>
      <div className="space-y-3 p-5">
        <div className="grid gap-3 md:grid-cols-[1fr_14rem]">
          <Field label="Description">
            <TextInput value={str(entity.description)} onChange={(v) => set([...base, "description"], v || undefined)} placeholder="A maintenance request" />
          </Field>
          <Field label="Title field" hint="What a record is called in lists.">
            <select className={inputClass} value={str(entity.title_field)} onChange={(e) => set([...base, "title_field"], e.target.value || undefined)}>
              <option value="">First text field</option>
              {Object.keys(fields).map((k) => <option key={k} value={k}>{k}</option>)}
              {str(entity.title_field) && !(str(entity.title_field) in fields) && (
                <option value={str(entity.title_field)}>{str(entity.title_field)} (no such field: pick another)</option>
              )}
            </select>
          </Field>
        </div>
        <div className="overflow-x-auto">
          <table className="w-full text-left text-xs">
            <thead className="text-[11px] uppercase tracking-wide text-zinc-500">
              <tr><th className="py-1.5 pr-2">Field</th><th className="pr-2">Type</th><th className="pr-2">Details</th><th className="pr-2" title="Required">Req.</th><th className="pr-2" title="Database index for filtering and sorting">Index</th><th /></tr>
            </thead>
            <tbody>
              {Object.entries(fields).map(([fname, fv]) => {
                const f = obj(fv);
                const type = str(f.type) || "string";
                const fpath = [...base, "fields", fname];
                return (
                  <tr key={fname} className="align-top">
                    <td className="py-1 pr-2">
                      <input className={cx(inputClass, "w-40 font-mono text-xs")} defaultValue={fname} aria-label="Field name"
                        onBlur={(e) => {
                          const to = snake(e.target.value);
                          if (!to || to === fname || to in fields) { e.target.value = fname; return; }
                          renameField(fname, to);
                        }} />
                    </td>
                    <td className="pr-2">
                      <select className={cx(inputClass, "w-32 text-xs")} value={type} onChange={(e) => set(fpath, retype(f, e.target.value))}>
                        {FIELD_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
                      </select>
                    </td>
                    <td className="pr-2">
                      {type === "enum" ? (
                        <EnumValues field={f} onChange={(next) => set(fpath, next)} />
                      ) : type === "ref" ? (
                        <select className={cx(inputClass, "text-xs")} value={str(f.entity)} onChange={(e) => set([...fpath, "entity"], e.target.value)}>
                          <option value="">Which entity?</option>
                          {names.map((n) => <option key={n} value={n}>{n}</option>)}
                        </select>
                      ) : type === "end_user" ? (
                        <span className="block py-2 text-zinc-500">Set by the runtime: whose record it is.</span>
                      ) : (
                        <TextInput className="text-xs" value={str(f.description)} placeholder="Description (optional)"
                          onChange={(v) => set([...fpath, "description"], v || undefined)} />
                      )}
                    </td>
                    <td className="pr-2 pt-2.5"><input type="checkbox" checked={f.required === true} disabled={type === "end_user"} onChange={(e) => set([...fpath, "required"], e.target.checked)} aria-label="Required" /></td>
                    <td className="pr-2 pt-2.5"><input type="checkbox" checked={f.indexed === true} disabled={type === "text" || type === "json"} onChange={(e) => set([...fpath, "indexed"], e.target.checked)} aria-label="Indexed" /></td>
                    <td><RemoveButton label={`Remove ${fname}`} onClick={() => removeField(fname)} /></td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        <Button size="sm" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => {
          let n = "new_field", i = 2;
          while (n in fields) n = `new_field_${i++}`;
          set([...base, "fields", n], { type: "string" });
        }}>Add field</Button>
      </div>
    </Card>
  );
}

function Workspaces({ manifest, set, entities, mentionsFor }: { manifest: Obj; set: SetFn; entities: string[]; mentionsFor: MentionsFor }) {
  const workspaces = list(manifest.workspaces).map(obj);
  const connectors = list(manifest.connectors).map((c) => str(obj(c).name)).filter(Boolean);
  return (
    <div className="space-y-4">
      <Card>
        <CardHeader title="Workspaces" description="Standing work: each runs its pipeline when a trigger fires, with its own data access, budget and safety policy."
          actions={<Button size="sm" variant="primary" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => set(["workspaces", workspaces.length], {
            key: `workspace-${workspaces.length + 1}`, name: `Workspace ${workspaces.length + 1}`, goal: "",
            pipeline: { stages: [{ stage_id: "work", name: "Work", role: "", instructions: "", capabilities: ["research"] }] }, triggers: [],
          })}>Add workspace</Button>} />
        {workspaces.length === 0 && <p className="p-5 text-sm text-zinc-500">No workspaces: the solution is only an assistant. That&apos;s fine too.</p>}
      </Card>
      {workspaces.map((w, i) => <WorkspaceEditor key={i} index={i} ws={w} set={set} entities={entities} connectors={connectors} manifest={manifest} mentionables={mentionsFor({ in: "workspace", key: str(w.key) })} />)}
    </div>
  );
}

function WorkspaceEditor({ index, ws, set, entities, connectors, manifest, mentionables }: {
  index: number; ws: Obj; set: SetFn; entities: string[]; connectors: string[]; manifest: Obj; mentionables: Mentionable[];
}) {
  const base: Path = ["workspaces", index];
  const stages = list(obj(ws.pipeline).stages).map(obj);
  const triggers = list(ws.triggers).map(obj);
  const data = obj(ws.data);
  const tools = Object.entries(data).flatMap(([e, a]) => ["list", "get", ...(a === "write" ? ["create", "update", "delete"] : [])].map((v) => `data__${v}_${toolSlug(e)}`));
  const [open, setOpen] = useState(index === 0);
  const fieldsOf = (e: string) => Object.keys(obj(obj(obj(manifest.entities)[e]).fields));
  const stagesPath: Path = [...base, "pipeline", "stages"];
  // An id no other stage has, so a new stage never ends up running after itself.
  const newStageId = (prefix: string) => {
    const taken = new Set(stages.map((s) => str(s.stage_id)));
    let n = stages.length + 1;
    while (taken.has(`${prefix}-${n}`)) n++;
    return `${prefix}-${n}`;
  };
  const addStage = (stage: Obj) => set([...stagesPath, stages.length], { ...stage, inputs: stages.length ? [str(stages[stages.length - 1].stage_id)] : [] });
  // Renaming a stage id carries over to the stages that run after it, and to @mentions of it.
  const renameStage = (si: number, to: string) => {
    const from = str(stages[si].stage_id);
    const renamed = stages.map((s, i) => (i === si ? { ...s, stage_id: to } : { ...s, inputs: strs(s.inputs).map((x) => (x === from ? to : x)) }));
    set(base, renameMentions({ ...ws, pipeline: { ...obj(ws.pipeline), stages: renamed } }, from, to));
  };
  // A text its agents read: a declared connector it mentions is connected to the workspace.
  const setText = (path: Path, v: string) => {
    set(path, v);
    const add = newlyMentioned(v, connectors, strs(ws.connectors));
    if (add.length > 0) set([...base, "connectors"], [...strs(ws.connectors), ...add]);
  };
  const removeStage = (si: number) => {
    const id = str(stages[si].stage_id);
    set(stagesPath, stages.filter((_, i) => i !== si).map((s) => ({ ...s, inputs: strs(s.inputs).filter((x) => x !== id) })));
  };
  return (
    <Card>
      <button type="button" onClick={() => setOpen(!open)} className="flex w-full items-center gap-3 px-5 py-3 text-left">
        <Icons.ChevronRight className={cx("h-4 w-4 text-zinc-400 transition-transform", open && "rotate-90")} />
        <span className="font-semibold text-zinc-900 dark:text-zinc-100">{str(ws.name) || "Untitled workspace"}</span>
        <span className="font-mono text-[11px] text-zinc-500">{str(ws.key)}</span>
        <span className="ml-auto text-xs text-zinc-500">{stages.length} stage{stages.length === 1 ? "" : "s"} · {triggers.length} trigger{triggers.length === 1 ? "" : "s"}</span>
      </button>
      {open && (
        <div className="space-y-5 border-t border-zinc-200 p-5 dark:border-zinc-800">
          <div className="grid gap-4 md:grid-cols-3">
            <Field label="Name"><TextInput value={str(ws.name)} onChange={(v) => set([...base, "name"], v)} /></Field>
            <Field label="Key" hint="Stable across versions: upgrades match workspaces by it."><TextInput mono value={str(ws.key)} onChange={(v) => set([...base, "key"], typedSlug(v))} /></Field>
            <Field label="Daily cost limit (USD)"><NumberInput value={num(ws.daily_cost_usd)} onChange={(v) => set([...base, "daily_cost_usd"], v)} placeholder="no limit" /></Field>
            <Field label="Goal" className="md:col-span-3"><MentionArea value={str(ws.goal)} onChange={(v) => setText([...base, "goal"], v)} rows={2} mentionables={mentionables} placeholder="What this workspace is for. Type @ to mention a stage, skill, knowledge or connector." /></Field>
          </div>

          <div className="grid gap-5 md:grid-cols-2">
            <div>
              <div className="mb-2 text-xs font-medium text-zinc-700 dark:text-zinc-300">Data its agents may use</div>
              <AccessPicker entities={entities} value={data} onChange={(v) => set([...base, "data"], v)} />
            </div>
            <div className="space-y-3">
              <Field label="Autonomy">
                <select className={inputClass} value={str(obj(ws.safety).autonomy) || "Autonomous"} onChange={(e) => set([...base, "safety", "autonomy"], e.target.value)}>
                  <option value="Autonomous">Autonomous: agents act within their limits</option>
                  <option value="SemiAutonomous">Semi-autonomous: external writes wait for approval</option>
                  <option value="Supervised">Supervised: every write waits for approval</option>
                </select>
              </Field>
              {connectors.length > 0 && (
                <div>
                  <div className="mb-1.5 text-xs font-medium text-zinc-700 dark:text-zinc-300">Connectors</div>
                  <div className="flex flex-wrap gap-2">
                    {connectors.map((c) => {
                      const on = strs(ws.connectors).includes(c);
                      return (
                        <label key={c} className="flex items-center gap-1.5 text-sm">
                          <input type="checkbox" checked={on} onChange={() => set([...base, "connectors"], on ? strs(ws.connectors).filter((x) => x !== c) : [...strs(ws.connectors), c])} /> {c}
                        </label>
                      );
                    })}
                  </div>
                </div>
              )}
            </div>
          </div>

          <div>
            <div className="mb-2 flex items-center justify-between">
              <div className="text-sm font-semibold text-zinc-900 dark:text-zinc-100">Pipeline</div>
              <div className="flex gap-2">
                <Button size="sm" onClick={() => addStage({ stage_id: newStageId("stage"), name: `Stage ${stages.length + 1}`, instructions: "", capabilities: ["research"] })}>+ Agent stage</Button>
                <Button size="sm" onClick={() => addStage({ stage_id: newStageId("action"), name: `Action ${stages.length + 1}`, kind: "Action", tool: tools[0] ?? "", arguments: "{}", retries: 0 })}>+ Action stage</Button>
              </div>
            </div>
            <div className="space-y-3">
              {stages.map((s, si) => {
                const spath = [...base, "pipeline", "stages", si];
                const isAction = str(s.kind) === "Action";
                return (
                  <div key={si} className="rounded-lg border border-zinc-200 p-3 dark:border-zinc-800">
                    <div className="mb-2 flex items-center gap-2">
                      <Badge tone={isAction ? "blue" : "brand"}>{isAction ? "Action (no model)" : "Agent"}</Badge>
                      <input className={cx(inputClass, "w-56 py-1 text-sm font-medium")} value={str(s.name)} aria-label="Stage name"
                        onChange={(e) => { set([...spath, "name"], e.target.value); }} />
                      <input className={cx(inputClass, "w-40 py-1 font-mono text-xs")} value={str(s.stage_id)} aria-label="Stage id"
                        onChange={(e) => renameStage(si, typedSlug(e.target.value))} />
                      <div className="ml-auto"><RemoveButton label="Remove stage" onClick={() => removeStage(si)} /></div>
                    </div>
                    {isAction ? (
                      <div className="grid gap-3 md:grid-cols-2">
                        <Field label="Tool" hint="A connection tool, e.g. data__update_ticket or crm__find_customer.">
                          <input list={`tools-${index}`} className={cx(inputClass, "font-mono text-xs")} value={str(s.tool)} onChange={(e) => set([...spath, "tool"], e.target.value)} />
                          <datalist id={`tools-${index}`}>{tools.map((t) => <option key={t} value={t} />)}</datalist>
                        </Field>
                        <Field label="Arguments (JSON)" hint="{{ input.record_id }}, {{ previous }}, {{ vars.name }} are filled in from the run.">
                          <TextArea mono rows={3} value={typeof s.arguments === "string" ? s.arguments : JSON.stringify(s.arguments ?? {}, null, 2)} onChange={(v) => set([...spath, "arguments"], v)} />
                        </Field>
                      </div>
                    ) : (
                      <div className="grid gap-3 md:grid-cols-2">
                        <Field label="Role"><TextInput value={str(s.role)} onChange={(v) => set([...spath, "role"], v)} placeholder="Support specialist" /></Field>
                        <Field label="Capabilities" hint="Which tools it gets besides its connections.">
                          <div className="flex flex-wrap gap-2 pt-1">
                            {CAPABILITIES.map((c) => {
                              const on = strs(s.capabilities).includes(c.id);
                              return (
                                <label key={c.id} className="flex items-center gap-1 text-xs">
                                  <input type="checkbox" checked={on} onChange={() => set([...spath, "capabilities"], on ? strs(s.capabilities).filter((x) => x !== c.id) : [...strs(s.capabilities), c.id])} /> {c.label}
                                </label>
                              );
                            })}
                          </div>
                        </Field>
                        <Field label="Instructions" className="md:col-span-2" hint="Name the data__ tools it should use. {{ vars.name }} is filled in. @ mentions a stage, a skill (loaded first), knowledge (read first) or a connector (connected to this workspace).">
                          <MentionArea rows={4} value={str(s.instructions)} onChange={(v) => setText([...spath, "instructions"], v)} mentionables={mentionables}
                            placeholder="Triage the request with @skill:… and text the tenant with @connector:…" />
                        </Field>
                      </div>
                    )}
                    {(si > 0 || strs(s.inputs).length > 0) && (
                      <div className="mt-2 flex flex-wrap items-center gap-2 text-xs text-zinc-600 dark:text-zinc-400">
                        Runs after:
                        {stages.slice(0, si).map((p) => {
                          const id = str(p.stage_id);
                          const on = strs(s.inputs).includes(id);
                          return (
                            <label key={id} className="flex items-center gap-1">
                              <input type="checkbox" checked={on} onChange={() => set([...spath, "inputs"], on ? strs(s.inputs).filter((x) => x !== id) : [...strs(s.inputs), id])} /> {str(p.name) || id}
                            </label>
                          );
                        })}
                        {strs(s.inputs).filter((x) => !stages.slice(0, si).some((p) => str(p.stage_id) === x)).map((x) => (
                          <button key={`bad-${x}`} type="button" title="Not an earlier stage: click to remove"
                            className="rounded border border-red-300 px-1.5 py-0.5 text-red-600 hover:bg-red-50 dark:border-red-800 dark:text-red-400 dark:hover:bg-red-950"
                            onClick={() => set([...spath, "inputs"], strs(s.inputs).filter((y) => y !== x))}>
                            {x === str(s.stage_id) ? `${x} (itself)` : x} ✕
                          </button>
                        ))}
                        <span className="text-zinc-400">(none: it starts with the run, in parallel)</span>
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </div>

          <div>
            <div className="mb-2 flex items-center justify-between">
              <div className="text-sm font-semibold text-zinc-900 dark:text-zinc-100">Triggers <span className="font-normal text-zinc-500">what starts a run</span></div>
              <select className={cx(inputClass, "h-8 w-48 py-1 text-xs")} value="" aria-label="Add a trigger"
                onChange={(e) => {
                  const kind = e.target.value;
                  if (!kind) return;
                  const n = triggers.length + 1;
                  const t: Obj = kind === "Record" ? { kind, name: `Record trigger ${n}`, entity: entities[0] ?? "", on: ["created"] }
                    : kind === "Schedule" ? { kind, name: `Schedule ${n}`, cron: "0 8 * * 1", instruction: "" }
                    : { kind, name: `Webhook ${n}`, instruction: "" };
                  set([...base, "triggers", triggers.length], t);
                }}>
                <option value="">+ Add a trigger…</option>
                <option value="Record">When a record changes</option>
                <option value="Schedule">On a schedule</option>
                <option value="Webhook">When a webhook is called</option>
              </select>
            </div>
            {triggers.length === 0 && <p className="text-xs text-zinc-500">No triggers: runs start only by hand or from the API.</p>}
            <div className="space-y-2">
              {triggers.map((t, ti) => {
                const tpath = [...base, "triggers", ti];
                const kind = str(t.kind);
                const conditions = list(obj(t.rule).conditions).map(obj);
                return (
                  <div key={ti} className="rounded-lg border border-zinc-200 p-3 dark:border-zinc-800">
                    <div className="mb-2 flex items-center gap-2">
                      <Badge>{kind}</Badge>
                      <input className={cx(inputClass, "w-64 py-1 text-sm")} value={str(t.name)} aria-label="Trigger name" onChange={(e) => set([...tpath, "name"], e.target.value)} />
                      <div className="ml-auto"><RemoveButton label="Remove trigger" onClick={() => set(tpath, undefined)} /></div>
                    </div>
                    {kind === "Record" && (
                      <div className="space-y-2 text-sm">
                        <div className="flex flex-wrap items-center gap-3">
                          <select className={cx(inputClass, "w-44 py-1 text-xs")} value={str(t.entity)} onChange={(e) => set([...tpath, "entity"], e.target.value)}>
                            <option value="">Which entity?</option>
                            {entities.map((e) => <option key={e} value={e}>{e}</option>)}
                          </select>
                          {["created", "updated", "deleted"].map((ev) => {
                            const on = (strs(t.on).length ? strs(t.on) : ["created"]).includes(ev);
                            return (
                              <label key={ev} className="flex items-center gap-1 text-xs">
                                <input type="checkbox" checked={on} onChange={() => {
                                  const current = strs(t.on).length ? strs(t.on) : ["created"];
                                  set([...tpath, "on"], on ? current.filter((x) => x !== ev) : [...current, ev]);
                                }} /> {ev}
                              </label>
                            );
                          })}
                        </div>
                        {conditions.map((c, ci) => (
                          <div key={ci} className="flex items-center gap-2 text-xs">
                            <span className="text-zinc-500">only when</span>
                            <select className={cx(inputClass, "w-36 py-1 text-xs")} value={str(c.field)} onChange={(e) => set([...tpath, "rule", "conditions", ci, "field"], e.target.value)}>
                              {fieldsOf(str(t.entity)).map((f) => <option key={f} value={f}>{f}</option>)}
                            </select>
                            <select className={cx(inputClass, "w-28 py-1 text-xs")} value={str(c.op)} onChange={(e) => set([...tpath, "rule", "conditions", ci, "op"], e.target.value)}>
                              {OPS.map((o) => <option key={o} value={o}>{o}</option>)}
                            </select>
                            {str(c.op) !== "exists" && <input className={cx(inputClass, "w-40 py-1 text-xs")} value={str(c.value)} onChange={(e) => set([...tpath, "rule", "conditions", ci, "value"], e.target.value)} />}
                            <RemoveButton label="Remove condition" onClick={() => set([...tpath, "rule", "conditions", ci], undefined)} />
                          </div>
                        ))}
                        <button type="button" className="text-xs text-brand-600 hover:underline dark:text-brand-400"
                          onClick={() => set([...tpath, "rule", "conditions", conditions.length], { field: fieldsOf(str(t.entity))[0] ?? "", op: "==", value: "" })}>
                          + only when a field…
                        </button>
                      </div>
                    )}
                    {kind === "Schedule" && (
                      <div className="grid gap-2 md:grid-cols-2">
                        <Field label="Cron (UTC)" hint="0 8 * * 1 = Mondays 08:00. Or leave empty and set minutes.">
                          <TextInput mono value={str(t.cron)} onChange={(v) => set([...tpath, "cron"], v || undefined)} />
                        </Field>
                        <Field label="Every N minutes"><NumberInput value={num(t.every_minutes)} onChange={(v) => set([...tpath, "every_minutes"], v)} /></Field>
                      </div>
                    )}
                    {kind === "Webhook" && <p className="text-xs text-zinc-500">Its secret URL is shown once it&apos;s deployed; each POST starts a run with the body as input.</p>}
                    {(kind === "Watch" || kind === "Form") && <p className="text-xs text-zinc-500">Edit this {kind.toLowerCase()} trigger&apos;s details in the YAML tab (Review &amp; deploy).</p>}
                    {kind !== "Watch" && kind !== "Form" && (
                      <div className="mt-2">
                        <MentionArea singleLine className="text-xs" value={str(t.instruction)} placeholder="What each run is told (optional; @ to mention)" mentionables={mentionables} onChange={(v) => setText([...tpath, "instruction"], v)} />
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </div>

          <div className="flex justify-end">
            <Button variant="danger" size="sm" onClick={() => window.confirm(`Remove ${str(ws.name)}?`) && set(base, undefined)}>Remove workspace</Button>
          </div>
        </div>
      )}
    </Card>
  );
}

function Assistant({ manifest, set, entities, mentionsFor }: { manifest: Obj; set: SetFn; entities: string[]; mentionsFor: MentionsFor }) {
  const a = manifest.assistant ? obj(manifest.assistant) : null;
  const jwt = obj(obj(manifest.end_users).jwt);
  const connectors = list(manifest.connectors).map((c) => str(obj(c).name)).filter(Boolean);
  return (
    <div className="space-y-4">
      <Card>
        <CardHeader title="Assistant" description="The agent your own customers talk to, on your site (embed widget) or in your app. Each one reaches only their own records." />
        <div className="space-y-4 p-5">
          <Toggle checked={!!a} label="Give end users an assistant" onChange={(on) => set(["assistant"], on ? { name: "Assistant", instructions: "", data: {} } : undefined)} />
          {a && (
            <div className="grid gap-4 md:grid-cols-2">
              <Field label="Name"><TextInput value={str(a.name)} onChange={(v) => set(["assistant", "name"], v)} /></Field>
              <Field label="Greeting"><TextInput value={str(a.greeting)} onChange={(v) => set(["assistant", "greeting"], v || undefined)} placeholder="Hi! How can I help?" /></Field>
              <Field label="Instructions" className="md:col-span-2" hint="{{ inputs.name }} is filled in at install. Tell it which data__ tools to use and when. @ mentions a connector (connected to the assistant) or your organization's skills and knowledge.">
                <MentionArea rows={7} value={str(a.instructions)} mentionables={mentionsFor({ in: "assistant" })} onChange={(v) => {
                  set(["assistant", "instructions"], v);
                  const add = newlyMentioned(v, connectors, strs(a.connectors));
                  if (add.length > 0) set(["assistant", "connectors"], [...strs(a.connectors), ...add]);
                }} />
              </Field>
              <div>
                <div className="mb-2 text-xs font-medium text-zinc-700 dark:text-zinc-300">Data it may use (for each customer, their own)</div>
                <AccessPicker entities={entities} value={obj(a.data)} onChange={(v) => set(["assistant", "data"], v)} />
              </div>
              <div className="space-y-3">
                <Field label="Monthly cost cap per customer (USD)" hint="Leave empty for none.">
                  <NumberInput value={num(a.monthly_cost_limit_usd_per_user)} onChange={(v) => set(["assistant", "monthly_cost_limit_usd_per_user"], v)} />
                </Field>
                <Field label="Messages per minute per customer"><NumberInput value={num(a.max_messages_per_minute)} placeholder="10" onChange={(v) => set(["assistant", "max_messages_per_minute"], v)} /></Field>
                <Field label="Allowed sites" hint="Comma-separated origins that may embed it, e.g. https://app.example.com. Empty: any.">
                  <TextInput value={strs(a.allowed_origins).join(", ")} onChange={(v) => set(["assistant", "allowed_origins"], v.split(",").map((x) => x.trim()).filter(Boolean))} />
                </Field>
                {connectors.length > 0 && (
                  <div className="flex flex-wrap gap-2 text-sm">
                    <span className="text-xs text-zinc-500">Connectors:</span>
                    {connectors.map((c) => {
                      const on = strs(a.connectors).includes(c);
                      return <label key={c} className="flex items-center gap-1 text-xs"><input type="checkbox" checked={on} onChange={() => set(["assistant", "connectors"], on ? strs(a.connectors).filter((x) => x !== c) : [...strs(a.connectors), c])} /> {c}</label>;
                    })}
                  </div>
                )}
              </div>
            </div>
          )}
        </div>
      </Card>
      {a && (
        <Card>
          <CardHeader title="Your own sign-in (optional)" description="Let customers use the tokens your identity provider issues (JWT), instead of asking Aktor for a token per user." />
          <div className="space-y-4 p-5">
            <Toggle checked={!!obj(manifest.end_users).jwt} label="Accept my identity provider's tokens"
              onChange={(on) => set(["end_users"], on ? { jwt: { issuer: "", audience: "", jwks_url: "" } } : undefined)} />
            {!!obj(manifest.end_users).jwt && (
              <div className="grid gap-4 md:grid-cols-2">
                <Field label="Issuer (iss)"><TextInput mono value={str(jwt.issuer)} onChange={(v) => set(["end_users", "jwt", "issuer"], v)} placeholder="https://auth.example.com/" /></Field>
                <Field label="Audience (aud)"><TextInput mono value={str(jwt.audience)} onChange={(v) => set(["end_users", "jwt", "audience"], v)} placeholder="my-app" /></Field>
                <Field label="JWKS URL" className="md:col-span-2"><TextInput mono value={str(jwt.jwks_url)} onChange={(v) => set(["end_users", "jwt", "jwks_url"], v)} placeholder="https://auth.example.com/.well-known/jwks.json" /></Field>
                <Field label="User id claim"><TextInput mono value={str(jwt.user_claim)} onChange={(v) => set(["end_users", "jwt", "user_claim"], v || undefined)} placeholder="sub" /></Field>
                <Field label="Name claim"><TextInput mono value={str(jwt.name_claim)} onChange={(v) => set(["end_users", "jwt", "name_claim"], v || undefined)} placeholder="name" /></Field>
              </div>
            )}
          </div>
        </Card>
      )}
    </div>
  );
}

function Setup({ manifest, set, plugins, globals, renameEverywhere }: { manifest: Obj; set: SetFn; plugins: PluginInfo[]; globals: GlobalConnector[]; renameEverywhere: RenameFn }) {
  const inputs = list(manifest.inputs).map(obj);
  const connectors = list(manifest.connectors).map(obj);
  const choosable = plugins.filter((p) => p.id !== "aktor-records");
  return (
    <div className="space-y-4">
      <Card>
        <CardHeader title="Inputs" description="Values asked for when someone installs it (a company name, a fee). Workspaces use them as {{ vars.key }}, the assistant as {{ inputs.key }}."
          actions={<Button size="sm" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => set(["inputs", inputs.length], { key: `input_${inputs.length + 1}`, label: "" })}>Add input</Button>} />
        <div className="space-y-2 p-5">
          {inputs.length === 0 && <p className="text-sm text-zinc-500">No inputs.</p>}
          {inputs.map((inp, i) => (
            <div key={i} className="grid items-center gap-2 md:grid-cols-[10rem_1fr_8rem_1fr_auto_auto]">
              <TextInput mono value={str(inp.key)} placeholder="key" onChange={(v) => set(["inputs", i, "key"], snake(v))} />
              <TextInput value={str(inp.label)} placeholder="Label" onChange={(v) => set(["inputs", i, "label"], v || undefined)} />
              <select className={inputClass} value={str(inp.type) || "string"} onChange={(e) => set(["inputs", i, "type"], e.target.value)}>
                {["string", "text", "number", "integer", "boolean"].map((t) => <option key={t}>{t}</option>)}
              </select>
              <TextInput value={str(inp.default)} placeholder="Default" onChange={(v) => set(["inputs", i, "default"], v || undefined)} />
              <label className="flex items-center gap-1 text-xs"><input type="checkbox" checked={inp.required === true} onChange={(e) => set(["inputs", i, "required"], e.target.checked)} /> required</label>
              <RemoveButton label="Remove input" onClick={() => set(["inputs", i], undefined)} />
            </div>
          ))}
        </div>
      </Card>
      <Card>
        <CardHeader title="Connectors" description="Integrations it needs (SMS, Slack, a CRM, an MCP server…). Secrets are never in the package: whoever deploys it enters them, or picks one of your organization's global connectors."
          actions={<div className="flex gap-2">
            {globals.length > 0 && (
              <select className={cx(inputClass, "h-8 w-auto py-0 text-xs")} value="" aria-label="Add from a global connector"
                onChange={(e) => {
                  const g = globals.find((x) => x.connector_id === e.target.value);
                  if (g) set(["connectors", connectors.length], { name: connectors.some((c) => str(c.name) === g.name) ? `${g.name}_${connectors.length + 1}` : g.name, plugin: g.plugin_id, global: g.name });
                }}>
                <option value="">+ From a global connector…</option>
                {globals.map((g) => <option key={g.connector_id} value={g.connector_id}>{g.name} ({g.plugin_name})</option>)}
              </select>
            )}
            <Button size="sm" className="shrink-0 whitespace-nowrap" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => set(["connectors", connectors.length], { name: `connector_${connectors.length + 1}`, plugin: choosable[0]?.id ?? "" })}>Add connector</Button>
          </div>} />
        <div className="space-y-3 p-5">
          {connectors.length === 0 && <p className="text-sm text-zinc-500">No connectors: its agents use only their own data and built-in tools.</p>}
          {connectors.map((c, i) => {
            const plugin = plugins.find((p) => p.id === str(c.plugin));
            const matching = globals.filter((g) => g.plugin_id === str(c.plugin));
            const global = str(c.global);
            return (
              <div key={i} className="rounded-lg border border-zinc-200 p-3 dark:border-zinc-800">
                <div className="grid items-center gap-2 md:grid-cols-[12rem_1fr_auto_auto]">
                  <TextInput mono value={str(c.name)} placeholder="name (its tool prefix)" onChange={(v) => {
                    const from = str(c.name), to = snake(v).replace(/__+/g, "_");
                    if (!from || !to) { set(["connectors", i, "name"], to); return; }
                    // The workspaces and the assistant that use it, and mentions of it, follow the new name.
                    renameEverywhere(`connector:${from}`, `connector:${to}`, (m) => {
                      const rename = (names: Json | undefined) => strs(names).map((x) => (x === from ? to : x));
                      let next = setIn(m, ["connectors", i, "name"], to);
                      list(next.workspaces).forEach((w, wi) => { if (strs(obj(w).connectors).includes(from)) next = setIn(next, ["workspaces", wi, "connectors"], rename(obj(w).connectors)); });
                      if (strs(obj(next.assistant).connectors).includes(from)) next = setIn(next, ["assistant", "connectors"], rename(obj(next.assistant).connectors));
                      return next;
                    });
                  }} />
                  <select className={inputClass} value={str(c.plugin)} onChange={(e) => set(["connectors", i, "plugin"], e.target.value)}>
                    {choosable.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
                  </select>
                  <label className="flex items-center gap-1 text-xs"><input type="checkbox" checked={c.required !== false} onChange={(e) => set(["connectors", i, "required"], e.target.checked)} /> required</label>
                  <RemoveButton label="Remove connector" onClick={() => set(["connectors", i], undefined)} />
                </div>
                {(matching.length > 0 || global) && (
                  <div className="mt-2 flex flex-wrap items-center gap-2 text-xs">
                    <Badge tone={global ? "brand" : "neutral"}>Global</Badge>
                    <select className={cx(inputClass, "h-8 w-auto py-0 text-xs")} value={global} aria-label="Global connector"
                      onChange={(e) => set(["connectors", i, "global"], e.target.value || undefined)}>
                      <option value="">Don&apos;t use one (enter secrets at deploy)</option>
                      {matching.map((g) => <option key={g.connector_id} value={g.name}>Use {g.name}{g.description ? ` — ${g.description}` : ""}</option>)}
                      {global && !matching.some((g) => g.name === global) && <option value={global}>{global} (not in this organization)</option>}
                    </select>
                    {global && <span className="text-zinc-500">Its settings and secrets come from the global connector. Deploying elsewhere? Names differ per organization, so it can also be chosen at deploy.</span>}
                  </div>
                )}
                {!global && plugin && plugin.settings.filter((f) => !f.secret).length > 0 && (
                  <div className="mt-2 grid gap-2 md:grid-cols-2">
                    {plugin.settings.filter((f) => !f.secret).map((f) => (
                      <Field key={f.key} label={f.label} hint={f.description ?? undefined}>
                        <TextInput value={str(obj(c.settings)[f.key])} placeholder={f.placeholder ?? f.default_value ?? ""}
                          onChange={(v) => set(["connectors", i, "settings", f.key], v || undefined)} />
                      </Field>
                    ))}
                  </div>
                )}
                {!global && plugin && plugin.settings.some((f) => f.secret) && (
                  <p className="mt-2 text-xs text-zinc-500">Asked for at deploy: {plugin.settings.filter((f) => f.secret).map((f) => f.label).join(", ")}.</p>
                )}
              </div>
            );
          })}
        </div>
      </Card>
    </div>
  );
}

function Knowhow({ manifest, set, skills, setSkills, workspaces, mentionsFor, renameEverywhere }: {
  manifest: Obj; set: SetFn; skills: BuilderSkill[]; setSkills: React.Dispatch<React.SetStateAction<BuilderSkill[]>>; workspaces: Obj[];
  mentionsFor: MentionsFor; renameEverywhere: RenameFn;
}) {
  const knowledge = list(manifest.knowledge).map(obj);
  const wsSelect = (value: string, onChange: (v: string | null) => void) => (
    <select className={cx(inputClass, "w-48")} value={value} onChange={(e) => onChange(e.target.value || null)}>
      <option value="">Every workspace</option>
      {workspaces.map((w) => <option key={str(w.key)} value={str(w.key)}>{str(w.name)}</option>)}
    </select>
  );
  return (
    <div className="space-y-4">
      <Card>
        <CardHeader title="Knowledge" description="Reference text agents search: a policy, a price list, an FAQ."
          actions={<Button size="sm" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => set(["knowledge", knowledge.length], { key: "New document", text: "" })}>Add knowledge</Button>} />
        <div className="space-y-3 p-5">
          {knowledge.length === 0 && <p className="text-sm text-zinc-500">No knowledge.</p>}
          {knowledge.map((k, i) => (
            <div key={i} className="space-y-2 rounded-lg border border-zinc-200 p-3 dark:border-zinc-800">
              <div className="flex items-center gap-2">
                <TextInput value={str(k.key)} placeholder="Title" onChange={(v) => {
                  const from = knowledgeHandle(str(k.key)), to = knowledgeHandle(v);
                  if (from && to) renameEverywhere(`knowledge:${from}`, `knowledge:${to}`, (m) => setIn(m, ["knowledge", i, "key"], v));
                  else set(["knowledge", i, "key"], v);
                }} />
                {wsSelect(str(k.workspace), (v) => set(["knowledge", i, "workspace"], v))}
                <RemoveButton label="Remove" onClick={() => set(["knowledge", i], undefined)} />
              </div>
              <TextArea rows={5} value={str(k.text)} placeholder="Markdown or plain text" onChange={(v) => set(["knowledge", i, "text"], v)} />
            </div>
          ))}
        </div>
      </Card>
      <Card>
        <CardHeader title="Skills" description="How your organization does a kind of work. Agents see each skill's description and load it when their work matches."
          actions={<Button size="sm" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => setSkills([...skills, { name: `skill-${skills.length + 1}`, description: "", instructions: "", workspace: null }])}>Add skill</Button>} />
        <div className="space-y-3 p-5">
          {skills.length === 0 && <p className="text-sm text-zinc-500">No skills.</p>}
          {skills.map((s, i) => {
            const update = (patch: Partial<BuilderSkill>) => setSkills(skills.map((x, j) => (j === i ? { ...x, ...patch } : x)));
            return (
              <div key={i} className="space-y-2 rounded-lg border border-zinc-200 p-3 dark:border-zinc-800">
                <div className="flex items-center gap-2">
                  <TextInput mono value={s.name} placeholder="skill-name" className="w-56" onChange={(v) => {
                    const to = typedSlug(v, 64);
                    update({ name: to });
                    if (s.name && to) renameEverywhere(`skill:${s.name}`, `skill:${to}`);
                  }} />
                  <TextInput value={s.description} placeholder="When to use it (one line)" onChange={(v) => update({ description: v })} />
                  {wsSelect(s.workspace ?? "", (v) => update({ workspace: v }))}
                  <RemoveButton label="Remove skill" onClick={() => setSkills(skills.filter((_, j) => j !== i))} />
                </div>
                <MentionArea rows={5} value={s.instructions} placeholder="The steps, in Markdown. @ mentions knowledge, another skill or a connector." mentionables={mentionsFor({ in: "skill", workspace: s.workspace ?? null })} onChange={(v) => update({ instructions: v })} />
              </div>
            );
          })}
        </div>
      </Card>
    </div>
  );
}

function Evals({ manifest, set, entities, workspaces, mentionsFor }: { manifest: Obj; set: SetFn; entities: string[]; workspaces: Obj[]; mentionsFor: MentionsFor }) {
  const evals = list(manifest.evals).map(obj);
  return (
    <Card>
      <CardHeader title="Evals" description="Expected behaviour, checked by running a workspace for real: its status, what its summary says, and the records it writes."
        actions={<Button size="sm" icon={<Icons.Plus className="h-3.5 w-3.5" />} onClick={() => set(["evals", evals.length], { name: `eval ${evals.length + 1}`, workspace: str(workspaces[0]?.key), input: "", expect: { status: "Completed" } })}>Add eval</Button>} />
      <div className="space-y-3 p-5">
        {evals.length === 0 && <p className="text-sm text-zinc-500">No evals yet. One per behaviour you care about keeps upgrades honest.</p>}
        {evals.map((e, i) => {
          const expect = obj(e.expect);
          const records = obj(expect.records);
          return (
            <div key={i} className="space-y-2 rounded-lg border border-zinc-200 p-3 dark:border-zinc-800">
              <div className="flex items-center gap-2">
                <TextInput value={str(e.name)} placeholder="What it checks" onChange={(v) => set(["evals", i, "name"], v)} />
                <select className={cx(inputClass, "w-52")} value={str(e.workspace)} onChange={(ev) => set(["evals", i, "workspace"], ev.target.value)}>
                  {workspaces.map((w) => <option key={str(w.key)} value={str(w.key)}>{str(w.name)}</option>)}
                </select>
                <RemoveButton label="Remove eval" onClick={() => set(["evals", i], undefined)} />
              </div>
              <MentionArea rows={2} value={str(e.input)} placeholder="The run's input, e.g. a customer's message (@ to mention a stage, skill or knowledge)" mentionables={mentionsFor({ in: "workspace", key: str(e.workspace) })} onChange={(v) => set(["evals", i, "input"], v)} />
              <div className="grid gap-2 md:grid-cols-3">
                <Field label="Expected status">
                  <select className={inputClass} value={str(expect.status) || "Completed"} onChange={(ev) => set(["evals", i, "expect", "status"], ev.target.value)}>
                    {["Completed", "Failed", "TimedOut"].map((s) => <option key={s}>{s}</option>)}
                  </select>
                </Field>
                <Field label="Summary mentions" hint="Comma-separated">
                  <TextInput value={strs(expect.summary_contains).join(", ")} onChange={(v) => set(["evals", i, "expect", "summary_contains"], v.split(",").map((x) => x.trim()).filter(Boolean))} />
                </Field>
                <Field label="Records written (at least)">
                  <div className="flex gap-2">
                    <select className={inputClass} value={Object.keys(records)[0] ?? ""} onChange={(ev) => set(["evals", i, "expect", "records"], ev.target.value ? { [ev.target.value]: { at_least: 1 } } : undefined)}>
                      <option value="">—</option>
                      {entities.map((en) => <option key={en}>{en}</option>)}
                    </select>
                    {Object.keys(records)[0] && (
                      <NumberInput value={num(obj(records[Object.keys(records)[0]]).at_least)} onChange={(v) => set(["evals", i, "expect", "records", Object.keys(records)[0], "at_least"], v)} />
                    )}
                  </div>
                </Field>
              </div>
            </div>
          );
        })}
      </div>
    </Card>
  );
}

function Review({ bundle, validation, manifestName, onApply }: { bundle: SolutionBundle; validation: SolutionValidation | null; manifestName: string; onApply: (yaml: string) => void }) {
  const [editing, setEditing] = useState<string | null>(null);
  return (
    <div className="space-y-4">
      <Card>
        <CardHeader title="Check" description="The same validation the CLI and every deploy run, against this server." />
        <div className="p-5 text-sm">
          {!validation ? <span className="text-zinc-500">Checking…</span>
            : validation.ok ? <div className="text-emerald-600 dark:text-emerald-400">✓ Valid. Deploy it from the button at the top, or download the package.</div>
            : (
              <div className="text-rose-700 dark:text-rose-300">
                <div className="mb-1 font-medium">{validation.errors.length} problem(s)</div>
                <ul className="list-disc space-y-0.5 pl-5 text-xs">{validation.errors.map((e) => <li key={e}>{e}</li>)}</ul>
              </div>
            )}
          {validation?.warnings.length ? <ul className="mt-2 list-disc space-y-0.5 pl-5 text-xs text-amber-700 dark:text-amber-400">{validation.warnings.map((w) => <li key={w}>{w}</li>)}</ul> : null}
        </div>
      </Card>
      <Card>
        <CardHeader title="aktor.yaml"
          description={editing === null ? "What the forms make. Download it to keep the package in git and use the CLI; or edit it here and apply it back to the forms." : "Edit, then apply: the server reads it, and the forms show the result."}
          actions={
            <>
              <Button size="sm" variant="primary" icon={<Icons.Download className="h-3.5 w-3.5" />} title="Everything: aktor.yaml and its skill files, ready for git or the CLI"
                onClick={() => download(`${manifestName}.zip`, zipPackage(manifestName, { "aktor.yaml": bundle.manifest, ...bundle.files }))}>Package (.zip)</Button>
              <Button size="sm" icon={<Icons.Download className="h-3.5 w-3.5" />}
                title={Object.keys(bundle.files).length ? "Only the manifest; its skill files are in the .zip" : "The manifest (the whole package)"}
                onClick={() => download("aktor.yaml", bundle.manifest)}>{Object.keys(bundle.files).length ? "aktor.yaml only" : "aktor.yaml"}</Button>
              {editing === null
                ? <Button size="sm" onClick={() => setEditing(bundle.manifest)}>Edit YAML</Button>
                : <>
                    <Button size="sm" onClick={() => setEditing(null)}>Cancel</Button>
                    <Button size="sm" variant="primary" onClick={() => { onApply(editing); setEditing(null); }}>Apply to the forms</Button>
                  </>}
            </>
          } />
        {editing === null
          ? <pre className="max-h-[36rem] overflow-auto bg-zinc-50 p-4 font-mono text-xs leading-5 text-zinc-800 dark:bg-zinc-950 dark:text-zinc-300">{bundle.manifest}</pre>
          : <textarea className="block h-[36rem] w-full resize-y bg-zinc-50 p-4 font-mono text-xs leading-5 text-zinc-800 outline-none dark:bg-zinc-950 dark:text-zinc-300" value={editing} onChange={(e) => setEditing(e.target.value)} spellCheck={false} />}
        {Object.keys(bundle.files).length > 0 && (
          <div className="border-t border-zinc-200 px-5 py-3 text-xs text-zinc-500 dark:border-zinc-800">
            Also in the package: {Object.keys(bundle.files).map((f) => <code key={f} className="mr-2 rounded bg-zinc-100 px-1 font-mono dark:bg-zinc-800">{f}</code>)}
          </div>
        )}
      </Card>
    </div>
  );
}
