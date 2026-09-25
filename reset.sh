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

# With accounts on (the default), a reset needs an operator (Auth:PlatformAdmins /
# PLATFORM_ADMIN_EMAIL): sign in first. The session cookie lives in a temporary file only.
jar=$(mktemp)
trap 'rm -f "$jar"' EXIT
mode=$(curl -s "$API_BASE/api/auth/me" | sed -n 's/.*"auth_mode":"\([a-z]*\)".*/\1/p')
if [[ "$mode" == "accounts" ]]; then
  email="${AKTOR_EMAIL:-}"
  password="${AKTOR_PASSWORD:-}"
  [[ -n "$email" ]] || read -r -p "Operator email: " email
  [[ -n "$password" ]] || { read -r -s -p "Password: " password; echo; }
  esc=${password//\\/\\\\}; esc=${esc//\"/\\\"}   # JSON-escape backslashes, then quotes
  body=$(printf '{"email":"%s","password":"%s"}' "$email" "$esc")
  login=$(curl -s -o /dev/null -w "%{http_code}" -c "$jar" -H "Content-Type: application/json" -d "$body" "$API_BASE/api/auth/login")
  [[ "$login" == "200" ]] || { echo "Sign-in failed (HTTP $login)." >&2; exit 1; }
fi

status=$(curl -s -o /dev/null -w "%{http_code}" -b "$jar" -X POST "$API_BASE/api/admin/reset")

if [[ "$status" == "204" ]]; then
  echo "Reset complete."
else
  [[ "$status" == "403" ]] && echo "Only operators can reset (set PLATFORM_ADMIN_EMAIL to your email)." >&2
  echo "Reset failed (HTTP $status). Is the API running at $API_BASE?" >&2
  exit 1
fi
