"use client";

import { useEffect, useState } from "react";

/** Reload delays after a change: live events reach the browser before the server has saved the
 * row they describe, so one immediate reload can still see the old list. */
const RETRY_DELAYS_MS = [0, 1000, 3000, 7000];

/**
 * A server list that reloads when `trigger` changes (e.g. a count of "file written" events), and
 * again a few times after, so the newest item shows up without a page refresh. `load` should be
 * stable for a given `key`; changing `key` starts over.
 */
export function useLiveList<T>(key: string | null, trigger: number, load: () => Promise<T[]>): T[] | null {
  const [state, setState] = useState<{ key: string | null; items: T[] | null }>({ key, items: null });

  useEffect(() => {
    if (key === null) return;
    let cancelled = false;
    const timers = RETRY_DELAYS_MS.map((delay) => setTimeout(() => {
      load()
        .then((items) => { if (!cancelled) setState({ key, items }); })
        .catch(() => { /* API briefly unavailable: the next attempt retries */ });
    }, delay));
    return () => {
      cancelled = true;
      timers.forEach(clearTimeout);
    };
    // `load` is tied to `key`; re-running on its identity would reload on every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, trigger]);

  return state.key === key ? state.items : null;
}
