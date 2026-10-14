import type { StageKind } from "@/lib/pipelineTypes";
import { BotIcon } from "../BotIcon";

/** The icon for what does a stage's work: a bot (agent), a plug (action), brackets (script). */
export function StageKindIcon({ kind, className }: { kind: StageKind; className?: string }) {
  if (kind === "Agent") return <BotIcon className={className} />;
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" className={className} aria-hidden="true">
      {kind === "Action" ? (
        <>
          <path d="M9 2v6M15 2v6" />
          <path d="M6 8h12v3a6 6 0 0 1-12 0V8Z" />
          <path d="M12 17v5" />
        </>
      ) : (
        <>
          <path d="m8 7-5 5 5 5" />
          <path d="m16 7 5 5-5 5" />
          <path d="m14 4-4 16" />
        </>
      )}
    </svg>
  );
}

/** Small icons for the parts attached to an agent on the canvas (n8n-style sub-nodes). */
export function PartIcon({ part, className }: { part: "model" | "tools" | "memory"; className?: string }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" className={className} aria-hidden="true">
      {part === "model" ? (
        <>
          <rect x="5" y="5" width="14" height="14" rx="2" />
          <path d="M9 9h6v6H9zM9 2v3M15 2v3M9 19v3M15 19v3M2 9h3M2 15h3M19 9h3M19 15h3" />
        </>
      ) : part === "tools" ? (
        <path d="M14.7 6.3a4 4 0 0 0-5.4 5.4L3 18l3 3 6.3-6.3a4 4 0 0 0 5.4-5.4l-2.6 2.6-2.4-.6-.6-2.4 2.6-2.6Z" />
      ) : (
        <>
          <ellipse cx="12" cy="5" rx="8" ry="3" />
          <path d="M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3" />
        </>
      )}
    </svg>
  );
}

/** Icons for trigger nodes. */
export function TriggerIcon({ kind, className }: { kind: string; className?: string }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" className={className} aria-hidden="true">
      {kind === "Form" ? (
        <>
          <rect x="4" y="3" width="16" height="18" rx="2" />
          <path d="M8 8h8M8 12h8M8 16h5" />
        </>
      ) : kind === "Webhook" ? (
        <path d="M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7" />
      ) : kind === "Watch" ? (
        <>
          <path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z" />
          <circle cx="12" cy="12" r="3" />
        </>
      ) : kind === "Schedule" ? (
        <>
          <circle cx="12" cy="12" r="9" />
          <path d="M12 7v5l3 2" />
        </>
      ) : (
        <>
          <path d="M4 21v-1a6 6 0 0 1 6-6h4a6 6 0 0 1 6 6v1" />
          <circle cx="12" cy="8" r="4" />
        </>
      )}
    </svg>
  );
}
