import type { ReactNode } from "react";
import { cx } from "@/components/ui";

/** A connector's mark, by plugin id, in its own colors (as n8n shows Slack or Sheets nodes). */
const MARKS: Record<string, ReactNode> = {
  slack: (
    <>
      <rect x="1" y="6.5" width="10.5" height="5" rx="2.5" fill="#36C5F0" />
      <rect x="6.5" y="1" width="5" height="4.5" rx="2.25" fill="#36C5F0" />
      <rect x="12.5" y="1" width="5" height="10.5" rx="2.5" fill="#2EB67D" />
      <rect x="18.5" y="6.5" width="4.5" height="5" rx="2.25" fill="#2EB67D" />
      <rect x="12.5" y="12.5" width="10.5" height="5" rx="2.5" fill="#ECB22E" />
      <rect x="12.5" y="18.5" width="5" height="4.5" rx="2.25" fill="#ECB22E" />
      <rect x="6.5" y="12.5" width="5" height="10.5" rx="2.5" fill="#E01E5A" />
      <rect x="1" y="12.5" width="4.5" height="5" rx="2.25" fill="#E01E5A" />
    </>
  ),
  "google-sheets": (
    <>
      <path d="M6 1.5h8.5l5.5 5.5v14a1.5 1.5 0 0 1-1.5 1.5h-12.5A1.5 1.5 0 0 1 4.5 21V3A1.5 1.5 0 0 1 6 1.5Z" fill="#0F9D58" />
      <path d="M14.5 1.5V7H20Z" fill="#87CEAC" />
      <path d="M8 11h8v7H8z M8 13.3h8 M8 15.7h8 M11.2 11v7" fill="none" stroke="#fff" strokeWidth="1.2" />
    </>
  ),
  airtable: (
    <>
      <path d="M10.9 2.6 2.7 6c-.5.2-.5.9 0 1.1l8.2 3.3c.7.3 1.5.3 2.2 0l8.2-3.3c.5-.2.5-.9 0-1.1l-8.2-3.4a2.9 2.9 0 0 0-2.2 0Z" fill="#FCB400" />
      <path d="M12.8 12.5v8.1c0 .4.4.7.8.5l9-3.5c.2-.1.4-.3.4-.6V8.9c0-.4-.4-.7-.8-.5l-9 3.5c-.2.1-.4.4-.4.6Z" fill="#18BFFF" />
      <path d="m10.7 12.9-2.7 1.3-.3.1-5.7 2.7c-.4.2-.9-.1-.9-.5V9c0-.2.1-.3.2-.4l.2-.1c.2-.1.3-.1.5 0l8.6 3.4c.5.2.5.8.1 1Z" fill="#F82B60" />
    </>
  ),
  bigquery: (
    <>
      <path d="M12 1.5 21.5 7v10L12 22.5 2.5 17V7Z" fill="#4386FA" />
      <circle cx="11" cy="11" r="4" fill="none" stroke="#fff" strokeWidth="1.8" />
      <path d="m14 14 3.2 3.2" stroke="#fff" strokeWidth="2" strokeLinecap="round" />
      <path d="M9.4 12.3v-1.6M11 12.3V9.4M12.6 12.3v-1" stroke="#fff" strokeWidth="1.1" strokeLinecap="round" />
    </>
  ),
  "zoho-crm": (
    <>
      <rect x="2" y="2" width="9.5" height="9.5" rx="2" fill="#E42527" />
      <rect x="12.5" y="2" width="9.5" height="9.5" rx="2" fill="#089949" />
      <rect x="2" y="12.5" width="9.5" height="9.5" rx="2" fill="#226DB4" />
      <rect x="12.5" y="12.5" width="9.5" height="9.5" rx="2" fill="#F9B21D" />
    </>
  ),
  sentry: (
    <>
      <rect x="1" y="1" width="22" height="22" rx="5" fill="#362D59" />
      <path d="M12 5.5 5.5 17h3.2M12 5.5l6.5 11.5h-3.2M12 5.5l-2 3.5a8 8 0 0 1 4.2 8" fill="none" stroke="#fff" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
    </>
  ),
  "twilio-sms": (
    <>
      <circle cx="12" cy="12" r="10" fill="none" stroke="#F22F46" strokeWidth="2.6" />
      <circle cx="9.3" cy="9.3" r="1.9" fill="#F22F46" />
      <circle cx="14.7" cy="9.3" r="1.9" fill="#F22F46" />
      <circle cx="9.3" cy="14.7" r="1.9" fill="#F22F46" />
      <circle cx="14.7" cy="14.7" r="1.9" fill="#F22F46" />
    </>
  ),
  telegram: (
    <>
      <circle cx="12" cy="12" r="11" fill="#26A5E4" />
      <path d="m5.4 11.7 11.3-4.4c.5-.2 1 .1.8.9l-1.9 9c-.1.6-.5.8-1.1.5l-3-2.2-1.4 1.4c-.2.2-.3.3-.6.3l.2-3.1 5.6-5c.2-.2 0-.4-.4-.1l-6.9 4.3-3-.9c-.6-.2-.7-.6.4-.7Z" fill="#fff" />
    </>
  ),
  sendgrid: (
    <>
      <path d="M8 2h7v7H8z" fill="#99E1F4" />
      <path d="M15 2h7v7h-7zM2 15h7v7H2z" fill="#1A82E2" />
      <path d="M8 9h7v7H8z" fill="#00B3E3" />
      <path d="M2 9h6v6H2zM15 9h7v6h-7z" fill="#99E1F4" opacity=".6" />
    </>
  ),
  brevo: (
    <>
      <circle cx="12" cy="12" r="11" fill="#0B996E" />
      <path d="M8.5 6.5h4.2a2.8 2.8 0 0 1 0 5.6H8.5Zm0 5.6h4.9a2.7 2.7 0 0 1 0 5.4H8.5Z" fill="none" stroke="#fff" strokeWidth="2" strokeLinejoin="round" />
    </>
  ),
  "email-smtp": (
    <>
      <rect x="1" y="1" width="22" height="22" rx="5" fill="#475569" />
      <rect x="5" y="7" width="14" height="10" rx="1.5" fill="none" stroke="#fff" strokeWidth="1.7" />
      <path d="m5.5 8 6.5 5 6.5-5" fill="none" stroke="#fff" strokeWidth="1.7" strokeLinejoin="round" />
    </>
  ),
  mcp: (
    <>
      <rect x="1" y="1" width="22" height="22" rx="5" fill="#18181B" />
      <path d="m6 11.5 6.2-6.2a2.3 2.3 0 0 1 3.3 3.3l-4.7 4.7M10.8 13.3l4.7-4.7a2.3 2.3 0 0 1 3.3 3.3L13 17.6a.9.9 0 0 0 0 1.3l.9.9M9.3 9.6l-3.6 3.6a2.3 2.3 0 0 0 3.3 3.3l3.6-3.6"
        fill="none" stroke="#fff" strokeWidth="1.5" strokeLinecap="round" />
    </>
  ),
  "http-api": (
    <>
      <rect x="1" y="1" width="22" height="22" rx="5" fill="#6366F1" />
      <circle cx="12" cy="12" r="6.5" fill="none" stroke="#fff" strokeWidth="1.6" />
      <path d="M5.5 12h13M12 5.5c-2.2 2.5-2.2 10.5 0 13M12 5.5c2.2 2.5 2.2 10.5 0 13" fill="none" stroke="#fff" strokeWidth="1.4" />
    </>
  ),
  "demo-ops": (
    <>
      <rect x="1" y="1" width="22" height="22" rx="5" fill="#EA580C" />
      <rect x="6" y="6" width="12" height="5" rx="1" fill="none" stroke="#fff" strokeWidth="1.6" />
      <rect x="6" y="13" width="12" height="5" rx="1" fill="none" stroke="#fff" strokeWidth="1.6" />
      <circle cx="8.6" cy="8.5" r=".9" fill="#fff" />
      <circle cx="8.6" cy="15.5" r=".9" fill="#fff" />
    </>
  ),
};

const FALLBACK_COLORS = ["#0EA5E9", "#8B5CF6", "#14B8A6", "#F43F5E", "#F59E0B", "#64748B"];

/** Whether a plugin has its own mark (others get a lettered tile). */
export const hasConnectorMark = (pluginId: string) => pluginId in MARKS;

export function ConnectorIcon({ pluginId, name, className }: { pluginId: string; name?: string; className?: string }) {
  const mark = MARKS[pluginId];
  if (mark) {
    return <svg viewBox="0 0 24 24" className={cx("shrink-0", className)} aria-hidden="true">{mark}</svg>;
  }
  // A plugin from the plugins folder: its initial on a color picked from its id.
  const color = FALLBACK_COLORS[[...pluginId].reduce((sum, c) => sum + c.charCodeAt(0), 0) % FALLBACK_COLORS.length];
  return (
    <svg viewBox="0 0 24 24" className={cx("shrink-0", className)} aria-hidden="true">
      <rect x="1" y="1" width="22" height="22" rx="5" fill={color} />
      <text x="12" y="16.5" textAnchor="middle" fontSize="12" fontWeight="700" fill="#fff" fontFamily="system-ui, sans-serif">
        {(name || pluginId).charAt(0).toUpperCase()}
      </text>
    </svg>
  );
}

/** The connection an action's tool ("sheets__read_rows") belongs to. */
export function connectionOfTool<T extends { name: string }>(tool: string | null | undefined, connections: T[]): T | undefined {
  if (!tool || !tool.includes("__")) return undefined;
  const prefix = tool.slice(0, tool.indexOf("__"));
  return connections.find((c) => c.name === prefix);
}
