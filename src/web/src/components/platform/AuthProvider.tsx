"use client";

import { createContext, useCallback, useContext, useEffect, useState } from "react";
import { usePathname, useRouter } from "next/navigation";
import { UNAUTHORIZED_EVENT, getMe } from "@/lib/api";
import type { Me } from "@/lib/platformTypes";

const PUBLIC_PATHS = ["/login"];

interface AuthState {
  me: Me | null;
  refresh: () => Promise<Me | null>;
}

const AuthContext = createContext<AuthState>({ me: null, refresh: async () => null });

export function useAuth() {
  return useContext(AuthContext);
}

/** Loads who's signed in and keeps everything but the sign-in page behind it. */
export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [error, setError] = useState<string | null>(null);
  const pathname = usePathname();
  const router = useRouter();
  const isPublic = PUBLIC_PATHS.includes(pathname);

  const refresh = useCallback(async () => {
    try {
      const next = await getMe();
      setMe(next);
      setError(null);
      return next;
    } catch {
      setError("Can't reach the API server.");
      return null;
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    getMe()
      .then((next) => { if (!cancelled) { setMe(next); setError(null); } })
      .catch(() => { if (!cancelled) setError("Can't reach the API server."); });
    return () => { cancelled = true; };
  }, []);

  // A 401 from any call means the session ended (signed out elsewhere, expired, removed).
  useEffect(() => {
    const onUnauthorized = () => { void refresh(); };
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
  }, [refresh]);

  const mustSignIn = me !== null && !me.authenticated && me.server.auth_mode === "accounts" && !isPublic;
  useEffect(() => {
    if (mustSignIn) {
      const next = window.location.pathname + window.location.search;
      router.replace(`/login?next=${encodeURIComponent(next)}`);
    }
  }, [mustSignIn, router]);

  if (error && !me) {
    return <div className="m-auto p-8 text-sm text-rose-600">{error}</div>;
  }

  if (!isPublic && (me === null || mustSignIn)) {
    return <div className="m-auto p-8 text-sm text-neutral-500">Loading…</div>;
  }

  return <AuthContext.Provider value={{ me, refresh }}>{children}</AuthContext.Provider>;
}
