#!/usr/bin/env bash
#
# gates.sh — run every build/test gate, in the one order that works, from the one directory each needs.
#
# WHY THIS EXISTS. The gates are correct only in a specific order from specific directories, and getting
# it wrong does not fail loudly — it fails GREEN:
#
#   * `dotnet test` from RouteBaseWeb/ targets the non-test project. It runs ZERO tests and exits 0.
#     That is indistinguishable from success by exit code alone, which is why this script asserts on the
#     test-summary line instead of on $? (see require_test_summary).
#   * `npx vitest` from the repo root picks up a config-less vitest; every test dies with
#     "document is not defined".
#   * The first `dotnet build` after any src/js edit fails MSB3030, because Vite rehashes chunk names
#     mid-build. A second build is green. That is not a regression — it is why the frontend build runs
#     first here.
#
# Every path below is resolved from THIS FILE's location, never the caller's cwd, so the script is correct
# no matter where you invoke it from. That is the point: the wrong-directory class becomes impossible
# rather than merely documented.
#
# Usage:
#   scripts/gates.sh              # web gates (build, dotnet, vitest, tsc, eslint, css)
#   scripts/gates.sh --android    # + GpsRecorder lint and unit tests (slow; needs the JDK/SDK toolchain)
#   scripts/gates.sh --matrix     # + hostile-environment reruns of the SAME suites (th-TH culture,
#                                 #   Chatham/Santiago timezones). Green above + red here = latent
#                                 #   environment bug. Fix the code, never the test.
#   scripts/gates.sh --mutation   # + per-file mutation-score ratchet (~16 min). Asks whether the TESTS
#                                 #   are real: it breaks the code on purpose and checks that something
#                                 #   complains. Opt-in for the same reason as --dragnet — far too slow
#                                 #   for a gate you run constantly.
#
# Runs every gate even after one fails, so a single run tells you everything that is broken.
# Exits non-zero if any gate failed.

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WEB="$REPO_ROOT/RouteBaseWeb"
ANDROID="$REPO_ROOT/GpsRecorder"

WITH_ANDROID=0
WITH_MATRIX=0
WITH_DRAGNET=0
WITH_MUTATION=0
for arg in "$@"; do
	case "$arg" in
		--android)  WITH_ANDROID=1 ;;
		--matrix)   WITH_MATRIX=1 ;;
		--dragnet)  WITH_DRAGNET=1 ;;
		--mutation) WITH_MUTATION=1 ;;
		*) echo "unknown flag: $arg (known: --android, --matrix, --dragnet, --mutation)" >&2; exit 2 ;;
	esac
done

FAILED=()
PASSED=()
LOGDIR="$(mktemp -d)"
trap 'rm -rf "$LOGDIR"' EXIT

bold() { printf '\033[1m%s\033[0m\n' "$1"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; PASSED+=("$1"); }
bad()  { printf '  \033[31m✗\033[0m %s\n' "$1"; FAILED+=("$1"); }

# run <name> <workdir> <command...>
run() {
	local name="$1" workdir="$2"; shift 2
	local log="$LOGDIR/${name//[^A-Za-z0-9]/_}.log"
	if ( cd "$workdir" && "$@" ) >"$log" 2>&1; then
		ok "$name"
	else
		bad "$name"
		sed 's/^/      /' "$log" | tail -25
	fi
}

# The dotnet gate needs its own runner: exit code 0 is exactly what the wrong-directory failure mode
# produces, so a summary line ("Passed! - Failed: 0, Passed: N" / "Failed! ...") is the only honest
# evidence that tests actually ran.
# require_test_summary <name> [VAR=value ...] — extra args become environment for this run only
# (the matrix stage reruns the suite under a hostile culture/timezone this way).
require_test_summary() {
	local name="$1"; shift
	local log="$LOGDIR/${name//[^A-Za-z0-9]/_}.log"
	( cd "$REPO_ROOT" && env "$@" dotnet test ) >"$log" 2>&1
	local rc=$?
	local summary
	summary="$(grep -aoE '(Passed|Failed)! +- +Failed: +[0-9]+, +Passed: +[0-9]+, +Skipped: +[0-9]+, +Total: +[0-9]+' "$log" | tail -1)"

	if [[ -z "$summary" ]]; then
		bad "$name — NO TEST SUMMARY (rc=$rc). Zero tests ran; this is the silent false pass."
		tail -20 "$log" | sed 's/^/      /'
		return
	fi
	if (( rc != 0 )); then
		bad "$name — $summary"
		grep -aE '^\s+(Failed|Error)' "$log" | head -15 | sed 's/^/      /'
		return
	fi
	ok "$name — $summary"
}

bold "gates · $REPO_ROOT"

# 1. Frontend build FIRST — otherwise the next dotnet build fails MSB3030.
run "npm run build"        "$WEB" npm run build

# 2. dotnet test from the REPO ROOT (never RouteBaseWeb/ — see header).
require_test_summary "dotnet test"

# 3. vitest from RouteBaseWeb/ (never the repo root — see header).
run "vitest"               "$WEB" npx vitest run

# 4. Typecheck + lint.
run "tsc --noEmit"         "$WEB" npx tsc --noEmit
run "eslint"               "$WEB" npx eslint .
run "lint:css"             "$WEB" npm run lint:css
run "lint:css:dead"        "$WEB" npm run lint:css:dead

# 5. Hostile-environment matrix — opt-in. The SAME suites, under environments latent bugs need:
#    th-TH (Buddhist calendar — 2026 CE formats as 2569; the culture hook is
#    RouteBaseWeb.Tests/MatrixEnvironment.cs, env-gated so the normal run above is untouched),
#    TZ=Pacific/Chatham (+12:45/+13:45 DST, non-integer offset; TimeZoneInfo.Local honors TZ on
#    Linux), TZ=America/Santiago for vitest (southern-hemisphere DST; Node honors TZ directly).
#    Anything green in the default run but red here is a latent environment bug — never fix it
#    by weakening the test.
if (( WITH_MATRIX )); then
	require_test_summary "dotnet test [th-TH, Chatham]" ROUTEBASE_TEST_CULTURE=th-TH TZ=Pacific/Chatham
	run "vitest [Santiago]"    "$WEB" env TZ=America/Santiago npx vitest run
fi

# 6. Analyzer dragnet — opt-in (+27s: analyzers are skipped in normal builds via RunAnalyzers
#    in Directory.Build.props). Severity ledger: root .editorconfig. Adopted rule families sit
#    at severity=error there, so a regression in an adopted family FAILS this stage; untriaged
#    families surface as warnings only.
if (( WITH_DRAGNET )); then
	run "dotnet dragnet"       "$REPO_ROOT" dotnet build -p:Dragnet=true --no-incremental
fi

# 6b. Mutation ratchet — opt-in (~16 min). The dragnet asks "is this code written explicitly?";
#     this asks "are the TESTS real?" by breaking the code on purpose and checking that something
#     complains. Coverage cannot answer that: a test can execute a line and assert nothing.
#     Per-file scores are pinned in RouteBaseWeb.Tests/Architecture/mutation-pins.json and only ever
#     rise. The cheap half — that every pin still points at a file that exists — runs in `dotnet test`
#     as MutationRatchetPinTests, so a stale pin cannot sit here looking green while protecting nothing.
if (( WITH_MUTATION )); then
	run "mutation ratchet"     "$REPO_ROOT" python3 "$REPO_ROOT/scripts/mutation-ratchet.py"
fi

# 7. Android — opt-in. JAVA_HOME/ANDROID_HOME are not on the default PATH here, so export them first;
#    gradle dies with "JAVA_HOME is not set" otherwise. Lint is static-only on WSL: it finds risks but
#    cannot prove a runtime fix, so on-device behaviour stays user-tested.
if (( WITH_ANDROID )); then
	if JAVA_HOME="$(ls -d "$HOME"/toolchain/jdk-21* 2>/dev/null | head -1)" && [[ -n "$JAVA_HOME" ]]; then
		export JAVA_HOME
		export ANDROID_HOME="$HOME/Android/Sdk"
		run "gradle lintDebug"        "$ANDROID" ./gradlew lintDebug
		run "gradle testDebugUnitTest" "$ANDROID" ./gradlew testDebugUnitTest
	else
		bad "android — no JDK 21 under \$HOME/toolchain (expected jdk-21*)"
	fi
fi

echo
bold "summary"
printf '  %d passed\n' "${#PASSED[@]}"
if (( ${#FAILED[@]} )); then
	printf '  \033[31m%d FAILED:\033[0m\n' "${#FAILED[@]}"
	printf '    - %s\n' "${FAILED[@]}"
	exit 1
fi
printf '  \033[32mall gates green\033[0m\n'
