// chart-empty-state.ts — ONE "this chart has no data" contract for the chart layer.
//
// Empty charts were handled seven different ways: the factory returned null + console.warn
// (a blank canvas, no message), Statistics toggled a #cumulative-empty sibling, Distribution
// toggled a bespoke #distributionEmptyState div, and Progress returned early leaving a BLANK
// canvas with no message at all — the worst case, and exactly the "silent blank chart" the owner
// reported. One contract instead: a centred message overlaid on the chart's host, with the canvas
// hidden (not removed, so Chart.js keeps its box). Visibly-empty by construction.
//
// Styles are inline so the message shows even where no stylesheet targets the class — "visibly
// empty" must not depend on a per-page CSS rule existing.

const EMPTY_CLASS = "chart-empty-state";

/**
 * Show/hide a chart's empty-state message. `host` is the element wrapping the canvas
 * (`.chart-container`, `.cumulative-chart-wrap`, …). When `isEmpty`, a message overlay is
 * shown and the canvas is hidden; otherwise the overlay is hidden and the canvas restored.
 * Idempotent and null-safe.
 */
export function setChartEmptyState(host: HTMLElement | null, message: string, isEmpty: boolean): void {
	if (!host) return;
	const canvas = host.querySelector<HTMLElement>("canvas");
	let el = host.querySelector<HTMLElement>("." + EMPTY_CLASS);

	if (isEmpty) {
		if (typeof getComputedStyle === "function" && getComputedStyle(host).position === "static") {
			host.style.position = "relative"; // the overlay is absolutely positioned
		}
		if (!el) {
			el = document.createElement("div");
			el.className = EMPTY_CLASS;
			Object.assign(el.style, {
				position: "absolute", top: "0", right: "0", bottom: "0", left: "0",
				display: "flex", alignItems: "center", justifyContent: "center",
				textAlign: "center", padding: "1rem", pointerEvents: "none",
				color: "var(--fg-3, #94a3b8)", fontFamily: "var(--font-mono, monospace)", fontSize: "13px",
			});
			host.appendChild(el);
		}
		el.textContent = message;
		el.style.display = "flex";
		if (canvas) canvas.style.visibility = "hidden";
	} else {
		if (el) el.style.display = "none";
		if (canvas) canvas.style.visibility = "";
	}
}
