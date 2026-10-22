"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { API_BASE, apiErrorMessage, createAppKey, listAppKeys, revokeAppKey, type AppKey, type SolutionView } from "@/lib/api";
import { Badge, Button, Card, CardHeader, CopyField, ErrorBanner, Field, Tabs, ago, cx, inputClass } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

/**
 * The Integrate tab (docs/solutions.md#integrate): everything needed to put a solution into an
 * application. A go-live checklist, the solution's app keys (what the builder's backend holds
 * instead of an organization API key), and copy-ready code for common stacks with this solution's
 * id, workflows and entities filled in.
 */
export function IntegrateTab({ solution, canManage }: { solution: SolutionView; canManage: boolean }) {
  const [keys, setKeys] = useState<AppKey[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState("Backend");
  const [created, setCreated] = useState<{ key: string; name: string } | null>(null);

  useEffect(() => {
    if (!canManage) return;
    listAppKeys(solution.install_id).then(setKeys).catch((e) => setError(apiErrorMessage(e)));
  }, [solution.install_id, canManage]);

  async function create() {
    setError(null);
    try {
      const k = await createAppKey(solution.install_id, name.trim() || "Backend");
      setCreated({ key: k.key, name: k.name });
      setKeys(await listAppKeys(solution.install_id));
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  async function revoke(k: AppKey) {
    if (!window.confirm(`Revoke "${k.name}"? Anything still using it stops working at once.`)) return;
    try {
      await revokeAppKey(solution.install_id, k.key_id);
      setKeys(await listAppKeys(solution.install_id));
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  const active = (keys ?? []).filter((k) => !k.revoked_at);
  const used = active.some((k) => k.last_used_at);
  const a = solution.assistant;
  const origins = a?.allowed_origins ?? [];
  const checks: { ok: boolean | null; title: string; detail: React.ReactNode }[] = [
    {
      ok: keys === null ? null : active.length > 0,
      title: "Your backend has an app key",
      detail: active.length > 0 ? `${active.length} active key${active.length === 1 ? "" : "s"}.` : "Create one below. It reaches this solution only, never the rest of your organization.",
    },
    {
      ok: keys === null ? null : used,
      title: "Your backend has connected",
      detail: used ? `Last call ${ago(active.map((k) => k.last_used_at).filter(Boolean).sort().at(-1))}.` : "Run the check command below from your server; this turns green on its first call.",
    },
  ];
  if (a) {
    checks.push(
      {
        ok: origins.length > 0,
        title: "Only your sites can embed the assistant",
        detail: origins.length > 0 ? origins.join(", ") : <>Any site can use it with a valid token. Add your sites under <b>Assistant → Allowed sites</b> in the builder.</>,
      },
      {
        ok: a.monthly_cost_limit_usd_per_user != null,
        title: "Each customer's spend is capped",
        detail: a.monthly_cost_limit_usd_per_user != null
          ? `$${a.monthly_cost_limit_usd_per_user.toFixed(2)} per user per month, ${a.max_messages_per_minute ?? 10} messages a minute.`
          : <>No monthly cap per user yet. Set one under <b>Assistant</b> in the builder.</>,
      },
    );
  }
  checks.push({
    ok: solution.evals.length > 0,
    title: "Behaviour is covered by evals",
    detail: solution.evals.length > 0 ? `${solution.evals.length} eval${solution.evals.length === 1 ? "" : "s"}: run them from the Evals tab before each release.` : "Add an eval for each workflow so a change that breaks it is caught.",
  });

  return (
    <div className="space-y-5">
      <ErrorBanner error={error} onClose={() => setError(null)} />
      <div className="grid grid-cols-1 gap-5 lg:grid-cols-[1fr_1.2fr]">
        <Card>
          <CardHeader title="Go-live checklist" description="What to have in place before real customers use it." />
          <ul className="divide-y divide-zinc-100 dark:divide-zinc-800">
            {checks.map((c) => (
              <li key={c.title} className="flex gap-3 px-5 py-3">
                <span className={cx("mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-full",
                  c.ok === null ? "bg-zinc-100 dark:bg-zinc-800" : c.ok ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-950/60 dark:text-emerald-300" : "bg-amber-100 text-amber-700 dark:bg-amber-950/60 dark:text-amber-300")}>
                  {c.ok ? <Icons.Check className="h-3 w-3" /> : <span className="text-[11px] font-bold">{c.ok === null ? "" : "!"}</span>}
                </span>
                <div className="min-w-0">
                  <div className="text-sm font-medium text-zinc-900 dark:text-zinc-100">{c.title}</div>
                  <div className="mt-0.5 break-words text-xs text-zinc-500">{c.detail}</div>
                </div>
              </li>
            ))}
          </ul>
          {canManage && (
            <div className="border-t border-zinc-100 px-5 py-3 dark:border-zinc-800">
              <Link href={`/solutions/build?from=${encodeURIComponent(solution.install_id)}`} className="text-xs font-medium text-brand-600 hover:underline dark:text-brand-400">
                Change sites or caps in the builder →
              </Link>
            </div>
          )}
        </Card>

        <Card>
          <CardHeader
            title="App keys"
            description="For your server only. A key can issue tokens for your users, read and write this solution's records and run its workflows. It can't deploy, change settings or reach anything else, and it's refused if a web page sends it."
          />
          <div className="space-y-4 p-5">
            {!canManage ? (
              <p className="text-sm text-zinc-500">Admins create and revoke app keys.</p>
            ) : (
              <>
                {created && (
                  <div className="space-y-2 rounded-lg border border-amber-200 bg-amber-50 p-3 dark:border-amber-900/60 dark:bg-amber-950/30">
                    <CopyField label={`${created.name}: copy it now, it won't be shown again`} value={created.key} />
                    <p className="text-xs text-amber-800 dark:text-amber-300">
                      Store it as a server secret (<span className="font-mono">AKTOR_APP_KEY</span>). Never put it in front-end code or a repository.
                    </p>
                  </div>
                )}
                <form className="flex items-end gap-2" onSubmit={(e) => { e.preventDefault(); create(); }}>
                  <Field label="Name" className="flex-1"><input className={inputClass} value={name} onChange={(e) => setName(e.target.value)} placeholder="Production backend" maxLength={100} /></Field>
                  <Button type="submit" variant="primary" icon={<Icons.Key className="h-3.5 w-3.5" />}>Create key</Button>
                </form>
                <ul className="divide-y divide-zinc-100 rounded-lg border border-zinc-200 dark:divide-zinc-800 dark:border-zinc-800">
                  {(keys ?? []).map((k) => (
                    <li key={k.key_id} className="flex items-center gap-3 px-3 py-2">
                      <div className="min-w-0 flex-1">
                        <div className="flex items-center gap-2 text-sm text-zinc-900 dark:text-zinc-100">
                          {k.name} {k.revoked_at && <Badge tone="red">Revoked</Badge>}
                        </div>
                        <div className="truncate font-mono text-[11px] text-zinc-500">
                          {k.prefix} · created {ago(k.created_at)} · {k.last_used_at ? `used ${ago(k.last_used_at)}` : "never used"}
                        </div>
                      </div>
                      {!k.revoked_at && <Button size="sm" variant="danger" onClick={() => revoke(k)}>Revoke</Button>}
                    </li>
                  ))}
                  {keys && keys.length === 0 && <li className="px-3 py-3 text-sm text-zinc-500">No keys yet.</li>}
                </ul>
              </>
            )}
          </div>
        </Card>
      </div>

      <CodeSamples solution={solution} />
    </div>
  );
}

type Stack = "next" | "express" | "python" | "curl";

function CodeSamples({ solution }: { solution: SolutionView }) {
  const [stack, setStack] = useState<Stack>("next");
  const id = solution.install_id;
  const workflow = solution.workspaces[0]?.key;
  const entity = solution.entities[0]?.name;
  // A required field, so the copied create call is valid as written.
  const fields = Object.entries(solution.entities[0]?.fields ?? {});
  const firstField = (fields.find(([, f]) => f.required) ?? fields[0])?.[0] ?? "name";
  const assistant = !!solution.assistant;
  const ownSignIn = !!solution.own_sign_in;
  const s = samples({ id, workflow, entity, firstField });
  let step = 2;

  return (
    <Card>
      <CardHeader
        title="Connect your app"
        description={`Copy these into your project. ${API_BASE} and ${id} are filled in; the key comes from an environment variable.`}
      />
      <div className="space-y-5 p-5">
        <CopyField label="1. Set these on your server" value={`AKTOR_URL=${API_BASE}\nAKTOR_APP_KEY=aks_…   # from App keys above`} multiline />
        <CopyField label="2. Check the connection from your server (lists what the key can use)" multiline
          value={`curl -s ${API_BASE}/api/v1/apps/${id} -H "Authorization: Bearer $AKTOR_APP_KEY"`} />

        <Tabs<Stack>
          value={stack}
          onChange={setStack}
          tabs={[
            { id: "next", label: "Next.js" },
            { id: "express", label: "Node / Express" },
            { id: "python", label: "Python" },
            { id: "curl", label: "Any language (HTTP)" },
          ]}
        />
        {s[stack].install && <CopyField label={`${++step}. Install the SDK`} value={s[stack].install!} />}
        {s[stack].client && <CopyField multiline label={`${++step}. Create the client once, on your server`} value={s[stack].client!} />}
        {assistant && (
          ownSignIn
            ? <p className="text-xs text-zinc-500">This solution accepts your own sign-in tokens (JWTs), so the widget can use the token your site already has; a token endpoint is optional.</p>
            : <CopyField multiline label={`${++step}. A token endpoint for the assistant (the widget POSTs to it): after your own sign-in, it returns a short-lived token for that user`} value={s[stack].token} />
        )}
        {assistant && (
          <CopyField multiline label={`${++step}. Put the assistant on your page`} value={`<script src="${API_BASE}/embed.js"
        data-install="${id}"
        data-token-url="/api/aktor-token"
        async></script>`} />
        )}
        {workflow && <CopyField multiline label="Run a workflow from your backend and wait for its result" value={s[stack].run} />}
        {entity && <CopyField multiline label="Read and write its records" value={s[stack].records} />}
        <p className="text-xs text-zinc-500">
          Runs follow the workflow&apos;s budgets, safety rules and approvals. A retried start with the same idempotency key returns the same run,
          and each key can start 60 runs a minute. Full reference: <Link href="/docs#solutions" className="text-brand-600 hover:underline dark:text-brand-400">Docs → Solutions</Link>.
        </p>
      </div>
    </Card>
  );
}

function samples({ id, workflow, entity, firstField }: { id: string; workflow?: string; entity?: string; firstField: string }) {
  const wf = workflow ?? "my-workflow";
  const en = entity ?? "Item";
  const tsClient = `// lib/aktor.ts
import { AktorServer } from "@aktor/sdk";

export const aktor = new AktorServer({
  installId: "${id}",
  appKey: process.env.AKTOR_APP_KEY!,
  baseUrl: process.env.AKTOR_URL,
});`;
  return {
    next: {
      install: "npm install @aktor/sdk",
      client: tsClient,
      token: `// app/api/aktor-token/route.ts
import { aktor } from "@/lib/aktor";
import { getCurrentUser } from "@/lib/auth"; // your own sign-in

export async function POST() {
  const user = await getCurrentUser();
  if (!user) return new Response("Sign in first", { status: 401 });
  const { token } = await aktor.issueToken(user.id, { name: user.name });
  return Response.json({ token });
}`,
      run: `// In a route handler or server action, never in client code
const run = await aktor.run("${wf}", {
  /* the values its stages read as {{ input.… }}, e.g. subject: "Charged twice" */
});
console.log(run.status, run.summary, run.stages.at(-1)?.output);`,
      records: `const { items } = await aktor.records.list("${en}", { limit: 20 });
const created = await aktor.records.create("${en}", { ${firstField}: "…" }, { idempotencyKey: "order-1234" });
await aktor.records.update("${en}", created.id, { ${firstField}: "…" }, { ifMatch: created.version });`,
    },
    express: {
      install: "npm install @aktor/sdk",
      client: tsClient.replace("// lib/aktor.ts\n", "").replace("process.env.AKTOR_APP_KEY!", "process.env.AKTOR_APP_KEY").replace("export const", "const"),
      token: `// requireLogin is your own sign-in middleware
app.post("/api/aktor-token", requireLogin, async (req, res) => {
  const { token } = await aktor.issueToken(req.user.id, { name: req.user.name });
  res.json({ token });
});`,
      run: `const run = await aktor.run("${wf}", {
  /* the values its stages read as {{ input.… }} */
});
console.log(run.status, run.summary);`,
      records: `const { items } = await aktor.records.list("${en}", { limit: 20 });
const created = await aktor.records.create("${en}", { ${firstField}: "…" }, { idempotencyKey: "order-1234" });`,
    },
    python: {
      install: "pip install ./sdk/python   # from the Aktor repository",
      client: `import os
from aktor import AktorServer

aktor = AktorServer("${id}", os.environ["AKTOR_APP_KEY"], base_url=os.environ["AKTOR_URL"])`,
      token: `# FastAPI; current_user is your own sign-in dependency
@app.post("/api/aktor-token")
def aktor_token(user=Depends(current_user)):
    return {"token": aktor.issue_token(user.id, name=user.name)["token"]}`,
      run: `run = aktor.run("${wf}", {
    # the values its stages read as {{ input.… }}
})
print(run["status"], run["summary"])`,
      records: `items = aktor.list_records("${en}", limit=20)["items"]
created = aktor.create_record("${en}", {"${firstField}": "…"}, idempotency_key="order-1234")`,
    },
    curl: {
      install: undefined as string | undefined,
      client: undefined as string | undefined,
      token: `curl -s -X POST ${API_BASE}/api/v1/apps/${id}/end-user-tokens \\
  -H "Authorization: Bearer $AKTOR_APP_KEY" -H "Content-Type: application/json" \\
  -d '{"user_id": "customer-42", "name": "Sam", "ttl_minutes": 60}'
# → { "token": "aeu_…", "expires_at": "…" }: return { token } to the widget`,
      run: `curl -s -X POST ${API_BASE}/api/v1/apps/${id}/runs \\
  -H "Authorization: Bearer $AKTOR_APP_KEY" -H "Content-Type: application/json" \\
  -H "Idempotency-Key: order-1234" \\
  -d '{"workflow": "${wf}", "data": { … the values its stages read as {{ input.… }} … }}'
# → 202 { "run_id": "…" }; then wait up to 30s for the result:
curl -s "${API_BASE}/api/v1/apps/${id}/runs/RUN_ID?wait=30" -H "Authorization: Bearer $AKTOR_APP_KEY"`,
      records: `curl -s "${API_BASE}/api/v1/apps/${id}/records/${en}?limit=20" -H "Authorization: Bearer $AKTOR_APP_KEY"
curl -s -X POST ${API_BASE}/api/v1/apps/${id}/records/${en} \\
  -H "Authorization: Bearer $AKTOR_APP_KEY" -H "Content-Type: application/json" \\
  -H "Idempotency-Key: order-1234" -d '{"${firstField}": "…"}'`,
    },
  };
}
