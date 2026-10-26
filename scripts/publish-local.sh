#!/usr/bin/env bash
#
# publish-local.sh — build the self-contained "localhost mode" package for Linux or macOS.
# (Windows: publish-local-win.ps1.) Single user, no server, no login; data lives in database/ next to the app.
#
# Usage:
#   scripts/publish-local.sh linux-x64       # → artifacts/RouteBase-localhost-linux-x64.tar.gz
#   scripts/publish-local.sh osx-arm64       # Apple Silicon — build on macOS (see below)
#   scripts/publish-local.sh osx-x64         # Intel Mac     — build on macOS (see below)
#
# macOS packages must be built ON macOS: Apple Silicon refuses to run unsigned native code, and the SDK only
# ad-hoc signs the app host reliably when codesign is available. tar.gz, not zip, so the executable bit survives.
# Bash 3.2-compatible on purpose (macOS's /bin/bash).

set -euo pipefail

RID="${1:-}"
case "$RID" in
	linux-x64|linux-arm64) LAUNCHER="start.sh" ;;
	osx-arm64|osx-x64)     LAUNCHER="Start RouteBase.command" ;;   # .command = double-clickable in Finder
	*) echo "usage: scripts/publish-local.sh <linux-x64|linux-arm64|osx-arm64|osx-x64>" >&2; exit 2 ;;
esac

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAGE="$REPO_ROOT/artifacts/localhost-$RID"
APP="$STAGE/RouteBase"
TARBALL="$REPO_ROOT/artifacts/RouteBase-localhost-$RID.tar.gz"

rm -rf "$STAGE" "$TARBALL"
mkdir -p "$STAGE"

# -r on the command line overrides the csproj's publish-only win-x64 RuntimeIdentifier. ReadyToRun off: it only
# speeds up startup, and cross-OS ReadyToRun compilation is the fragile part of a non-Windows publish.
# OpenAPI generation off: it EXECUTES the built app to reflect the API surface, which cannot work when the target
# OS/arch differs from the build machine (osx-x64 on an arm64 Mac, osx-* on Linux) — and a package doesn't need
# the document; it only feeds the dev-time TS contract codegen.
dotnet publish "$REPO_ROOT/RouteBaseWeb/RouteBaseWeb.csproj" -c Release -r "$RID" --self-contained \
	-p:PublishReadyToRun=false -p:OpenApiGenerateDocuments=false -o "$APP"

# The one file that switches the app into localhost mode (kept out of the project dir — see LocalModeGuard).
cp "$REPO_ROOT/scripts/appsettings.Local.json" "$APP/appsettings.Local.json"

cat > "$APP/$LAUNCHER" <<'EOF'
#!/bin/sh
# Starts RouteBase in localhost mode and opens http://localhost:5054 in your browser. Ctrl+C (or closing this
# window) stops it. Your data is in the database/ folder next to this file.
cd "$(dirname "$0")" && exec ./RouteBaseWeb
EOF
chmod +x "$APP/$LAUNCHER" "$APP/RouteBaseWeb"

tar -czf "$TARBALL" -C "$STAGE" RouteBase
echo "Package ready: $TARBALL"
