"use client";

import { cx } from "@/components/ui";
import { Icons } from "@/components/ui/icons";
import type { FormDefinition, FormField, FormFieldType } from "@/lib/workspaceTypes";

export const FIELD_TYPES: { type: FormFieldType; label: string }[] = [
  { type: "text", label: "Short text" },
  { type: "textarea", label: "Long text" },
  { type: "email", label: "Email" },
  { type: "number", label: "Number" },
  { type: "date", label: "Date" },
  { type: "url", label: "Link" },
  { type: "select", label: "Choose one" },
  { type: "multiselect", label: "Choose several" },
  { type: "checkbox", label: "Checkbox" },
];

export const EMPTY_FORM: FormDefinition = {
  title: "",
  description: "",
  fields: [{ key: "name", label: "Name", type: "text", required: true, options: [] }],
  submit_label: "Submit",
  success_message: "Thanks, your submission was received.",
};

/** "Company name" → "company_name": the key a field's answer has in the run's input. */
export function fieldKey(label: string, taken: string[]): string {
  let key = label.toLowerCase().replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "").replace(/^[^a-z]+/, "").slice(0, 32) || "field";
  const base = key;
  for (let n = 2; taken.includes(key); n++) key = `${base}_${n}`;
  return key;
}

/**
 * Designs a form trigger's form: its title and fields. Each answer reaches the run as
 * input.&lt;key&gt; (for action and script stages) and as a line of text (for agents).
 */
export function FormBuilder({ value, onChange, field }: { value: FormDefinition; onChange: (next: FormDefinition) => void; field: string }) {
  const setField = (i: number, patch: Partial<FormField>) =>
    onChange({ ...value, fields: value.fields.map((f, j) => (j === i ? { ...f, ...patch } : f)) });
  const move = (i: number, by: number) => {
    const fields = [...value.fields];
    const [f] = fields.splice(i, 1);
    fields.splice(Math.max(0, Math.min(fields.length, i + by)), 0, f);
    onChange({ ...value, fields });
  };

  return (
    <div className="space-y-2">
      <input value={value.title} onChange={(e) => onChange({ ...value, title: e.target.value })} placeholder="Form title, e.g. New client request" className={field} />
      <textarea value={value.description} onChange={(e) => onChange({ ...value, description: e.target.value })} rows={2}
        placeholder="Shown above the fields: what it's for, what happens next" className={`${field} block`} />

      <ol className="space-y-1.5">
        {value.fields.map((f, i) => (
          <li key={i} className="space-y-1 rounded border border-zinc-200 p-1.5 dark:border-zinc-800">
            <div className="flex items-center gap-1">
              <input value={f.label} placeholder="Question"
                onChange={(e) => {
                  const label = e.target.value;
                  // The key follows the label until the form is saved.
                  const others = value.fields.filter((_, j) => j !== i).map((x) => x.key);
                  setField(i, { label, key: fieldKey(label, others) });
                }}
                className={cx(field, "flex-1")} />
              <select value={f.type} onChange={(e) => setField(i, { type: e.target.value as FormFieldType })}
                className="rounded border border-zinc-300 bg-white px-1 py-1 dark:border-zinc-700 dark:bg-zinc-900">
                {FIELD_TYPES.map((t) => <option key={t.type} value={t.type}>{t.label}</option>)}
              </select>
            </div>
            {(f.type === "select" || f.type === "multiselect") && (
              <input value={f.options.join(", ")} placeholder="Options, separated by commas"
                onChange={(e) => setField(i, { options: e.target.value.split(",").map((o) => o.trimStart()) })} className={field} />
            )}
            <div className="flex items-center gap-2 text-[10px] text-zinc-500">
              <label className="flex items-center gap-1">
                <input type="checkbox" checked={f.required} onChange={(e) => setField(i, { required: e.target.checked })} /> required
              </label>
              <code className="truncate" title="Where the answer is in the run's data">input.{f.key}</code>
              <span className="ml-auto flex gap-0.5">
                <button type="button" disabled={i === 0} onClick={() => move(i, -1)} className="rounded px-1 hover:bg-zinc-100 disabled:opacity-30 dark:hover:bg-zinc-800" aria-label="Move up">↑</button>
                <button type="button" disabled={i === value.fields.length - 1} onClick={() => move(i, 1)} className="rounded px-1 hover:bg-zinc-100 disabled:opacity-30 dark:hover:bg-zinc-800" aria-label="Move down">↓</button>
                <button type="button" disabled={value.fields.length === 1} onClick={() => onChange({ ...value, fields: value.fields.filter((_, j) => j !== i) })}
                  className="rounded px-1 text-rose-600 hover:bg-rose-50 disabled:opacity-30 dark:hover:bg-rose-950" aria-label="Remove field">
                  <Icons.X className="h-3 w-3" />
                </button>
              </span>
            </div>
          </li>
        ))}
      </ol>
      <button type="button" disabled={value.fields.length >= 30}
        onClick={() => onChange({ ...value, fields: [...value.fields, { key: fieldKey("question", value.fields.map((f) => f.key)), label: "", type: "text", required: false, options: [] }] })}
        className="inline-flex items-center gap-1 rounded border border-dashed border-zinc-300 px-2 py-0.5 text-zinc-600 hover:border-zinc-400 disabled:opacity-50 dark:border-zinc-700 dark:text-zinc-400">
        <Icons.Plus className="h-3 w-3" /> Add a field
      </button>
      <input value={value.success_message} onChange={(e) => onChange({ ...value, success_message: e.target.value })} placeholder="Shown after submitting" className={field} />
    </div>
  );
}

/** The form as the API wants it: empty options dropped, labels trimmed. */
export function cleanForm(form: FormDefinition): FormDefinition {
  return {
    ...form,
    title: form.title.trim(),
    fields: form.fields.map((f) => ({ ...f, label: f.label.trim(), options: f.options.map((o) => o.trim()).filter(Boolean) })),
  };
}
