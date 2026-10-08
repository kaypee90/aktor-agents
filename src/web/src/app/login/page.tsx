"use client";

import { Icons } from "@/components/ui/icons";

import { useEffect, useState } from "react";
import { acceptInvitation, apiErrorMessage, getMe, peekInvitation, signIn, signUp } from "@/lib/api";
import type { Me } from "@/lib/platformTypes";

const field = "w-full rounded-lg border border-zinc-200 bg-white px-3 py-2.5 text-sm shadow-sm outline-none focus:border-brand-400 focus:ring-2 focus:ring-brand-500/20 dark:border-zinc-800 dark:bg-zinc-950";

/** Only same-site paths, so a crafted ?next= can't send someone to another site after sign-in. */
function safeNext(value: string | null) {
  return value && value.startsWith("/") && !value.startsWith("//") ? value : "/workspaces";
}

export default function LoginPage() {
  const [mode, setMode] = useState<"signin" | "signup">("signin");
  const [me, setMe] = useState<Me | null>(null);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [name, setName] = useState("");
  const [organization, setOrganization] = useState("");
  const [invitation, setInvitation] = useState<string | null>(null);
  const [invitedTo, setInvitedTo] = useState<{ organization: string; role: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const token = params.get("invite");
    getMe()
      .then(async (m) => {
        setMe(m);
        if (m.server.auth_mode === "disabled") {
          window.location.replace(safeNext(params.get("next")));
          return;
        }

        if (token) {
          setInvitation(token);
          try {
            const peek = await peekInvitation(token);
            setInvitedTo({ organization: peek.organization, role: peek.role });
            setEmail(peek.email);
            if (m.authenticated) {
              // Already signed in: join straight away.
              await acceptInvitation(token);
              window.location.replace("/workspaces");
              return;
            }
            setMode("signup");
          } catch (err) {
            setError(apiErrorMessage(err));
          }
        } else if (m.authenticated) {
          window.location.replace(safeNext(params.get("next")));
        } else if (params.get("mode") === "signup") {
          setMode("signup");
        }
      })
      .catch(() => setError("Can't reach the API server."));
  }, []);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      if (mode === "signin") await signIn(email, password);
      else await signUp({ email, password, name: name || undefined, organization: organization || undefined, invitation: invitation ?? undefined });
      window.location.replace(safeNext(new URLSearchParams(window.location.search).get("next")));
    } catch (err) {
      setError(apiErrorMessage(err));
      setBusy(false);
    }
  }

  const signupAvailable = !!invitation || me?.server.signup_allowed !== false;

  return (
    <div className="grid min-h-screen lg:grid-cols-2">
      <div className="relative hidden overflow-hidden bg-zinc-950 p-12 text-zinc-100 lg:flex lg:flex-col lg:justify-between">
        <div className="pointer-events-none absolute -left-32 -top-32 h-96 w-96 rounded-full bg-brand-500/25 blur-3xl" />
        <div className="pointer-events-none absolute -bottom-40 right-0 h-96 w-96 rounded-full bg-brand-700/20 blur-3xl" />
        <a href="/welcome" className="relative flex items-center gap-3">
          <Icons.Logo className="h-9 w-9" />
          <span className="text-lg font-semibold tracking-tight">Aktor</span>
        </a>
        <div className="relative max-w-md space-y-4">
          <h2 className="text-3xl font-semibold leading-tight tracking-tight">Governed agent teams for your organization.</h2>
          <ul className="space-y-2 text-sm text-zinc-400">
            <li className="flex gap-2"><Icons.Check className="h-4 w-4 text-brand-400" /> Teams that plan and staff themselves, under budgets the runtime enforces</li>
            <li className="flex gap-2"><Icons.Check className="h-4 w-4 text-brand-400" /> Approvals, team-shape rules and a tamper-evident audit log</li>
            <li className="flex gap-2"><Icons.Check className="h-4 w-4 text-brand-400" /> Callable from Claude Code, n8n, CrewAI and OpenClaw</li>
          </ul>
        </div>
        <div className="relative text-xs text-zinc-500">Every run is recorded, replayable and inspectable.</div>
      </div>

      <div className="flex items-center justify-center bg-zinc-50 p-6 dark:bg-zinc-950">
      <form onSubmit={submit} className="w-full max-w-sm space-y-4 rounded-2xl border border-zinc-200 bg-white p-8 shadow-xl dark:border-zinc-800 dark:bg-zinc-900">
        <div>
          <a href="/welcome" aria-label="Aktor home"><Icons.Logo className="mb-4 h-9 w-9 lg:hidden" /></a>
          <h1 className="text-xl font-semibold tracking-tight">{mode === "signin" ? "Welcome back" : "Create your account"}</h1>
          <p className="mt-1 text-sm text-zinc-500">
            {invitedTo
              ? `You're invited to join ${invitedTo.organization} as ${invitedTo.role}.`
              : mode === "signin" ? "Sign in to your organization." : "Create an account and your organization."}
          </p>
        </div>

        {mode === "signup" && (
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Your name" autoComplete="name" className={field} />
        )}
        {/* Sign-in also takes a username (the local admin), so it's not an email-only field. */}
        <input value={email} onChange={(e) => setEmail(e.target.value)} placeholder={mode === "signin" ? "Email or username" : "Email"}
          type={mode === "signin" ? "text" : "email"} required autoComplete={mode === "signin" ? "username" : "email"}
          readOnly={!!invitedTo} className={field} />
        <input value={password} onChange={(e) => setPassword(e.target.value)} placeholder={mode === "signup" ? "Password (10+ characters)" : "Password"}
          type="password" required autoComplete={mode === "signup" ? "new-password" : "current-password"} className={field} />
        {mode === "signup" && !invitedTo && (
          <input value={organization} onChange={(e) => setOrganization(e.target.value)} placeholder="Organization name (optional)" className={field} />
        )}

        {error && <div className="text-xs text-rose-600">{error}</div>}

        <button type="submit" disabled={busy} className="w-full rounded-lg bg-brand-500 py-2.5 text-sm font-medium text-white shadow-sm hover:bg-brand-600 disabled:opacity-50">
          {busy ? "…" : mode === "signin" ? "Sign in" : invitedTo ? "Create account and join" : "Create account"}
        </button>

        {!invitedTo && signupAvailable && (
          <button type="button" onClick={() => { setMode(mode === "signin" ? "signup" : "signin"); setError(null); }}
            className="w-full text-center text-xs text-brand-600 hover:underline dark:text-brand-400">
            {mode === "signin" ? "No account? Create one" : "Have an account? Sign in"}
          </button>
        )}
        {invitedTo && mode === "signup" && (
          <button type="button" onClick={() => setMode("signin")} className="w-full text-center text-xs text-brand-600 hover:underline dark:text-brand-400">
            Already have an account? Sign in, then open the invitation link again
          </button>
        )}
      </form>
      </div>
    </div>
  );
}
