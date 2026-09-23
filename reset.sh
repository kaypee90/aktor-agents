#!/usr/bin/env bash
# Clears all tasks, agents, messages, events, tool calls, artifacts, and the live agent registry,
# so the next submitted goal starts from a clean slate. The API and Postgres containers keep
# running — this hits POST /api/admin/reset rather than tearing the stack down.
set -euo pipefail

API_BASE="${API_BASE:-http://localhost:5080}"

if [[ "${1:-}" != "-y" ]]; then
  read -r -p "This permanently deletes all tasks, agents, and artifacts at $API_BASE. Continue? [y/N] " confirm
  [[ "$confirm" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 0; }
fi

status=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$API_BASE/api/admin/reset")

if [[ "$status" == "204" ]]; then
  echo "Reset complete."
else
  echo "Reset failed (HTTP $status). Is the API running at $API_BASE?" >&2
  exit 1
fi
