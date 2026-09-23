#!/usr/bin/env bash
# Builds and starts the full Aktor Agents stack (Postgres + API + dashboard) via Docker Compose.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

if [ ! -f .env ]; then
  echo "No .env found — creating one from .env.example (Mock LLM provider, no API key needed)."
  cp .env.example .env
fi

docker compose up --build "$@"
