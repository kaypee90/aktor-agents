"use client";

import { useEffect, useRef, useState } from "react";
import {
  API_BASE,
  apiErrorMessage,
  bundleFromFolder,
  deploySolution,
  getRegistryPackage,
  validateSolution,
  type SolutionBundle,
  type SolutionValidation,
} from "@/lib/api";
import { Badge, Button, CopyField, ErrorBanner, Field, Modal, Toggle, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

// ---------------------------------------------------------------- deploy

/** Validates a package against the server, asks for its inputs and secrets, then installs or
 * upgrades it, and shows the webhook URLs it created. The package comes from a folder the person
 * picks, the registry (packageRef), or a solution built in the builder (initialBundle). */
export function DeployDialog({ installId, packageRef, initialBundle, currentInputs, onClose, onDone }: {
  installId?: string; packageRef?: string; initialBundle?: SolutionBundle; currentInputs?: Record<string, string>;
  onClose: () => void; onDone: (installId: string) => void;
}) {
  const [bundle, setBundle] = useState<SolutionBundle | null>(null);
  const [validation, setValidation] = useState<SolutionValidation | null>(null);
  const [inputs, setInputs] = useState<Record<string, string>>({});
  const [secrets, setSecrets] = useState<Record<string, Record<string, string>>>({});
  const [force, setForce] = useState(false);
  // After a deploy: the webhook URLs it created. They hold a secret, so this is the one place
  // (besides each workspace's chat) they're shown.
  const [deployed, setDeployed] = useState<{ installId: string; hooks: { workspace: string; name: string; path: string }[] } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function pick(files: FileList | null) {
    if (!files || files.length === 0) return;
    try {
      await load(await bundleFromFolder(files));
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  async function load(b: SolutionBundle) {
    setError(null);
    setValidation(null);
    setBusy(true);
    try {
      setBundle(b);
      const v = await validateSolution(b);
      setValidation(v);
      const values: Record<string, string> = {};
      for (const i of v.summary?.inputs ?? []) values[i.key] = currentInputs?.[i.key] ?? i.default ?? "";
      setInputs(values);
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function deploy() {
    if (!bundle) return;
    setBusy(true);
    setError(null);
    try {
      const filled = Object.fromEntries(Object.entries(inputs).filter(([, v]) => v.trim() !== ""));
      const result = await deploySolution({ installId, bundle, packageRef, inputs: filled, secrets, force });
      const id = String(result.install_id ?? installId);
      type Report = { name?: string; key?: string; triggers?: { name: string; webhook_path?: string | null }[] };
      const hooks = ((result.workspaces as Report[] | undefined) ?? []).flatMap((w) =>
        (w.triggers ?? []).filter((t) => t.webhook_path).map((t) => ({ workspace: w.name ?? w.key ?? "", name: t.name, path: t.webhook_path! })));
      if (hooks.length > 0) setDeployed({ installId: id, hooks });
      else onDone(id);
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  // From the registry: the server fetches the package (checking its signature); it's validated
  // here like a folder, then deployed by reference.
  const loadRef = useRef(load);
  loadRef.current = load;
  useEffect(() => {
    if (initialBundle) loadRef.current(initialBundle);
  }, [initialBundle]);

  useEffect(() => {
    if (!packageRef) return;
    const [name, version] = packageRef.split("@");
    getRegistryPackage(name, version ?? "latest").then((p) => loadRef.current(p.bundle)).catch((e) => setError(apiErrorMessage(e)));
  }, [packageRef]);

  const needed = Object.entries(validation?.secrets ?? {}).filter(([, list]) => list.length > 0);
  return (
    <Modal
      open
      wide
      onClose={onClose}
      title={packageRef ? `${installId ? "Upgrade to" : "Install"} ${packageRef}`
        : initialBundle ? (installId ? "Upgrade to the version you built" : "Install the solution you built")
        : installId ? "Upgrade from a package folder" : "Deploy a package folder"}
      description={packageRef ? "From the registry. It's checked against this server before anything changes."
        : initialBundle ? "It's checked against this server before anything changes."
        : "Pick the folder that holds aktor.yaml. It's checked against this server before anything changes."}
      footer={
        deployed ? (
          <Button variant="primary" onClick={() => onDone(deployed.installId)}>Done</Button>
        ) : (
          <>
            <Button onClick={onClose}>Cancel</Button>
            <Button variant="primary" disabled={busy || !validation?.ok} onClick={deploy}>
              {busy && validation ? "Deploying…" : installId ? "Upgrade" : "Install"}
            </Button>
          </>
        )
      }
    >
      <div className="space-y-4 text-sm">
        {!packageRef && !initialBundle && <label className="flex cursor-pointer items-center justify-center gap-2 rounded-xl border border-dashed border-zinc-300 px-4 py-6 text-zinc-600 hover:border-brand-400 hover:text-brand-600 dark:border-zinc-700 dark:text-zinc-400">
          <Icons.Upload className="h-4 w-4" />
          {bundle ? `${Object.keys(bundle.files).length + 1} files read — pick another folder` : "Choose the package folder"}
          <input type="file" className="hidden" multiple ref={(el) => { el?.setAttribute("webkitdirectory", ""); }} onChange={(e) => pick(e.target.files)} />
        </label>}
        <ErrorBanner error={error} onClose={() => setError(null)} />
        {deployed && (
          <div className="space-y-3 rounded-xl border border-emerald-200 bg-emerald-50/60 p-4 dark:border-emerald-900/60 dark:bg-emerald-950/30">
            <div className="font-medium text-emerald-800 dark:text-emerald-300">Deployed. Its webhooks are ready:</div>
            <p className="text-xs text-zinc-600 dark:text-zinc-400">
              Point the services that send these events at them. Each URL holds a secret, so keep it private. It&apos;s shown
              here and in the workspace&apos;s chat, nowhere else.
            </p>
            {deployed.hooks.map((h) => <CopyField key={h.path} label={`${h.workspace}: ${h.name}`} value={`${API_BASE}${h.path}`} />)}
          </div>
        )}
        {busy && !validation && <div className="text-zinc-500">Checking…</div>}

        {validation?.summary && (
          <div className="rounded-xl border border-zinc-200 p-4 dark:border-zinc-800">
            <div className="font-medium text-zinc-900 dark:text-zinc-100">{validation.summary.title} <span className="font-mono text-xs text-zinc-500">{validation.summary.version}</span></div>
            <div className="mt-2 flex flex-wrap gap-1.5">
              {validation.summary.workspaces.map((w) => <Badge key={w.key}>{w.name}</Badge>)}
              {validation.summary.entities.map((e) => <Badge key={e.name} tone="brand">{e.name}</Badge>)}
              {validation.summary.assistant && <Badge tone="green">{validation.summary.assistant}</Badge>}
              {validation.summary.evals > 0 && <Badge>{validation.summary.evals} evals</Badge>}
            </div>
          </div>
        )}

        {validation && !validation.ok && (
          <div className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-rose-700 dark:border-rose-900/60 dark:bg-rose-950/40 dark:text-rose-300">
            <div className="mb-1 font-medium">{validation.errors.length} problem(s) to fix first</div>
            <ul className="list-disc space-y-0.5 pl-5 text-xs">{validation.errors.map((e) => <li key={e}>{e}</li>)}</ul>
          </div>
        )}
        {validation && validation.warnings.length > 0 && (
          <ul className="list-disc space-y-0.5 pl-5 text-xs text-amber-700 dark:text-amber-400">{validation.warnings.map((w) => <li key={w}>{w}</li>)}</ul>
        )}

        {validation?.ok && (validation.summary?.inputs.length ?? 0) > 0 && (
          <div className="grid gap-3 md:grid-cols-2">
            {validation.summary!.inputs.map((i) => (
              <Field key={i.key} label={`${i.label}${i.required ? " *" : ""}`} hint={i.description ?? undefined}>
                <input className={inputClass} value={inputs[i.key] ?? ""} onChange={(e) => setInputs({ ...inputs, [i.key]: e.target.value })} />
              </Field>
            ))}
          </div>
        )}

        {validation?.ok && needed.length > 0 && (
          <div className="space-y-3">
            <div className="text-xs text-zinc-500">Connector secrets go straight to the vault{installId ? "; leave them empty for connectors already set up" : ""}.</div>
            <div className="grid gap-3 md:grid-cols-2">
              {needed.flatMap(([connector, fields]) => fields.map((f) => (
                <Field key={`${connector}.${f.key}`} label={`${connector}: ${f.label}${f.required && !installId ? " *" : ""}`} hint={f.description ?? undefined}>
                  <input type="password" autoComplete="off" className={inputClass} value={secrets[connector]?.[f.key] ?? ""}
                    onChange={(e) => setSecrets({ ...secrets, [connector]: { ...secrets[connector], [f.key]: e.target.value } })} />
                </Field>
              )))}
            </div>
          </div>
        )}

        {installId && validation?.ok && (
          <Toggle checked={force} onChange={setForce} label="Redeploy even if the version didn't change" description="For development. Upgrades normally need a higher version." />
        )}
      </div>
    </Modal>
  );
}
