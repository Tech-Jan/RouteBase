#!/usr/bin/env bash
# check-contracts-drift.sh — the enforcement that makes C#↔TS contracts change atomically.
#
# Regenerates the OpenAPI document (from the C# DTOs, at build) and the TS contracts, then fails if either
# differs from what is committed. A C# DTO edit not reflected in the checked-in TS becomes a RED build.
#
# Run from RouteBaseWeb/. Wire into CI after `dotnet build`/`vitest`. Exit 1 on drift, 0 when in sync.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "[drift] building (emits openapi/RouteBaseWeb.json via ApiDescription.Server)…"
dotnet build -v minimal --nologo >/dev/null

echo "[drift] regenerating TS contracts…"
npm run --silent gen:contracts

# git diff (no HEAD) compares the regenerated worktree against the INDEX — deliberately: after a legit
# regen you stage the result, and the gate must then pass; in CI's clean checkout index == HEAD anyway.
echo "[drift] diffing regenerated artifacts against the staged/committed state…"
if ! git diff --exit-code -- openapi/RouteBaseWeb.json src/js/generated/; then
  echo ""
  echo "❌ Generated contracts are STALE. A C# DTO changed without regenerating the TS mirror."
  echo "   Fix: run \`npm run gen:contracts\` (after \`dotnet build\`) and commit the result."
  exit 1
fi
echo "✅ contracts in sync with the C# DTOs."
