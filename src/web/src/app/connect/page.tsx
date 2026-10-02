"use client";

import Link from "next/link";
import { useState } from "react";
import { useAuth } from "@/components/platform/AuthProvider";
import { API_BASE, apiErrorMessage, createApiKey } from "@/lib/api";
import { atLeast, type Role } from "@/lib/platformTypes";
import { Badge, Button, Card, CardHeader, CopyField, ErrorBanner, PageHeader, Tabs } from "@/components/ui";
import { Icons } from "@/components/ui/icons";

type Channel = "mcp" | "a2a" | "acp" | "rest";

/**
 * How other tools hand goals to Aktor: MCP, A2A, ACP and REST all reach the same task service,
 * with the same API keys, budgets, quotas and isolation (docs/integrations.md).
 */
export default function ConnectPage() {
  const { me } = useAuth();
  const isAdmin = atLeast((me?.role ?? "Viewer") as Role, "Admin");
  const [channel, setChannel] = useState<Channel>("mcp");
  const [key, setKey] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const keyText = key ?? "ak_your_key";
  const mcp = `${API_BASE}/mcp`;

  async function newKey() {
    setBusy(true);
    setError(null);
    try {
      const created = await createApiKey(`Integration ${new Date().toISOString().slice(0, 10)}`, "Member");
      setKey(created.key);
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <PageHeader
        title="Integrations & API"
        description="Let other tools hand goals to an Aktor team: Claude Code and n8n over MCP, CrewAI and other frameworks over A2A, OpenClaw and Zed over ACP, or anything over REST. Every way in uses the same API keys, budgets, quotas and organization isolation."
      />
      <div className="mx-auto max-w-5xl space-y-6 px-6 py-6">
        <Card>
          <CardHeader
            title="1. An API key"
            description="Each key belongs to this organization and has a role. Member keys can start tasks; Viewer keys can only read."
            actions={isAdmin
              ? <Button variant="primary" size="sm" disabled={busy} onClick={newKey}>{busy ? "Creating…" : "Create a Member key"}</Button>
              : <Badge>Ask an Admin for a key</Badge>}
          />
          <div className="space-y-3 p-5">
            <ErrorBanner error={error} />
            {key ? (
              <>
                <CopyField label="Your new key (shown once: store it somewhere safe)" value={key} />
                <p className="text-xs text-zinc-500">The snippets below now include it.</p>
              </>
            ) : (
              <p className="text-sm text-zinc-500">
                Create one here, or manage keys under <Link href="/settings?tab=api-keys" className="text-brand-600 hover:underline dark:text-brand-400">Settings → API keys</Link>.
                Snippets below show <code className="font-mono text-xs">ak_your_key</code> until you do.
              </p>
            )}
          </div>
        </Card>

        <Card>
          <CardHeader title="2. Connect your tool" />
          <Tabs className="px-5" value={channel} onChange={setChannel}
            tabs={[{ id: "mcp", label: "MCP" }, { id: "a2a", label: "A2A" }, { id: "acp", label: "ACP" }, { id: "rest", label: "REST & webhooks" }]} />
          <div className="space-y-5 p-5">
            {channel === "mcp" && (
              <>
                <p className="text-sm text-zinc-600 dark:text-zinc-400">
                  Streamable HTTP, stateless. Tools: <code className="font-mono text-xs">run_goal</code>, <code className="font-mono text-xs">get_task_status</code> (can wait, with progress),{" "}
                  <code className="font-mono text-xs">get_task_result</code>, <code className="font-mono text-xs">cancel_task</code>, <code className="font-mono text-xs">list_agents</code>.
                </p>
                <CopyField label="Endpoint" value={mcp} />
                <CopyField label="Claude Code" value={`claude mcp add --transport http aktor ${mcp} \\\n  --header "Authorization: Bearer ${keyText}"`} multiline />
                <CopyField label="n8n: MCP Client Tool node" value={`Endpoint:       ${mcp}\nAuthentication: Header Auth\nHeader name:    Authorization\nHeader value:   Bearer ${keyText}`} multiline />
                <CopyField label="Any MCP client (JSON config)" multiline value={JSON.stringify({ mcpServers: { aktor: { type: "http", url: mcp, headers: { Authorization: `Bearer ${keyText}` } } } }, null, 2)} />
              </>
            )}

            {channel === "a2a" && (
              <>
                <p className="text-sm text-zinc-600 dark:text-zinc-400">
                  An A2A agent speaking protocol 1.0 and 0.3. A message&apos;s text is the goal; the reply is an A2A task whose artifact holds the summary, findings and full result.
                  Options go in <code className="font-mono text-xs">message.metadata.aktor</code>: <code className="font-mono text-xs">budget</code>, <code className="font-mono text-xs">workspace</code>, <code className="font-mono text-xs">team_policy</code>.
                </p>
                <CopyField label="Agent card (public)" value={`${API_BASE}/.well-known/agent-card.json`} />
                <CopyField label="JSON-RPC endpoint" value={`${API_BASE}/a2a`} />
                <CopyField label="Try it" multiline value={`curl -s ${API_BASE}/a2a \\\n  -H "Authorization: Bearer ${keyText}" -H "A2A-Version: 1.0" -H "Content-Type: application/json" \\\n  -d '{"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"Research our top competitors"}]},"configuration":{"returnImmediately":true}}}'`} />
              </>
            )}

            {channel === "acp" && (
              <>
                <p className="text-sm text-zinc-600 dark:text-zinc-400">
                  ACP clients launch an agent as a local process. <code className="font-mono text-xs">aktor-acp</code> (Node 22+, in the TypeScript SDK) is that process: it relays to this server, and each prompt runs as a task, with the team shown as a plan.
                </p>
                <CopyField label="Run the bridge" value={`AKTOR_URL=${API_BASE} AKTOR_API_KEY=${keyText} npx aktor-acp`} />
                <CopyField label="OpenClaw / acpx" value={`AKTOR_URL=${API_BASE} AKTOR_API_KEY=${keyText} acpx --agent "npx aktor-acp" "Research the market for …"`} />
              </>
            )}

            {channel === "rest" && (
              <>
                <p className="text-sm text-zinc-600 dark:text-zinc-400">
                  Start a task, then long-poll it, or give a webhook that is POSTed the result (signed with <code className="font-mono text-xs">X-Aktor-Signature: sha256=HMAC(secret, body)</code>).
                  Every result carries a <code className="font-mono text-xs">correlation_id</code> and a <code className="font-mono text-xs">dashboard_url</code>.
                </p>
                <CopyField label="Start a task" multiline value={`curl -s ${API_BASE}/api/tasks \\\n  -H "Authorization: Bearer ${keyText}" -H "Content-Type: application/json" \\\n  -d '{"goal":"Research our top competitors","budget":{"max_cost_usd":3},"callback_url":"https://example.com/aktor-done","callback_secret":"change-me"}'`} />
                <CopyField label="Wait for it (long poll)" value={`curl -s "${API_BASE}/api/tasks/<task_id>/wait?timeout_seconds=120" -H "Authorization: Bearer ${keyText}"`} />
                <CopyField label="API description (OpenAPI)" value={`${API_BASE}/openapi/v1.json`} />
              </>
            )}
          </div>
        </Card>

        <Card className="flex items-start gap-3 p-5">
          <Icons.Shield className="mt-0.5 h-5 w-5 shrink-0 text-emerald-500" />
          <div className="text-sm text-zinc-600 dark:text-zinc-400">
            <span className="font-medium text-zinc-900 dark:text-zinc-100">No bypass.</span> Callers can&apos;t exceed the server&apos;s task budget ceiling, your plan&apos;s quotas or the team-shape rules,
            and never see another organization&apos;s tasks. Runs started here appear in <Link href="/runs" className="text-brand-600 hover:underline dark:text-brand-400">Run history</Link> with their source.
          </div>
        </Card>
      </div>
    </div>
  );
}
