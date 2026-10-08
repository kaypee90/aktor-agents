"use client";

import { useEffect, useMemo, useState } from "react";
import {
  apiErrorMessage,
  createModelProfile,
  deleteModelProfile,
  getLlmSettings,
  listLlmModels,
  listLlmProviders,
  resetLlmSettings,
  setAgentsMayChoose,
  setDefaultModel,
  testLlmSettings,
  updateModelProfile,
  type ListedModel,
  type LlmProviderInfo,
  type LlmSettingsView,
  type ModelProfile,
  type ModelProfileInput,
} from "@/lib/api";
import { Badge, Button, Card, CardHeader, ErrorBanner, Field, Modal, Toggle, ago, cx, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

/** Prices are entered per million tokens, as providers publish them, and stored per token. */
const PER_MILLION = 1_000_000;
const toPerToken = (perMillion: string) => (perMillion.trim() === "" ? null : Number(perMillion) / PER_MILLION);
const toPerMillion = (perToken: number | null | undefined) =>
  perToken === null || perToken === undefined ? "" : String(Math.round(perToken * PER_MILLION * 10_000) / 10_000);
const money = (n: number) => (n === 0 ? "free" : `$${n.toFixed(2).replace(/\.00$/, "")}`);
/** A per-million price as providers write it: "$2", "$2.50", "$0.075". */
const listPrice = (n: number) => `$${n.toFixed(4).replace(/0{1,2}$/, "").replace(/\.00$/, "")}`;

/** The list price of a model, as the server finds it: snapshots ("gpt-4o-2024-08-06") are priced as
 * their model. None at a custom base URL, which is another service with its own prices. */
function findListed(provider: LlmProviderInfo | undefined, model: string, baseUrl: string): ListedModel | undefined {
  if (!provider || baseUrl.trim() || !model.trim()) return undefined;
  const id = model.trim().toLowerCase().replace(/^models\//, "");
  const lookup = (m: string) => provider.models.find((l) => l.id.toLowerCase() === m);
  return lookup(id) ?? lookup(id.replace(/-(\d{4}-\d{2}-\d{2}|\d{8}|latest)$/, ""));
}

/** Picking "Custom model…" in a model dropdown. */
const CUSTOM = "__custom__";

type Draft = {
  id: string | null;
  name: string;
  description: string;
  provider: string;
  model: string;
  fastModel: string;
  baseUrl: string;
  apiKey: string;
  priceIn: string;
  priceOut: string;
  fastPriceIn: string;
  fastPriceOut: string;
  makeDefault: boolean;
};

function draftOf(p: ModelProfile): Draft {
  return {
    id: p.id,
    name: p.name,
    description: p.description ?? "",
    provider: p.provider,
    model: p.model,
    fastModel: p.fast_model ?? "",
    baseUrl: p.base_url ?? "",
    apiKey: "",
    priceIn: toPerMillion(p.price_per_input_token_usd),
    priceOut: toPerMillion(p.price_per_output_token_usd),
    fastPriceIn: toPerMillion(p.fast_price_per_input_token_usd),
    fastPriceOut: toPerMillion(p.fast_price_per_output_token_usd),
    makeDefault: p.is_default,
  };
}

function newDraft(provider: LlmProviderInfo | undefined, first: boolean): Draft {
  return {
    id: null, name: "", description: "", provider: provider?.id ?? "Anthropic", model: provider?.suggested_models[0] ?? "", fastModel: "",
    baseUrl: "", apiKey: "", priceIn: "", priceOut: "", fastPriceIn: "", fastPriceOut: "", makeDefault: first,
  };
}

function inputOf(d: Draft): ModelProfileInput {
  return {
    id: d.id,
    name: d.name.trim() || null,
    description: d.description.trim() || null,
    provider: d.provider,
    model: d.model.trim() || null,
    fast_model: d.fastModel.trim() || null,
    base_url: d.baseUrl.trim() || null,
    api_key: d.apiKey.trim() || null,
    price_per_input_token_usd: toPerToken(d.priceIn),
    price_per_output_token_usd: toPerToken(d.priceOut),
    fast_price_per_input_token_usd: toPerToken(d.fastPriceIn),
    fast_price_per_output_token_usd: toPerToken(d.fastPriceOut),
    make_default: d.makeDefault,
  };
}

const needsKey = (providers: LlmProviderInfo[], id: string) => providers.find((p) => p.id === id)?.needs_api_key ?? false;

/**
 * The organization's models (docs/llm-settings.md): the server's default plus any number of named
 * models, each with its own provider, key and prices. Tasks pick one when they start and can switch
 * while they run; the default is used when they don't. Everyone sees the list; Admins change it.
 */
export function ModelSettings({ canEdit }: { canEdit: boolean }) {
  const [view, setView] = useState<LlmSettingsView | null>(null);
  const [providers, setProviders] = useState<LlmProviderInfo[]>([]);
  const [editing, setEditing] = useState<Draft | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([getLlmSettings(), listLlmProviders()])
      .then(([v, p]) => { setView(v); setProviders(p); })
      .catch((e) => setError(apiErrorMessage(e)));
  }, []);

  if (!view) return error ? <ErrorBanner error={error} /> : <div className="h-40 animate-pulse rounded-xl bg-zinc-100 dark:bg-zinc-900" />;
  const editable = canEdit && view.allow_organization_settings;

  async function act(key: string, run: () => Promise<LlmSettingsView>) {
    setBusy(key);
    setError(null);
    try {
      setView(await run());
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  const rows = [
    {
      id: "server", name: "Server default", description: "Set by whoever runs this server (LLM_PROVIDER, LLM_MODEL).",
      provider: view.server.provider, model: view.server.model, fast: view.server.fast_model,
      inM: view.server.price_per_million_input_usd, outM: view.server.price_per_million_output_usd, isDefault: view.server.is_default,
      key: undefined as string | undefined, profile: undefined as ModelProfile | undefined, unpriced: false,
    },
    ...view.profiles.map((p) => ({
      id: p.id, name: p.name, description: p.description, provider: p.provider, model: p.model, fast: p.fast_model,
      inM: p.price_per_million_input_usd, outM: p.price_per_million_output_usd, isDefault: p.is_default, profile: p,
      unpriced: p.price_source === "server",
      key: needsKey(providers, p.provider) ? (p.api_key_set ? "key saved" : p.uses_server_key ? "server's key" : "no key") : undefined,
    })),
  ];

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader
          title="Models"
          description="Set up the models your agents can use, e.g. a strong one for hard work and a cheap or local one for routine tasks. Pick one per task when you start it and switch while it runs; the default is used otherwise."
          actions={editable && (
            <Button variant="primary" size="sm" icon={<Icons.Plus className="h-3.5 w-3.5" />}
              onClick={() => setEditing(newDraft(providers[0], view.profiles.length === 0))}>
              Add model
            </Button>
          )}
        />
        {error && <div className="px-5 pt-4"><ErrorBanner error={error} onClose={() => setError(null)} /></div>}
        <ul className="divide-y divide-zinc-100 dark:divide-zinc-800">
          {rows.map((r) => (
            <li key={r.id} className="flex flex-wrap items-center gap-x-6 gap-y-2 px-5 py-4">
              <div className="min-w-56 flex-1">
                <div className="flex items-center gap-2">
                  <span className="font-medium text-zinc-900 dark:text-zinc-100">{r.name}</span>
                  {r.isDefault && <Badge tone="brand">Default</Badge>}
                  {r.key && <Badge tone={r.key === "no key" ? "amber" : "neutral"}>{r.key}</Badge>}
                </div>
                <div className="mt-0.5 truncate text-xs text-zinc-500">
                  {r.provider === "Mock" ? "Mock · scripted demo" : <>{r.provider} · <span className="font-mono">{r.model}</span></>}
                  {r.fast && <> · routine: <span className="font-mono">{r.fast}</span></>}
                </div>
                {r.description && <div className="mt-0.5 text-xs text-zinc-400">{r.description}</div>}
              </div>
              <div className="w-40 text-xs text-zinc-500">
                <div className="tabular-nums text-zinc-800 dark:text-zinc-200">{money(r.inM)} in · {money(r.outM)} out</div>
                <div>per million tokens</div>
                {r.unpriced && <div className="text-amber-700 dark:text-amber-400">Not priced: using the server&apos;s. Edit to set its price.</div>}
              </div>
              {editable && (
                <div className="flex items-center gap-1">
                  {!r.isDefault && (
                    <Button size="sm" variant="ghost" disabled={busy !== null} onClick={() => act(`default:${r.id}`, () => setDefaultModel(r.id))}>
                      {busy === `default:${r.id}` ? "…" : "Make default"}
                    </Button>
                  )}
                  {r.profile && (
                    <>
                      <Button size="sm" variant="ghost" onClick={() => setEditing(draftOf(r.profile!))}>Edit</Button>
                      <Button size="sm" variant="ghost" disabled={busy !== null}
                        onClick={() => confirm(`Delete "${r.name}" and its saved key? Running tasks on it continue on the default.`) && act(`delete:${r.id}`, () => deleteModelProfile(r.id))}>
                        Delete
                      </Button>
                    </>
                  )}
                </div>
              )}
            </li>
          ))}
        </ul>
        {editable && view.profiles.length > 0 && (
          <div className="flex items-center justify-between border-t border-zinc-100 px-5 py-3 text-xs text-zinc-500 dark:border-zinc-800">
            <span>Last change {ago(view.profiles.map((p) => p.updated_at).sort().at(-1))}</span>
            <Button size="sm" variant="ghost" disabled={busy !== null}
              onClick={() => confirm("Delete every model and saved key, and go back to the server's model?") && act("reset", resetLlmSettings)}>
              Remove all models
            </Button>
          </div>
        )}
      </Card>

      {view.profiles.length > 0 && (
        <Card className="p-5">
          <Toggle checked={view.agents_may_choose} disabled={!editable || busy !== null}
            onChange={(v) => act("choice", () => setAgentsMayChoose(v))}
            label="Let agents choose models"
            description="Agents see these models, with their descriptions and prices, and can give one to an agent they spawn: when your goal says so (“use Quick for data collection”) or when the work suits a cheaper or stronger model. Each spawned agent's cost still comes out of its parent's budget. Off: every agent runs on the task's model." />
        </Card>
      )}

      {!view.allow_organization_settings && (
        <Card className="p-5 text-sm text-zinc-600 dark:text-zinc-400">
          This server sets the model for every organization. Ask its operator to change <code className="font-mono text-xs">LLM_PROVIDER</code> and <code className="font-mono text-xs">LLM_MODEL</code>.
        </Card>
      )}
      {view.allow_organization_settings && !canEdit && (
        <Card className="p-5 text-sm text-zinc-600 dark:text-zinc-400">Only Admins can add or change models. You can pick any of them when you start a task.</Card>
      )}

      {editing && (
        <ModelEditor initial={editing} providers={providers} view={view} onClose={() => setEditing(null)}
          onSaved={(v) => { setView(v); setEditing(null); }} />
      )}
    </div>
  );
}

/** Add or edit one model: provider, key, model id (listed from the provider), prices, then test. */
function ModelEditor({ initial, providers, view, onClose, onSaved }: {
  initial: Draft; providers: LlmProviderInfo[]; view: LlmSettingsView; onClose: () => void; onSaved: (v: LlmSettingsView) => void;
}) {
  const [draft, setDraft] = useState(initial);
  const [models, setModels] = useState<Record<string, string[]>>({});
  const [loadingModels, setLoadingModels] = useState(false);
  const [test, setTest] = useState<{ ok: boolean; message: string; latency_ms?: number } | null>(null);
  const [busy, setBusy] = useState<"save" | "test" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [advanced, setAdvanced] = useState(Boolean(initial.baseUrl));
  const initialProvider = providers.find((p) => p.id === initial.provider);
  const known = (m: string) => !m.trim() || Boolean(initialProvider?.suggested_models.some((s) => s.toLowerCase() === m.trim().toLowerCase()))
    || Boolean(findListed(initialProvider, m, ""));
  // A model id typed in rather than picked from the list.
  const [custom, setCustom] = useState({ model: !known(initial.model), fast: !known(initial.fastModel) });
  // Prices entered for a model that has a list price.
  const [override, setOverride] = useState({ model: initial.priceIn !== "" || initial.priceOut !== "", fast: initial.fastPriceIn !== "" || initial.fastPriceOut !== "" });

  const provider = providers.find((p) => p.id === draft.provider);
  const saved = initial.id ? view.profiles.find((p) => p.id === initial.id) : undefined;
  // A saved key is only reused for the provider and address it was saved with.
  const sameEndpoint = Boolean(saved && saved.provider === draft.provider && (saved.base_url ?? "") === draft.baseUrl.trim());
  const keyOnFile = Boolean(saved?.api_key_set && sameEndpoint);
  const serverKeyUsable = draft.provider === view.server.provider && !draft.baseUrl.trim();
  // Models the provider's account serves that aren't on the list (no price known), for the dropdown.
  const extra = useMemo(() => {
    const listedIds = new Set((provider?.models.length ? provider.models.map((m) => m.id) : provider?.suggested_models ?? []).map((m) => m.toLowerCase()));
    return (models[draft.provider] ?? []).filter((m) => !listedIds.has(m.toLowerCase()));
  }, [models, draft.provider, provider]);
  const listedModel = findListed(provider, draft.model, draft.baseUrl);
  const listedFast = findListed(provider, draft.fastModel, draft.baseUrl);

  function change(patch: Partial<Draft>) {
    setDraft((d) => ({ ...d, ...patch }));
    setTest(null);
  }

  function pickProvider(p: LlmProviderInfo) {
    if (p.id === draft.provider) return;
    change({ provider: p.id, model: p.suggested_models[0] ?? "", fastModel: "", baseUrl: "", apiKey: "", priceIn: "", priceOut: "", fastPriceIn: "", fastPriceOut: "" });
    setAdvanced(false);
    setCustom({ model: false, fast: false });
    setOverride({ model: false, fast: false });
  }

  /** A model picked from the dropdown: a listed one is priced from the list again. */
  function pickModel(which: "model" | "fast", value: string) {
    const [modelKey, inKey, outKey] = which === "model" ? (["model", "priceIn", "priceOut"] as const) : (["fastModel", "fastPriceIn", "fastPriceOut"] as const);
    if (value === CUSTOM) {
      setCustom((c) => ({ ...c, [which]: true }));
      change({ [modelKey]: "", [inKey]: "", [outKey]: "" });
    } else {
      setCustom((c) => ({ ...c, [which]: false }));
      change({ [modelKey]: value, [inKey]: "", [outKey]: "" });
    }
    setOverride((o) => ({ ...o, [which]: false }));
  }

  /** Prices are required for a model without a list price, or when overriding one. */
  function missingPrice(): string | null {
    if (!provider || provider.free) return null;
    const checks = [
      { model: draft.model, listed: listedModel, over: override.model, inP: draft.priceIn, outP: draft.priceOut },
      { model: draft.fastModel, listed: listedFast, over: override.fast, inP: draft.fastPriceIn, outP: draft.fastPriceOut },
    ];
    const gap = checks.find((c) => c.model.trim() && (!c.listed || c.over) && (c.inP.trim() === "" || c.outP.trim() === ""));
    return gap ? `Enter the input and output price for ${gap.model.trim()} (USD per million tokens, from ${draft.provider}'s price list).` : null;
  }

  async function loadModels() {
    setLoadingModels(true);
    setError(null);
    try {
      const { models: list } = await listLlmModels(draft.provider, draft.baseUrl, draft.apiKey, sameEndpoint ? initial.id : null);
      setModels((m) => ({ ...m, [draft.provider]: list }));
      if (list.length === 0) setError(`${draft.provider} returned no models.`);
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setLoadingModels(false);
    }
  }

  async function run(kind: "save" | "test") {
    setBusy(kind);
    setError(null);
    try {
      if (kind === "test") {
        setTest(await testLlmSettings({ ...inputOf(draft), id: sameEndpoint ? initial.id : null }));
      } else {
        const gap = missingPrice();
        if (gap) { setError(gap); return; }
        onSaved(initial.id ? await updateModelProfile(initial.id, inputOf(draft)) : await createModelProfile({ ...inputOf(draft), id: null }));
      }
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  return (
    <Modal open onClose={onClose} wide
      title={initial.id ? `Edit "${initial.name}"` : "Add a model"}
      description="Pick a provider, add its API key, choose a model, then test it before saving."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button disabled={busy !== null} onClick={() => run("test")} icon={<Icons.Play className="h-3.5 w-3.5" />}>
            {busy === "test" ? "Testing…" : "Test connection"}
          </Button>
          <Button variant="primary" disabled={busy !== null} onClick={() => run("save")}>{busy === "save" ? "Saving…" : "Save"}</Button>
        </>
      }>
      <div className="space-y-5">
        <ErrorBanner error={error} onClose={() => setError(null)} />

        <div className="grid gap-2 sm:grid-cols-3">
          {providers.map((p) => (
            <button key={p.id} type="button" onClick={() => pickProvider(p)}
              className={cx("rounded-xl border p-3 text-left transition",
                draft.provider === p.id
                  ? "border-brand-500 bg-brand-50/60 ring-2 ring-brand-500/20 dark:bg-brand-950/30"
                  : "border-zinc-200 hover:border-zinc-300 dark:border-zinc-800 dark:hover:border-zinc-700")}>
              <div className="flex items-center justify-between gap-2">
                <span className="text-sm font-medium text-zinc-900 dark:text-zinc-100">{p.label}</span>
                {draft.provider === p.id && <Icons.Check className="h-4 w-4 text-brand-500" />}
              </div>
              <div className="mt-0.5 text-[11px] text-zinc-500">
                {p.id === "Mock" ? "Scripted demo answers." : p.free ? "Your own machine; free." : "API key; billed by the provider."}
              </div>
            </button>
          ))}
        </div>

        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Name" hint="What people see when they pick a model, e.g. “GPT-5 mini (cheap)”.">
            <input className={inputClass} value={draft.name} placeholder={draft.model || draft.provider} onChange={(e) => change({ name: e.target.value })} />
          </Field>
          <Field label="When to use it (optional)" hint="Shown next to the name.">
            <input className={inputClass} value={draft.description} placeholder="Best for research and long reports" onChange={(e) => change({ description: e.target.value })} />
          </Field>
        </div>

        {provider?.needs_api_key && (
          <Field label="API key" hint={
            <>
              {keyOnFile ? "A key is saved (encrypted). Leave blank to keep it."
                : saved?.api_key_set ? "The provider or address changed, so enter the key for it."
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
              <ModelSelect provider={provider} extra={extra} value={draft.model} custom={custom.model}
                onPick={(v) => pickModel("model", v)} onType={(v) => change({ model: v })} />
            </Field>
            <Field label="Fast model (optional)" hint="A cheaper model for routine work: standing agents handling events, history summaries.">
              <ModelSelect provider={provider} extra={extra} value={draft.fastModel} custom={custom.fast} optional
                onPick={(v) => pickModel("fast", v)} onType={(v) => change({ fastModel: v })} />
            </Field>
            <div className="flex items-center gap-3 sm:col-span-2">
              <Button size="sm" disabled={loadingModels} onClick={loadModels} icon={<Icons.Search className="h-3.5 w-3.5" />}>
                {loadingModels ? "Asking the provider…" : `Load ${draft.provider}'s models`}
              </Button>
              <span className="text-xs text-zinc-500">
                {models[draft.provider] ? `${models[draft.provider].length} models available; ones not on the price list are under "From your account".` : "Lists every model your key can use, including fine-tunes."}
              </span>
            </div>
          </div>
        )}

        {provider && !provider.free && draft.model.trim() && (
          <div className="space-y-3">
            <div className="text-xs font-medium text-zinc-700 dark:text-zinc-300">Prices (USD per million tokens)</div>
            <p className="-mt-2 text-xs text-zinc-500">Budgets, spend and cost estimates are counted with these.</p>
            <PriceRow label="Model" model={draft.model} listed={listedModel} asOf={provider.prices_as_of} provider={draft.provider}
              customUrl={Boolean(draft.baseUrl.trim())} overriding={override.model}
              values={[draft.priceIn, draft.priceOut]}
              onOverride={(on) => {
                setOverride((o) => ({ ...o, model: on }));
                change(on && listedModel ? { priceIn: String(listedModel.input_per_million_usd), priceOut: String(listedModel.output_per_million_usd) } : { priceIn: "", priceOut: "" });
              }}
              onChange={([priceIn, priceOut]) => change({ priceIn, priceOut })} />
            {draft.fastModel.trim() && (
              <PriceRow label="Fast model" model={draft.fastModel} listed={listedFast} asOf={provider.prices_as_of} provider={draft.provider}
                customUrl={Boolean(draft.baseUrl.trim())} overriding={override.fast}
                values={[draft.fastPriceIn, draft.fastPriceOut]}
                onOverride={(on) => {
                  setOverride((o) => ({ ...o, fast: on }));
                  change(on && listedFast ? { fastPriceIn: String(listedFast.input_per_million_usd), fastPriceOut: String(listedFast.output_per_million_usd) } : { fastPriceIn: "", fastPriceOut: "" });
                }}
                onChange={([fastPriceIn, fastPriceOut]) => change({ fastPriceIn, fastPriceOut })} />
            )}
          </div>
        )}

        {draft.provider !== "Ollama" && draft.provider !== "Mock" && !advanced && (
          <button type="button" className="text-xs text-zinc-500 hover:text-zinc-800 dark:hover:text-zinc-200" onClick={() => setAdvanced(true)}>
            + Custom base URL
          </button>
        )}

        <Toggle checked={draft.makeDefault} onChange={(v) => change({ makeDefault: v })} label="Use for new tasks by default"
          description="Tasks that don't pick a model run on the default." />

        {test && (
          <div className={cx("flex items-start gap-2 rounded-lg border px-3 py-2 text-sm",
            test.ok ? "border-emerald-200 bg-emerald-50 text-emerald-800 dark:border-emerald-900 dark:bg-emerald-950/40 dark:text-emerald-300"
              : "border-rose-200 bg-rose-50 text-rose-800 dark:border-rose-900 dark:bg-rose-950/40 dark:text-rose-300")}>
            {test.ok ? <Icons.Check className="mt-0.5 h-4 w-4 shrink-0" /> : <Icons.X className="mt-0.5 h-4 w-4 shrink-0" />}
            <span>{test.ok ? "Works. " : "Didn't work. "}{test.message}{test.latency_ms !== undefined && test.ok ? ` (${(test.latency_ms / 1000).toFixed(1)} s)` : ""}</span>
          </div>
        )}
      </div>
    </Modal>
  );
}

/** The model dropdown: the provider's listed models with their prices, models the account serves
 * that aren't listed, and "Custom model…" to type any other id. */
function ModelSelect({ provider, extra, value, custom, optional = false, onPick, onType }: {
  provider: LlmProviderInfo | undefined; extra: string[]; value: string; custom: boolean; optional?: boolean;
  onPick: (value: string) => void; onType: (value: string) => void;
}) {
  const listed = provider?.models ?? [];
  // Local providers have no price list: their well-known models are offered by id.
  const plain = listed.length === 0 ? provider?.suggested_models ?? [] : [];
  // A saved model picked from "From your account" before the list was loaded again.
  const unlisted = !custom && value.trim() && !findListed(provider, value, "") && !plain.includes(value) && !extra.includes(value);
  return (
    <div className="space-y-2">
      <select className={inputClass} value={custom ? CUSTOM : value} onChange={(e) => onPick(e.target.value)}>
        {optional ? <option value="">None (use the model)</option> : !value && !custom && <option value="" disabled>Choose a model</option>}
        {listed.length > 0 && (
          <optgroup label="On the price list">
            {listed.map((m) => (
              <option key={m.id} value={m.id}>{m.name} · {listPrice(m.input_per_million_usd)} in / {listPrice(m.output_per_million_usd)} out</option>
            ))}
          </optgroup>
        )}
        {plain.map((m) => <option key={m} value={m}>{m}</option>)}
        {(extra.length > 0 || unlisted) && (
          <optgroup label="From your account (enter prices)">
            {unlisted && <option value={value}>{value}</option>}
            {extra.map((m) => <option key={m} value={m}>{m}</option>)}
          </optgroup>
        )}
        <option value={CUSTOM}>Custom model…</option>
      </select>
      {custom && (
        <input autoFocus className={`${inputClass} font-mono`} value={value} onChange={(e) => onType(e.target.value)}
          placeholder="Model id, e.g. ft:gpt-5-mini:acme::abc123" />
      )}
    </div>
  );
}

/** One model's prices: its list price with a way to override it, or inputs when it isn't listed. */
function PriceRow({ label, model, listed, asOf, provider, customUrl, overriding, values, onOverride, onChange }: {
  label: string; model: string; listed: ListedModel | undefined; asOf: string | null; provider: string; customUrl: boolean;
  overriding: boolean; values: [string, string]; onOverride: (on: boolean) => void; onChange: (values: [string, string]) => void;
}) {
  const link = "text-brand-600 hover:underline dark:text-brand-400";
  if (listed && !overriding) {
    return (
      <div className="rounded-lg border border-zinc-200 px-3 py-2 text-sm dark:border-zinc-800">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <span>
            <span className="text-zinc-500">{label}: </span>
            <span className="tabular-nums text-zinc-900 dark:text-zinc-100">{listPrice(listed.input_per_million_usd)} in · {listPrice(listed.output_per_million_usd)} out</span>
            {listed.cached_input_per_million_usd !== null && <span className="text-zinc-500"> · cached input {listPrice(listed.cached_input_per_million_usd)}</span>}
          </span>
          <button type="button" className={`text-xs ${link}`} onClick={() => onOverride(true)}>Use a different price</button>
        </div>
        <div className="mt-0.5 text-xs text-zinc-500">
          {provider}&apos;s list price{asOf && <> as of {new Date(asOf + "T00:00:00").toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" })}</>}.
          {listed.note && <> {listed.note}</>}
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-1.5">
      <div className="flex flex-wrap items-baseline justify-between gap-2 text-xs">
        <span className="text-zinc-600 dark:text-zinc-400">
          {label} <span className="font-mono">{model.trim()}</span>:{" "}
          {listed ? "your price (e.g. a negotiated rate)."
            : customUrl ? "a custom base URL has its own prices, so enter that service's."
            : `not on ${provider}'s price list, so enter its price.`}
        </span>
        {listed && <button type="button" className={link} onClick={() => onOverride(false)}>Use the list price</button>}
      </div>
      <div className="grid grid-cols-2 gap-3">
        {(["Input", "Output"] as const).map((name, i) => (
          <Field key={name} label={`${name} per million`}>
            <input type="number" min={0} step="any" required className={inputClass} value={values[i]} placeholder="e.g. 2.50"
              onChange={(e) => onChange(i === 0 ? [e.target.value, values[1]] : [values[0], e.target.value])} />
          </Field>
        ))}
      </div>
    </div>
  );
}
