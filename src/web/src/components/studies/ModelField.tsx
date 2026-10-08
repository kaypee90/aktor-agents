"use client";

import type { ModelChoice } from "@/lib/api";
import { Field, inputClass } from "@/components/ui";

export function modelName(models: ModelChoice[], id: string | null) {
  if (!id) return "Organization default";
  const m = models.find((x) => x.id === id);
  return m ? (m.provider === "Mock" ? "Mock (demo)" : `${m.name} · ${m.provider}`) : id;
}

/** The provider and model a study (or one run or experiment of it) uses. */
export function ModelField({ models, value, onChange, hint }: { models: ModelChoice[]; value: string; onChange: (id: string) => void; hint?: string }) {
  return (
    <Field label="Model" hint={hint ?? "Its runs and simulated participants use this model. Add models under Settings → AI model."}>
      <select className={inputClass} value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">Organization default</option>
        {models.map((m) => (
          <option key={m.id} value={m.id}>
            {m.name} · {m.provider === "Mock" ? "demo" : `${m.provider} ${m.model}`}{m.in_per_million ? ` · $${m.in_per_million} in / $${m.out_per_million} out` : ""}
          </option>
        ))}
      </select>
    </Field>
  );
}
