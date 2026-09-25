"use client";

import { useEffect, useState } from "react";
import { acceptInvitation, apiErrorMessage, getMe, peekInvitation, signIn, signUp } from "@/lib/api";
import type { Me } from "@/lib/platformTypes";

const field = "w-full rounded border border-neutral-300 bg-white px-3 py-2 text-sm dark:border-neutral-700 dark:bg-neutral-900";

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
    <div className="flex min-h-screen items-center justify-center bg-neutral-50 p-4 dark:bg-neutral-950">
      <form onSubmit={submit} className="w-full max-w-sm space-y-3 rounded-lg border border-neutral-200 bg-white p-6 shadow-sm dark:border-neutral-800 dark:bg-neutral-900">
        <div>
          <h1 className="text-lg font-semibold">Aktor Agents</h1>
          <p className="text-xs text-neutral-500">
            {invitedTo
              ? `You're invited to join ${invitedTo.organization} as ${invitedTo.role}.`
              : mode === "signin" ? "Sign in to your organization." : "Create an account and your organization."}
          </p>
        </div>

        {mode === "signup" && (
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Your name" autoComplete="name" className={field} />
        )}
        <input value={email} onChange={(e) => setEmail(e.target.value)} placeholder="Email" type="email" required autoComplete="email"
          readOnly={!!invitedTo} className={field} />
        <input value={password} onChange={(e) => setPassword(e.target.value)} placeholder={mode === "signup" ? "Password (10+ characters)" : "Password"}
          type="password" required autoComplete={mode === "signup" ? "new-password" : "current-password"} className={field} />
        {mode === "signup" && !invitedTo && (
          <input value={organization} onChange={(e) => setOrganization(e.target.value)} placeholder="Organization name (optional)" className={field} />
        )}

        {error && <div className="text-xs text-rose-600">{error}</div>}

        <button type="submit" disabled={busy} className="w-full rounded bg-blue-600 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50">
          {busy ? "…" : mode === "signin" ? "Sign in" : invitedTo ? "Create account and join" : "Create account"}
        </button>

        {!invitedTo && signupAvailable && (
          <button type="button" onClick={() => { setMode(mode === "signin" ? "signup" : "signin"); setError(null); }}
            className="w-full text-center text-xs text-blue-600 hover:underline">
            {mode === "signin" ? "No account? Create one" : "Have an account? Sign in"}
          </button>
        )}
        {invitedTo && mode === "signup" && (
          <button type="button" onClick={() => setMode("signin")} className="w-full text-center text-xs text-blue-600 hover:underline">
            Already have an account? Sign in, then open the invitation link again
          </button>
        )}
      </form>
    </div>
  );
}
