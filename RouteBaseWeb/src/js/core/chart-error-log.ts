// chart-error-log.ts — one logged failure path for the chart layer.
//
// Chart draws are wrapped in defensive try/catch so a stale chart mid-teardown can't
// crash the page. Historically those catches were SILENT ({}), so a real draw bug
// presented to the owner as "the crosshair just stopped working" with nothing in the
// console. This routes every such catch through one place that logs — the page still
// does not crash (callers keep their fallback), but the failure is visible.
//
// Throttled per label (a rebuilding chart can fail on every rAF frame): at most one
// line per label per window, so a genuine break surfaces without spamming 60 lines/s.

const THROTTLE_MS = 2000;
const _lastLogged = new Map<string, number>();

function _now(): number {
	return typeof performance !== "undefined" && typeof performance.now === "function"
		? performance.now()
		: 0;
}

/**
 * Log a swallowed chart-layer failure, throttled per `label`. `label` should name the
 * module + entry point (e.g. "chart-overlay:crosshair") so the console line points at
 * the failing surface. Never throws — safe to call from inside a catch on any path.
 */
export function reportChartError(label: string, err: unknown): void {
	const t = _now();
	const last = _lastLogged.get(label);
	if (last != null && t - last < THROTTLE_MS) return;
	_lastLogged.set(label, t);
	// console.warn (not error): these are handled/non-fatal by contract — the caller's
	// fallback already kept the page alive. We only want them to stop being invisible.
	console.warn(`[chart] ${label} failed`, err);
}

/** Test seam: clear the per-label throttle state between cases. */
export function _resetChartErrorLog(): void {
	_lastLogged.clear();
}
