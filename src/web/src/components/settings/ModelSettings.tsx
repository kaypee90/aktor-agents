"use client";

import { useEffect, useMemo, useState } from "react";
import {
  apiErrorMessage,
  getLlmSettings,
  listLlmModels,
  listLlmProviders,
  resetLlmSettings,
  saveLlmSettings,
  testLlmSettings,
  type LlmProviderInfo,
  type LlmSettingsInput,
  type LlmSettingsView,
} from "@/lib/api";
import { Badge, Button, Card, CardHeader, ErrorBanner, Field, ago, cx, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

/** Prices are entered per million tokens, as providers publish them, and stored per token. */
const PER_MILLION = 1_000_000;
const toPerToken = (perMillion: string) => (perMillion.trim() === "" ? null : Number(perMillion) / PER_MILLION);
const toPerMillion = (perToken: number | null | undefined) =>
  perToken === null || perToken === undefined ? "" : String(Math.round(perToken * PER_MILLION * 10_000) / 10_000);

type Draft = {
  provider: string;
  model: string;
  fastModel: string;
  baseUrl: string;
  apiKey: string;
  priceIn: string;
  priceOut: string;
  fastPriceIn: string;
  fastPriceOut: string;
};

function draftFrom(view: LlmSettingsView): Draft {
  const org = view.organization;
  return {
    provider: org?.provider ?? view.server.provider,
    model: org?.model ?? view.server.model,
    fastModel: org?.fast_model ?? view.server.fast_model ?? "",
    baseUrl: org?.base_url ?? "",
    apiKey: "",
    priceIn: toPerMillion(org?.price_per_input_token_usd),
    priceOut: toPerMillion(org?.price_per_output_token_usd),
    fastPriceIn: toPerMillion(org?.fast_price_per_input_token_usd),
    fastPriceOut: toPerMillion(org?.fast_price_per_output_token_usd),
  };
}

function inputOf(d: Draft): LlmSettingsInput {
  return {
    provider: d.provider,
    model: d.model.trim() || null,
    fast_model: d.fastModel.trim() || null,
    base_url: d.baseUrl.trim() || null,
    api_key: d.apiKey.trim() || null,
    price_per_input_token_usd: toPerToken(d.priceIn),
    price_per_output_token_usd: toPerToken(d.priceOut),
    fast_price_per_input_token_usd: toPerToken(d.fastPriceIn),
    fast_price_per_output_token_usd: toPerToken(d.fastPriceOut),
  };
}

const money = (n: number) => (n === 0 ? "free" : `$${n < 1 ? n.toFixed(2) : n.toFixed(2).replace(/\.00$/, "")}`);

/**
 * Which model the organization's agents use (docs/llm-settings.md). Everyone sees what's in use;
 * Admins pick the provider and model, add a key, set prices, and test before saving.
 */
export function ModelSettings({ canEdit }: { canEdit: boolean }) {
  const [view, setView] = useState<LlmSettingsView | null>(null);
  const [providers, setProviders] = useState<LlmProviderInfo[]>([]);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [models, setModels] = useState<Record<string, string[]>>({});
  const [loadingModels, setLoadingModels] = useState(false);
  const [test, setTest] = useState<{ ok: boolean; message: string; latency_ms?: number } | null>(null);
  const [busy, setBusy] = useState<"save" | "test" | "reset" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [advanced, setAdvanced] = useState(false);

  useEffect(() => {
    Promise.all([getLlmSettings(), listLlmProviders()])
      .then(([v, p]) => {
        setView(v);
        setProviders(p);
        setDraft(draftFrom(v));
        setAdvanced(Boolean(v.organization?.base_url));
      })
      .catch((e) => setError(apiErrorMessage(e)));
  }, []);

  const provider = useMemo(() => providers.find((p) => p.id === draft?.provider), [providers, draft?.provider]);
  const suggestions = useMemo(
    () => Array.from(new Set([...(models[draft?.provider ?? ""] ?? []), ...(provider?.suggested_models ?? [])])),
    [models, draft?.provider, provider],
  );

  if (!view || !draft) {
    return error ? <ErrorBanner error={error} /> : <div className="h-40 animate-pulse rounded-xl bg-zinc-100 dark:bg-zinc-900" />;
  }

  const org = view.organization;
  const savedProvider = org?.provider ?? null;
  // A key saved for this provider, or the server's key standing in (same provider, same address).
  const keyOnFile = draft.provider === savedProvider && Boolean(org?.api_key_set);
  const serverKeyUsable = draft.provider === view.server.provider && !draft.baseUrl.trim();
  const editable = canEdit && view.allow_organization_settings;

  function change(patch: Partial<Draft>) {
    setDraft((d) => (d ? { ...d, ...patch } : d));
    setTest(null);
    setSaved(false);
  }

  function pickProvider(p: LlmProviderInfo) {
    if (p.id === draft?.provider) return;
    const back = p.id === savedProvider && org ? draftFrom(view!) : null;
    change(back ?? {
      provider: p.id,
      model: p.id === view!.server.provider ? view!.server.model : p.suggested_models[0] ?? "",
      fastModel: "",
      baseUrl: "",
      apiKey: "",
      priceIn: "", priceOut: "", fastPriceIn: "", fastPriceOut: "",
    });
    setAdvanced(Boolean(back?.baseUrl));
  }

  async function loadModels() {
    if (!draft) return;
    setLoadingModels(true);
    setError(null);
    try {
      const { models: list } = await listLlmModels(draft.provider, draft.baseUrl, draft.apiKey);
      setModels((m) => ({ ...m, [draft.provider]: list }));
      if (list.length === 0) setError(`${draft.provider} returned no models.`);
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setLoadingModels(false);
    }
  }

  async function run(kind: "save" | "test" | "reset") {
    if (!draft) return;
    setBusy(kind);
    setError(null);
    try {
      if (kind === "test") {
        setTest(await testLlmSettings(inputOf(draft)));
      } else {
        const next = kind === "save" ? await saveLlmSettings(inputOf(draft)) : await resetLlmSettings();
        setView(next);
        setDraft(draftFrom(next));
        setSaved(true);
      }
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  const eff = view.effective;
  return (
    <div className="space-y-6">
      <Card>
        <CardHeader
          title="Model in use"
          description="Every agent of your organization reasons with this model. Changes apply from each agent's next step, including running tasks."
          actions={view.source === "organization"
            ? <Badge tone="brand">Your organization&apos;s choice</Badge>
            : <Badge>Server default</Badge>}
        />
        <div className="grid gap-4 p-5 sm:grid-cols-3">
          <div>
            <div className="text-[11px] uppercase tracking-wide text-zinc-500">Provider</div>
            <div className="mt-1 font-medium text-zinc-900 dark:text-zinc-100">{eff.provider}</div>
          </div>
          <div className="min-w-0">
            <div className="text-[11px] uppercase tracking-wide text-zinc-500">Model</div>
            <div className="mt-1 truncate font-mono text-sm text-zinc-900 dark:text-zinc-100" title={eff.model}>
              {eff.provider === "Mock" ? "scripted demo" : eff.model || "—"}
            </div>
            {eff.fast_model && <div className="truncate font-mono text-xs text-zinc-500" title={eff.fast_model}>routine work: {eff.fast_model}</div>}
          </div>
          <div>
            <div className="text-[11px] uppercase tracking-wide text-zinc-500">Budgets count</div>
            <div className="mt-1 text-sm text-zinc-900 dark:text-zinc-100">
              {money(eff.price_per_million_input_usd)} in · {money(eff.price_per_million_output_usd)} out
            </div>
            <div className="text-xs text-zinc-500">per million tokens</div>
          </div>
        </div>
        {org && (
          <div className="flex items-center justify-between border-t border-zinc-100 px-5 py-3 text-xs text-zinc-500 dark:border-zinc-800">
            <span>Changed {ago(org.updated_at)}{org.updated_by ? ` by ${org.updated_by}` : ""}</span>
            {editable && (
              <Button size="sm" variant="ghost" disabled={busy !== null}
                onClick={() => confirm("Go back to the server's model? Your organization's saved API keys are deleted.") && run("reset")}>
                {busy === "reset" ? "Resetting…" : "Use server default"}
              </Button>
            )}
          </div>
        )}
      </Card>

      {!view.allow_organization_settings && (
        <Card className="p-5 text-sm text-zinc-600 dark:text-zinc-400">
          This server sets the model for every organization. Ask its operator to change <code className="font-mono text-xs">LLM_PROVIDER</code> and <code className="font-mono text-xs">LLM_MODEL</code>.
        </Card>
      )}

      {view.allow_organization_settings && !canEdit && (
        <Card className="p-5 text-sm text-zinc-600 dark:text-zinc-400">Only Admins can change the model.</Card>
      )}

      {editable && (
        <Card>
          <CardHeader title="Choose a model" description="Pick a provider, add its API key, choose a model, then test it before saving." />
          <div className="space-y-6 p-5">
            <ErrorBanner error={error} onClose={() => setError(null)} />

            <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
              {providers.map((p) => (
                <button key={p.id} type="button" onClick={() => pickProvider(p)}
                  className={cx(
                    "rounded-xl border p-3 text-left transition",
                    draft.provider === p.id
                      ? "border-brand-500 bg-brand-50/60 ring-2 ring-brand-500/20 dark:bg-brand-950/30"
                      : "border-zinc-200 hover:border-zinc-300 dark:border-zinc-800 dark:hover:border-zinc-700",
                  )}>
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-sm font-medium text-zinc-900 dark:text-zinc-100">{p.label}</span>
                    {draft.provider === p.id && <Icons.Check className="h-4 w-4 text-brand-500" />}
                  </div>
                  <div className="mt-1 text-xs text-zinc-500">
                    {p.id === "Mock" ? "Scripted demo answers, for trying the platform."
                      : p.free ? "Runs on your own machine; free per token."
                      : "Needs an API key; billed by the provider."}
                  </div>
                </button>
              ))}
            </div>

            {provider?.needs_api_key && (
              <Field label="API key" hint={
                <>
                  {keyOnFile ? "A key is saved (encrypted). Leave blank to keep it."
                    : serverKeyUsable ? "Leave blank to use the server's key for this provider."
                    : "Stored encrypted. Agents never see it."}
                  {provider.get_key_url && <> <a href={provider.get_key_url} target="_blank" rel="noreferrer" className="text-brand-600 hover:underline dark:text-brand-400">Get a key</a></>}
                </>
              }>
                <input type="password" autoComplete="off" className={`${inputClass} font-mono`} value={draft.apiKey}
                  placeholder={keyOnFile ? "•••••••• saved" : serverKeyUsable ? "Using the server's key" : "Paste your key"}
                  onChange={(e) => change({ apiKey: e.target.value })} />
              </Field>
            )}

            {(draft.provider === "Ollama" || advanced) && (
              <Field label={draft.provider === "Ollama" ? "Ollama address" : "Base URL"}
                hint={draft.provider === "OpenAI"
                  ? "For OpenAI-compatible services (OpenRouter, Groq, Together, vLLM, LM Studio). Blank uses OpenAI."
                  : `Blank uses ${provider?.default_base_url ?? "the provider's default"}.`}>
                <input className={`${inputClass} font-mono`} value={draft.baseUrl} placeholder={provider?.default_base_url ?? ""}
                  onChange={(e) => change({ baseUrl: e.target.value })} />
              </Field>
            )}

            {draft.provider !== "Mock" && (
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="Model" hint="Planning and real work use this model.">
                  <input list="llm-models" className={`${inputClass} font-mono`} value={draft.model}
                    onChange={(e) => change({ model: e.target.value })} placeholder={provider?.suggested_models[0]} />
                </Field>
                <Field label="Fast model (optional)" hint="A cheaper model for routine work: standing agents handling events, and history summaries.">
                  <input list="llm-models" className={`${inputClass} font-mono`} value={draft.fastModel}
                    onChange={(e) => change({ fastModel: e.target.value })} placeholder="Same as the model" />
                </Field>
                <datalist id="llm-models">{suggestions.map((m) => <option key={m} value={m} />)}</datalist>
                <div className="flex items-center gap-3 sm:col-span-2">
                  <Button size="sm" disabled={loadingModels} onClick={loadModels} icon={<Icons.Search className="h-3.5 w-3.5" />}>
                    {loadingModels ? "Asking the provider…" : `Load ${draft.provider}'s models`}
                  </Button>
                  <span className="text-xs text-zinc-500">
                    {models[draft.provider] ? `${models[draft.provider].length} models available; type to filter.` : "Or type any model id the provider serves."}
                  </span>
                </div>
              </div>
            )}

            {provider && !provider.free && (
              <div className="space-y-2">
                <div className="text-xs font-medium text-zinc-700 dark:text-zinc-300">Prices (USD per million tokens)</div>
                <p className="text-xs text-zinc-500">
                  Budgets, spend and cost estimates are counted with these, so copy them from {draft.provider}&apos;s price list for the model.
                  Blank uses the server&apos;s prices ({money(view.effective.price_per_million_input_usd)} in, {money(view.effective.price_per_million_output_usd)} out).
                </p>
                <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
                  {([["priceIn", "Input"], ["priceOut", "Output"], ["fastPriceIn", "Fast input"], ["fastPriceOut", "Fast output"]] as const).map(([k, label]) => (
                    <Field key={k} label={label}>
                      <input type="number" min={0} step="any" className={inputClass} value={draft[k]}
                        disabled={k.startsWith("fast") && !draft.fastModel.trim()}
                        onChange={(e) => change({ [k]: e.target.value })} placeholder="—" />
                    </Field>
                  ))}
                </div>
              </div>
            )}

            {draft.provider !== "Ollama" && draft.provider !== "Mock" && !advanced && (
              <button type="button" className="text-xs text-zinc-500 hover:text-zinc-800 dark:hover:text-zinc-200" onClick={() => setAdvanced(true)}>
                + Custom base URL
              </button>
            )}

            {test && (
              <div className={cx("flex items-start gap-2 rounded-lg border px-3 py-2 text-sm",
                test.ok ? "border-emerald-200 bg-emerald-50 text-emerald-800 dark:border-emerald-900 dark:bg-emerald-950/40 dark:text-emerald-300"
                  : "border-rose-200 bg-rose-50 text-rose-800 dark:border-rose-900 dark:bg-rose-950/40 dark:text-rose-300")}>
                {test.ok ? <Icons.Check className="mt-0.5 h-4 w-4 shrink-0" /> : <Icons.X className="mt-0.5 h-4 w-4 shrink-0" />}
                <span>{test.ok ? "Works. " : "Didn't work. "}{test.message}{test.latency_ms !== undefined && test.ok ? ` (${(test.latency_ms / 1000).toFixed(1)} s)` : ""}</span>
              </div>
            )}

            <div className="flex flex-wrap items-center justify-end gap-2 border-t border-zinc-100 pt-4 dark:border-zinc-800">
              {saved && <span className="mr-auto text-xs text-emerald-600 dark:text-emerald-400">Saved. Agents use it from their next step.</span>}
              <Button disabled={busy !== null} onClick={() => run("test")} icon={<Icons.Play className="h-3.5 w-3.5" />}>
                {busy === "test" ? "Testing…" : "Test connection"}
              </Button>
              <Button variant="primary" disabled={busy !== null} onClick={() => run("save")}>
                {busy === "save" ? "Saving…" : "Save"}
              </Button>
            </div>
          </div>
        </Card>
      )}
    </div>
  );
}
