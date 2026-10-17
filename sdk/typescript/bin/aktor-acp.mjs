#!/usr/bin/env node
// aktor-acp: run Aktor as an Agent Client Protocol (ACP) agent.
//
// ACP clients (OpenClaw through acpx, Zed, other editors) launch an agent as a local process and
// speak newline-delimited JSON-RPC on its stdin/stdout. This bridge is that process: it opens one
// WebSocket to the Aktor server's /acp endpoint, authenticated with an API key, and relays every
// message both ways unchanged. The server runs each prompt as a governed Aktor task.
//
// Usage:
//   AKTOR_API_KEY=ak_... AKTOR_URL=https://agents.example.com npx aktor-acp
//   acpx --agent "npx aktor-acp" "Research the market for ..."
//
// Needs Node 22+ (built-in WebSocket).

const baseUrl = (process.env.AKTOR_URL ?? "http://localhost:5080").replace(/\/$/, "");
const apiKey = process.env.AKTOR_API_KEY;
if (!apiKey) {
  process.stderr.write("aktor-acp: set AKTOR_API_KEY to an Aktor API key (Settings → API keys).\n");
  process.exit(2);
}
if (typeof WebSocket === "undefined") {
  process.stderr.write("aktor-acp: needs Node 22 or later (built-in WebSocket).\n");
  process.exit(2);
}

const url = baseUrl.replace(/^http/, "ws") + "/acp";
// Node's WebSocket (undici) accepts headers as a non-standard option.
const socket = new WebSocket(url, { headers: { Authorization: `Bearer ${apiKey}` } });

const queued = [];
let open = false;

socket.addEventListener("open", () => {
  open = true;
  for (const line of queued.splice(0)) socket.send(line);
});

socket.addEventListener("message", async (event) => {
  const text = typeof event.data === "string" ? event.data : Buffer.from(await event.data.arrayBuffer()).toString("utf8");
  // One JSON-RPC message per line on stdout, as ACP requires.
  for (const line of text.split("\n")) {
    if (line.trim()) process.stdout.write(line.trim() + "\n");
  }
});

socket.addEventListener("close", (event) => {
  if (event.code !== 1000) process.stderr.write(`aktor-acp: connection closed (${event.code} ${event.reason || ""})\n`);
  process.exit(event.code === 1000 ? 0 : 1);
});

socket.addEventListener("error", () => {
  process.stderr.write(`aktor-acp: could not connect to ${url} (check AKTOR_URL and AKTOR_API_KEY)\n`);
});

let buffer = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => {
  buffer += chunk;
  let newline;
  while ((newline = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, newline).trim();
    buffer = buffer.slice(newline + 1);
    if (!line) continue;
    if (open) socket.send(line);
    else queued.push(line);
  }
});
process.stdin.on("end", () => socket.close(1000));
