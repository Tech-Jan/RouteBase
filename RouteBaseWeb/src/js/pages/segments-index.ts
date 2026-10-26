// segments-index.ts — Segments list page: lazy-loading track thumbnails + Analyze All + delete

import { asyncCallback } from '../core/async-listener';
import { readData } from '../core/data-attrs';
import { setupLazyThumbnails } from "../core/track-thumbnail";
import { createScrollRestore } from "../core/scroll-restore";
import { markCardDeleted } from "../core/mark-card-deleted";
import { startAnalysisPolling } from "../core/op-polling-bridge";
import { OP_TOAST_HIDDEN_EVENT } from "../core/dom-events";
import type { EffortsSummary, SegmentOutline } from "../core/core-types";
import type { ApiError } from "../generated/contracts";

// ── Lazy-loading canvas track thumbnails ──────────────────────────────────────

// Live "X / Y maps loaded" counter — thumbnails load lazily on scroll, so the count climbs
// as cards enter view (mirrors the activities-overview track counter).
const minimapStatus = document.getElementById("segMinimapStatus");

setupLazyThumbnails({
	containerSelector: ".segment-minimap[data-segment-id]",
	idAttribute: "data-segment-id",
	fetchGps: async (segmentId) => {
		const response = await fetch("/api/SegmentsApi/" + segmentId + "/gps-summary");
		if (!response.ok) throw new Error("HTTP " + response.status);
		return response.json();
	},
	// Shape/gate segments have no GPS summary — trace the matched efforts that ran through them.
	fetchEfforts: async (segmentId) => {
		const response = await fetch("/api/SegmentsApi/" + segmentId + "/efforts-summary");
		if (!response.ok) throw new Error("HTTP " + response.status);
		const data: EffortsSummary = await response.json();
		return data.efforts.map((e) => e.points);
	},
	// Zero-effort fallback (and context under the efforts): the zone rectangles + gate lines.
	fetchShapes: async (segmentId) => {
		const response = await fetch("/api/SegmentsApi/" + segmentId + "/shapes");
		if (!response.ok) throw new Error("HTTP " + response.status);
		const body: SegmentOutline = await response.json();
		return body;
	},
	trackColor: "#007bff",
	trackWeight: 3,
	onProgress: (loaded, total) => {
		if (minimapStatus)
			minimapStatus.textContent = "(" + loaded + " / " + total + " maps loaded)";
	},
});

// No pageshow handler, for the reason spelled out in dashboard/dashboard.ts: a bfcache restore already
// hands this page back exactly as it was, and the reload this used to do threw that away on every Back.

// ── Scroll position across Back ───────────────────────────────────────────────
// The page scrolls in .cockpit-scroll, not the document, so the browser restores nothing on its own.
// One pass is enough here: overview/segments-grid.js is a separate module loaded BEFORE this one, so
// its cards are already hidden/reordered by the time this runs, and nothing on the page loads
// asynchronously above the list (unlike the dashboard's right rail).
const segScroller = document.querySelector<HTMLElement>(".cockpit-scroll");
if (segScroller) {
	createScrollRestore({
		scroller: segScroller,
		stateKey: "segScroll",
		anchor: { itemSelector: ".seg-card[data-segment-id]", idAttr: "segmentId" },
	}).restore();
}

// ── Analyze All Segments button ────────────────────────────────────────────

const analyzeBtn = document.getElementById("analyzeAllSegmentsBtn") as HTMLButtonElement | null;
analyzeBtn?.addEventListener("click", asyncCallback('analyze all segments', async function (this: HTMLButtonElement) {
	const btn = this;
	if (
		!confirm(
			"\u26a0 This will re-analyze ALL segments across ALL activities.\n\nThis runs in the background \u2014 you can continue browsing.\n\nContinue?",
		)
	) return;

	try {
		const response = await fetch("/api/SegmentsApi/analyze-all", {
			method: "POST",
			headers: { "Content-Type": "application/json" },
		});

		// 409 = a run is already going. That is the same end state the caller wanted, so it takes the
		// same branch: adopt the run. The alert it used to raise ("…check the progress bar at the bottom
		// right") blocked the page to point at the very toast startAnalysisPolling was about to show.
		if (response.ok || response.status === 202 || response.status === 409) {
			btn.disabled = true;
			btn.innerHTML = '<span class="btn-spinner"></span> Running…';
			startAnalysisPolling();
			document.addEventListener(OP_TOAST_HIDDEN_EVENT, function onDone() {
				document.removeEventListener(OP_TOAST_HIDDEN_EVENT, onDone);
				btn.disabled = false;
				btn.innerHTML = '<i class="bi bi-lightning-charge"></i> Analyze All';
			});
		} else {
			const error: Partial<ApiError> = await response.json();
			alert(`\u274c Error: ${error.message}`);
		}
	} catch (error: unknown) {
		console.error("\u274c Error starting analysis:", error);
		const msg = error instanceof Error ? error.message : String(error);
		alert(`\u274c Network error: ${msg}`);
	}
}));

// ── Delete segment ─────────────────────────────────────────────────────────
// Cards are <div> wrappers with an overlay <a> (stretched-link, FUX-02). The delete button
// carries data-segment-id / data-segment-name and is wired via a delegated listener (no inline onclick).

async function deleteSegment(
	segmentId: number,
	segmentName: string,
	card: HTMLElement | null,
): Promise<void> {
	if (!confirm(`Are you sure you want to delete segment "${segmentName || segmentId}"?`)) return;
	try {
		const response = await fetch("/api/SegmentsApi/" + segmentId, { method: "DELETE" });
		if (response.ok) {
			markCardDeleted(card);
		} else {
			alert("Error deleting segment");
		}
	} catch (error: unknown) {
		console.error("Error:", error);
		const msg = error instanceof Error ? error.message : String(error);
		alert("Error deleting segment: " + msg);
	}
}

document.addEventListener("click", (e: MouseEvent) => {
	const btn = (e.target as HTMLElement).closest<HTMLElement>(".seg-delete-btn");
	if (!btn) return;
	e.preventDefault();
	e.stopPropagation();
	const card = btn.closest<HTMLElement>(".seg-card");
	void deleteSegment(Number(readData(btn, 'segmentId')), readData(btn, 'segmentName') ?? "", card);
});

export {};
