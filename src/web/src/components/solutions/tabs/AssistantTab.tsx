"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { API_BASE, apiErrorMessage, createEndUserToken, type SolutionView } from "@/lib/api";
import { Badge, Button, Card, CardHeader, CopyField, EmptyState, ErrorBanner, Field, Toggle, inputClass } from "@/components/ui";

type AktorChatApi = { mount: (o: Record<string, unknown>) => { destroy: () => void } };

/** The Assistant tab: what end users talk to, and a live preview signed in as any user id. */
export function AssistantTab({ solution, canIssue }: { solution: SolutionView; canIssue: boolean }) {
  const [userId, setUserId] = useState("");
  const [name, setName] = useState("");
  const [token, setToken] = useState<{ token: string; expires_at: string; user: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState(false);
  const container = useRef<HTMLDivElement>(null);

  const mountPreview = useCallback(() => {
    if (!token || !container.current) return;
    const el = container.current;
    let widget: { destroy: () => void } | null = null;
    const start = () => {
      const chat = (window as unknown as { AktorChat?: AktorChatApi }).AktorChat;
      if (chat) widget = chat.mount({ install: solution.install_id, token: token.token, apiBase: API_BASE, container: el });
    };
    if ((window as unknown as { AktorChat?: AktorChatApi }).AktorChat) start();
    else {
      const s = document.createElement("script");
      s.src = `${API_BASE}/embed.js`;
      s.onload = start;
      document.body.appendChild(s);
    }
    return () => widget?.destroy();
  }, [token, solution.install_id]);

  useEffect(() => (preview ? mountPreview() : undefined), [preview, mountPreview]);

  if (!solution.assistant) {
    return <EmptyState title="No assistant" description="This solution doesn't give its customers an assistant. Add one in the builder under Assistant." />;
  }

  async function issue() {
    setError(null);
    setPreview(false);
    try {
      const t = await createEndUserToken(solution.install_id, userId.trim(), name.trim());
      setToken({ ...t, user: userId.trim() });
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  const snippet = `<script src="${API_BASE}/embed.js"
        data-install="${solution.install_id}"
        data-token-url="/api/aktor-token"
        async></script>`;

  return (
    <div className="grid grid-cols-1 gap-5 lg:grid-cols-2">
      <Card>
        <CardHeader title={solution.assistant.name} description="What your customers talk to. Each end user's conversations and records are theirs alone, enforced by the runtime." />
        <div className="space-y-4 p-5 text-sm">
          {solution.assistant.greeting && <p className="rounded-lg bg-zinc-50 px-3 py-2 text-zinc-700 dark:bg-zinc-950 dark:text-zinc-300">“{solution.assistant.greeting}”</p>}
          <div className="flex flex-wrap gap-1.5">
            {Object.entries(solution.assistant.data).map(([e, a]) => <Badge key={e} tone={a === "write" ? "amber" : "neutral"}>{e}: {a}</Badge>)}
          </div>
          <CopyField multiline label="Embed it on your site" value={snippet} />
          <p className="text-xs text-zinc-500">
            <span className="font-mono">/api/aktor-token</span> is your backend: after your own sign-in it issues a token for that user with
            the solution&apos;s app key and returns <span className="font-mono">{"{ token }"}</span>. The <b>Integrate</b> tab has the key and the code for your stack.
          </p>
        </div>
      </Card>

      <Card>
        <CardHeader title="Try it as an end user" description="Issue a short-lived token for a user id of yours and talk to the assistant as them." />
        <div className="space-y-3 p-5">
          <ErrorBanner error={error} onClose={() => setError(null)} />
          {canIssue ? (
            <form className="grid grid-cols-2 gap-3" onSubmit={(e) => { e.preventDefault(); issue(); }}>
              <Field label="User id"><input className={inputClass} value={userId} onChange={(e) => setUserId(e.target.value)} placeholder="customer-42" required /></Field>
              <Field label="Name (optional)"><input className={inputClass} value={name} onChange={(e) => setName(e.target.value)} placeholder="Sam" /></Field>
              <div className="col-span-2 flex justify-end"><Button type="submit" variant="primary" disabled={!userId.trim()}>Issue token</Button></div>
            </form>
          ) : (
            <p className="text-sm text-zinc-500">Admins can issue end-user tokens.</p>
          )}
          {token && (
            <div className="space-y-3">
              <CopyField label={`Token for ${token.user} (expires ${new Date(token.expires_at).toLocaleTimeString()})`} value={token.token} />
              <Toggle checked={preview} onChange={setPreview} label="Open the widget here" description="The same widget your site embeds, signed in as this user." />
              {preview && <div ref={container} className="h-[440px] overflow-hidden rounded-xl border border-zinc-200 dark:border-zinc-800" />}
            </div>
          )}
        </div>
      </Card>
    </div>
  );
}
