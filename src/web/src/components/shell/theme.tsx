"use client";

import { useEffect, useState } from "react";

export type ThemeChoice = "light" | "dark" | "system";
const KEY = "aktor:theme";

/** Runs before first paint, so the page never flashes the wrong theme. */
export function ThemeScript() {
  const code = `(function(){try{var c=localStorage.getItem('${KEY}')||'system';var d=c==='dark'||(c==='system'&&matchMedia('(prefers-color-scheme: dark)').matches);document.documentElement.classList.toggle('dark',d);}catch(e){document.documentElement.classList.add('dark');}})();`;
  return <script dangerouslySetInnerHTML={{ __html: code }} />;
}

function apply(choice: ThemeChoice) {
  const dark = choice === "dark" || (choice === "system" && window.matchMedia("(prefers-color-scheme: dark)").matches);
  document.documentElement.classList.toggle("dark", dark);
}

function read(): ThemeChoice {
  try {
    return (localStorage.getItem(KEY) as ThemeChoice | null) ?? "system";
  } catch {
    return "system";
  }
}

/** The current choice and a setter; "system" keeps following the OS as it changes. */
export function useTheme(): [ThemeChoice, (c: ThemeChoice) => void] {
  const [choice, setChoice] = useState<ThemeChoice>(() => (typeof window === "undefined" ? "system" : read()));

  useEffect(() => {
    if (choice !== "system") return;
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => apply("system");
    media.addEventListener("change", onChange);
    return () => media.removeEventListener("change", onChange);
  }, [choice]);

  const set = (c: ThemeChoice) => {
    try {
      localStorage.setItem(KEY, c);
    } catch {
      // Not saved; still applies for this visit.
    }
    apply(c);
    setChoice(c);
  };

  return [choice, set];
}
