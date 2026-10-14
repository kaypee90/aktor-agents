"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";
import { Button, cx, inputClass } from "@/components/ui";
import { apiErrorMessage, apiFieldErrors, getForm, submitForm } from "@/lib/api";
import type { FormField, FormView } from "@/lib/workspaceTypes";

type Answer = string | boolean | string[];

/**
 * A workspace's form (a form trigger), for signed-in members of its organization: each
 * submission starts a run of the workspace's pipeline with the answers as its input.
 * /forms?workspace=ws-…&trigger=trg-…
 */
function FormPage() {
  const params = useSearchParams();
  const workspaceId = params.get("workspace") ?? "";
  const triggerId = params.get("trigger") ?? "";
  const [view, setView] = useState<FormView | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [answers, setAnswers] = useState<Record<string, Answer>>({});
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<{ message: string; runId: string | null; number: number } | null>(null);
  // One id per filled-in form, so a double click or a retried request starts one run.
  const [submissionId, setSubmissionId] = useState(() => crypto.randomUUID());

  useEffect(() => {
    if (!workspaceId || !triggerId) return;
    let live = true;
    getForm(workspaceId, triggerId)
      .then((v) => { if (live) setView(v); })
      .catch((e) => { if (live) setLoadError(apiErrorMessage(e).includes("404") ? "This form doesn't exist, or isn't shared with your organization." : apiErrorMessage(e)); });
    return () => { live = false; };
  }, [workspaceId, triggerId]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!view) return;
    setBusy(true);
    setError(null);
    setFieldErrors({});
    try {
      const result = await submitForm(workspaceId, triggerId, answers, submissionId);
      setDone({ message: result.message, runId: result.run_id, number: result.number });
    } catch (err) {
      const fields = apiFieldErrors(err);
      setFieldErrors(fields);
      setError(Object.keys(fields).length > 0 ? "Some answers need fixing." : apiErrorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  function another() {
    setAnswers({});
    setDone(null);
    setSubmissionId(crypto.randomUUID());
  }

  if (!workspaceId || !triggerId) return <Centered><p className="text-sm text-zinc-500">This link is missing the form&apos;s workspace or id.</p></Centered>;
  if (loadError) return <Centered><p className="text-sm text-rose-600 dark:text-rose-400">{loadError}</p></Centered>;
  if (!view) return <Centered><p className="text-sm text-zinc-500">Loading the form…</p></Centered>;

  const { form } = view;
  return (
    <Centered>
      <div className="w-full rounded-2xl border border-zinc-200 bg-white p-6 shadow-sm dark:border-zinc-800 dark:bg-zinc-900">
        <div className="text-xs uppercase tracking-wide text-zinc-500">{view.workspace_name}</div>
        <h1 className="mt-1 text-xl font-semibold">{form.title || view.name}</h1>
        {form.description && <p className="mt-2 whitespace-pre-wrap text-sm text-zinc-600 dark:text-zinc-400">{form.description}</p>}

        {done ? (
          <div className="mt-6 space-y-4">
            <div className="rounded-lg bg-emerald-50 p-4 text-sm text-emerald-900 dark:bg-emerald-950/50 dark:text-emerald-100">{done.message}</div>
            <div className="flex flex-wrap gap-2">
              <Button variant="primary" onClick={another}>Submit another</Button>
              {done.runId && (
                <Link href={`/?task=${encodeURIComponent(done.runId)}`}
                  className="inline-flex items-center rounded-lg border border-zinc-200 px-3 py-1.5 text-sm hover:border-zinc-300 dark:border-zinc-700">
                  Follow run #{done.number}
                </Link>
              )}
            </div>
          </div>
        ) : !view.accepting ? (
          <p className="mt-6 rounded-lg bg-amber-50 p-4 text-sm text-amber-900 dark:bg-amber-950/50 dark:text-amber-100">
            This form isn&apos;t taking submissions right now (its workspace is paused). Try again later.
          </p>
        ) : (
          <form onSubmit={submit} className="mt-6 space-y-5" noValidate>
            {form.fields.map((f) => (
              <Question key={f.key} field={f} value={answers[f.key]} error={fieldErrors[f.key]}
                onChange={(v) => { setAnswers((a) => ({ ...a, [f.key]: v })); setFieldErrors((errs) => { const next = { ...errs }; delete next[f.key]; return next; }); }} />
            ))}
            {error && <p className="text-sm text-rose-600 dark:text-rose-400">{error}</p>}
            <Button type="submit" variant="primary" disabled={busy}>{busy ? "Sending…" : form.submit_label || "Submit"}</Button>
          </form>
        )}
      </div>
    </Centered>
  );
}

function Question({ field: f, value, error, onChange }: { field: FormField; value: Answer | undefined; error?: string; onChange: (v: Answer) => void }) {
  const id = `q-${f.key}`;
  const label = (
    <label htmlFor={id} className="block text-sm font-medium">
      {f.label}{f.required && <span className="text-rose-500"> *</span>}
    </label>
  );
  const help = f.help && <p className="text-xs text-zinc-500">{f.help}</p>;
  const invalid = cx(inputClass, error && "border-rose-400 focus:border-rose-500");

  let control: React.ReactNode;
  switch (f.type) {
    case "textarea":
      control = <textarea id={id} rows={4} className={cx(invalid, "resize-y")} value={(value as string) ?? ""} placeholder={f.placeholder ?? ""} onChange={(e) => onChange(e.target.value)} />;
      break;
    case "select":
      control = (
        <select id={id} className={invalid} value={(value as string) ?? ""} onChange={(e) => onChange(e.target.value)}>
          <option value="">Choose…</option>
          {f.options.map((o) => <option key={o} value={o}>{o}</option>)}
        </select>
      );
      break;
    case "multiselect": {
      const chosen = (value as string[]) ?? [];
      control = (
        <div id={id} className="flex flex-wrap gap-1.5">
          {f.options.map((o) => {
            const on = chosen.includes(o);
            return (
              <button type="button" key={o} aria-pressed={on} onClick={() => onChange(on ? chosen.filter((c) => c !== o) : [...chosen, o])}
                className={cx("rounded-full border px-3 py-1 text-sm transition",
                  on ? "border-brand-400 bg-brand-50 text-brand-700 dark:border-brand-600 dark:bg-brand-500/10 dark:text-brand-300" : "border-zinc-200 text-zinc-600 hover:border-zinc-300 dark:border-zinc-700 dark:text-zinc-400")}>
                {o}
              </button>
            );
          })}
        </div>
      );
      break;
    }
    case "checkbox":
      return (
        <div className="space-y-1">
          <label className="flex items-start gap-2 text-sm">
            <input id={id} type="checkbox" className="mt-0.5" checked={(value as boolean) ?? false} onChange={(e) => onChange(e.target.checked)} />
            <span>{f.label}{f.required && <span className="text-rose-500"> *</span>}</span>
          </label>
          {help}
          {error && <p className="text-xs text-rose-600 dark:text-rose-400">{error}</p>}
        </div>
      );
    default:
      control = (
        <input id={id} className={invalid} value={(value as string) ?? ""} placeholder={f.placeholder ?? ""} onChange={(e) => onChange(e.target.value)}
          type={f.type === "number" ? "number" : f.type === "email" ? "email" : f.type === "url" ? "url" : f.type === "date" ? "date" : "text"}
          inputMode={f.type === "number" ? "decimal" : undefined} />
      );
  }

  return (
    <div className="space-y-1.5">
      {label}
      {help}
      {control}
      {error && <p className="text-xs text-rose-600 dark:text-rose-400">{error}</p>}
    </div>
  );
}

function Centered({ children }: { children: React.ReactNode }) {
  return <div className="flex min-h-full w-full justify-center overflow-y-auto px-4 py-10"><div className="w-full max-w-xl">{children}</div></div>;
}

export default function FormsPage() {
  return <Suspense><FormPage /></Suspense>;
}
