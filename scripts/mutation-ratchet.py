#!/usr/bin/env python3
"""
mutation-ratchet.py — pin per-file mutation scores so test QUALITY cannot decay silently.

WHY THIS EXISTS. Line coverage counts lines a test *executed*; it cannot see a test that executes a
line and asserts nothing. Mutation testing can: it breaks the code on purpose and checks whether any
test complains. The 2026-08-02 mutation-testing pass bought ~200 such tests. Nothing stopped that decaying —
the same way 830 analyzer warnings accumulated before the culture family was pinned at severity=error.
This is that pin, for test quality.

WHY IT IS NOT IN `dotnet test`. A full Stryker run over Services/Tracks + Services/Segments takes
~16 minutes (19,903 mutants created, ~1,450 actually tested). A gate you run constantly must stay
fast, so this mirrors the analyzer dragnet exactly: expensive and OPT-IN (`gates.sh --mutation`),
with a cheap backstop inside `dotnet test` (MutationRatchetPinTests) that catches pin-file rot —
a renamed or deleted file whose pin silently stops meaning anything.

SCORE DEFINITION — Stryker's own, verified against its reported 74.07% on the 2026-08-02 baseline:

    score = (Killed + Timeout) / (Killed + Timeout + Survived + NoCoverage) * 100

A Timeout is a DETECTION (the mutant hung the test, which is a test noticing). CompileError and
Ignored mutants are excluded from both halves — they are not valid mutants, and counting them would
make the score move when the compiler changes rather than when the tests do.

PINS ONLY EVER RISE. Lowering one is a deliberate act that needs a written reason in the same commit,
exactly like the file-size and culture ratchets. Raising them on improvement is the convention.

Usage:
    scripts/mutation-ratchet.py                      # run Stryker, then compare (slow, ~16 min)
    scripts/mutation-ratchet.py --report <path.json> # compare an existing report (fast, no Stryker)
    scripts/mutation-ratchet.py --update             # rewrite the pins from a fresh run (review the diff!)

Exit codes: 0 = every pinned file met its pin. 1 = a regression, or a pinned file vanished.
"""
import argparse
import json
import os
import subprocess
import sys
import tempfile

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PINS_PATH = os.path.join(REPO_ROOT, "RouteBaseWeb.Tests", "Architecture", "mutation-pins.json")
TESTS_DIR = os.path.join(REPO_ROOT, "RouteBaseWeb.Tests")

DETECTED = {"Killed", "Timeout"}
UNDETECTED = {"Survived", "NoCoverage"}

# The subtrees the pins cover. Kept in one place so the Stryker invocation and the "did the report
# cover what we pinned" check cannot drift apart.
MUTATE_GLOBS = ["**/Services/Tracks/**/*.cs", "**/Services/Segments/**/*.cs"]


def score_of(mutants):
    """Stryker's mutation score for one file, or None when the file has no valid mutants."""
    det = sum(1 for m in mutants if m["status"] in DETECTED)
    und = sum(1 for m in mutants if m["status"] in UNDETECTED)
    if det + und == 0:
        return None, det, und
    return round(100.0 * det / (det + und), 2), det, und


def read_report(path):
    """{repo-relative path: (score, detected, undetected)} for every file with valid mutants."""
    with open(path) as fh:
        doc = json.load(fh)
    out = {}
    for abs_path, entry in doc["files"].items():
        score, det, und = score_of(entry["mutants"])
        if score is None:
            continue
        rel = abs_path.replace("\\", "/")
        marker = "/RouteBaseWeb/"
        if marker in rel:
            rel = "RouteBaseWeb/" + rel.split(marker, 1)[1]
        out[rel] = (score, det, und)
    return out


def run_stryker(out_dir):
    cmd = ["dotnet", "stryker", "--project", "RouteBaseWeb.csproj"]
    for g in MUTATE_GLOBS:
        cmd += ["--mutate", g]
    cmd += ["--output", out_dir, "--reporter", "json", "--reporter", "progress"]
    env = dict(os.environ)
    # dotnet global tools are not on the default PATH in non-interactive shells — a scripted runner's
    # fixed PATH omits them, which cost a job an hour once. Add it here rather than at every call site.
    env["PATH"] = os.path.expanduser("~/.dotnet/tools") + os.pathsep + env.get("PATH", "")
    print(f"running stryker (~16 min) → {out_dir}", flush=True)
    rc = subprocess.call(cmd, cwd=TESTS_DIR, env=env)
    if rc != 0:
        print(f"stryker exited {rc}", file=sys.stderr)
        return None
    for root, _dirs, files in os.walk(out_dir):
        if "mutation-report.json" in files:
            return os.path.join(root, "mutation-report.json")
    print("stryker produced no mutation-report.json", file=sys.stderr)
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--report", help="compare an existing mutation-report.json instead of running Stryker")
    ap.add_argument("--update", action="store_true", help="rewrite the pins from the measured scores")
    args = ap.parse_args()

    report_path = args.report
    tmp = None
    if not report_path:
        tmp = tempfile.mkdtemp(prefix="mutation-ratchet-")
        report_path = run_stryker(tmp)
        if not report_path:
            return 1

    measured = read_report(report_path)
    with open(PINS_PATH) as fh:
        pins_doc = json.load(fh)
    pins = pins_doc["pins"]

    if args.update:
        pins_doc["pins"] = {k: measured[k][0] for k in sorted(measured)}
        pins_doc["measuredFrom"] = os.path.basename(report_path)
        with open(PINS_PATH, "w") as fh:
            json.dump(pins_doc, fh, indent=2, sort_keys=False)
            fh.write("\n")
        print(f"pins rewritten from {len(measured)} files — REVIEW THE DIFF; pins must not silently drop")
        return 0

    regressions, missing, improved = [], [], []
    for path, pinned in sorted(pins.items()):
        if path not in measured:
            missing.append(path)
            continue
        actual = measured[path][0]
        if actual + 1e-9 < pinned:
            regressions.append((path, pinned, actual, measured[path]))
        elif actual > pinned:
            improved.append((path, pinned, actual))

    for path, pinned, actual, (_s, det, und) in regressions:
        print(f"REGRESSION {path}: pinned {pinned:.2f}% → {actual:.2f}%  ({det} detected / {und} undetected)")
    for path in missing:
        print(f"MISSING    {path}: pinned but absent from the report — renamed, deleted, or outside the mutate globs")
    for path, pinned, actual in improved:
        print(f"improved   {path}: {pinned:.2f}% → {actual:.2f}%  (raise the pin)")

    total_det = sum(v[1] for v in measured.values())
    total_und = sum(v[2] for v in measured.values())
    overall = 100.0 * total_det / (total_det + total_und) if total_det + total_und else 0.0
    print(f"\n{len(pins)} pinned · {len(regressions)} regressions · {len(missing)} missing · "
          f"{len(improved)} improved · overall {overall:.2f}%")

    if regressions or missing:
        print("\nA pin may only be lowered deliberately, with a written reason in the same commit.")
        return 1
    print("mutation ratchet OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
