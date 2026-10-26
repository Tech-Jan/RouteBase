// dashboard.ts — Dashboard page entry: wires the feed grid, the widget modules + lazy track thumbnails.
// Widget logic lives in dashboard-{calendar,volume,kpi,widgets,shared}.ts, their numbers in dashboard-stats.ts.

import { readData } from '../core/data-attrs';
import { deleteActivity } from '../core/activity-delete';
import { markCardDeleted } from '../core/mark-card-deleted';
import { setupLazyThumbnails } from '../core/track-thumbnail';
import { createScrollRestore } from '../core/scroll-restore';
import { setupCardPhotos } from '../photos/dashboard-card-photos';
import { initCalendar, renderHeatmap } from './dashboard-calendar';
import { renderWeeklyVolume, computeVolumeOffset } from './dashboard-volume';
import { renderKpi } from './dashboard-kpi';
import { fetchSegments, fetchFriendsRecent } from './dashboard-widgets';
import { initDashboardGrid } from './dashboard-grid';
import type { DashboardGridRow } from '../generated/contracts';
import { MONTH_NAMES, WEEKDAY_NAMES } from './dashboard-shared';

// ─── Greeting + date (PI-03) ────────────────────────────────────────────────
// The server first-paints these from its own clock (UTC in the Docker deployment); overwrite both
// from the browser's local clock so they match the viewer's actual day/time, not the server's.

function renderLocalGreeting(): void {
	const now = new Date();
	const hour = now.getHours();
	const word = hour < 12 ? "Good morning" : hour < 17 ? "Good afternoon" : "Good evening";
	const dateLabel = `${WEEKDAY_NAMES[now.getDay()]} · ${MONTH_NAMES[now.getMonth()]} ${now.getDate()}`;

	const wordEl = document.getElementById("cockpit-greeting-word");
	if (wordEl) wordEl.textContent = word;
	const dateEl = document.getElementById("cockpit-greeting-date");
	if (dateEl) dateEl.textContent = dateLabel;
}

// ─── Lazy track thumbnails ──────────────────────────────────────────────────────

function setupMinimaps(): void {
	setupLazyThumbnails({
		containerSelector: ".minimap[data-track-id]",
		idAttribute: "data-track-id",
		fetchGps: async (trackId) => {
			const resp = await fetch("/api/activities/" + trackId + "/gps-summary");
			if (!resp.ok) throw new Error("GPS fetch failed: " + resp.status);
			return resp.json();
		},
		trackColor: "#22d3ee", // --acc-cyan
	});
}

// ─── Init ─────────────────────────────────────────────────────────────────────

function init(): void {
	// FIRST: it reads history.state synchronously, and initDashboardGrid's onQueryChange rewrites that
	// state one microtask after it returns. Restoring is deferred to after the grid has bound the cards.
	const scroller = document.querySelector<HTMLElement>('.cockpit-scroll');
	const feedScroll = scroller && createScrollRestore({
		scroller,
		stateKey: 'feedScroll',
		anchor: { itemSelector: '.activity-card[data-track-id]', idAttr: 'trackId' },
	});

	renderLocalGreeting();
	setupMinimaps();
	setupCardPhotos();

	// The feed grid's filtered rows are the single input to all three month-bound widgets, so the KPI
	// strip, the weekly bars and the calendar always describe exactly the cards on screen. An empty feed
	// (no bootstrap → initDashboardGrid returns without calling back) leaves rows empty: zero everywhere.
	let rows: DashboardGridRow[] = [];
	let cur = { year: 0, month: 0 };

	initCalendar({
		getRows: () => rows,
		// Calendar owns the selected month; volume + KPI repaint to match on every change.
		onMonthChange: (year, month) => {
			cur = { year, month };
			renderWeeklyVolume(rows, computeVolumeOffset(year, month), year, month);
			renderKpi(rows, year, month);
		},
	});

	// Every filter/sort edit re-runs this. Repainting the heatmap cascades to volume + KPI via
	// onMonthChange, so one call refreshes the whole strip.
	initDashboardGrid((next) => {
		rows = next;
		if (cur.year) renderHeatmap(cur.year, cur.month);
	});

	// Now that the controls strip is built and the cards carry their filter/sort layout, the feed's
	// geometry is final for this frame — restore against it.
	feedScroll?.restore();

	// Second pass. Below 1600px the right rail stacks ABOVE the feed, so these two fetches landing
	// move the whole feed down. restore() recomputes from where the anchor actually is, so this is a
	// no-op when nothing shifted (or when the browser's own scroll anchoring already absorbed it),
	// and it stands down entirely once the user has started scrolling.
	void Promise.all([fetchSegments(), fetchFriendsRecent()]).then(() => feedScroll?.restore());
}

// There is deliberately NO pageshow handler here any more.
//
// This used to be `if (event.persisted) window.location.reload()` — a cargo-culted line (it arrived in
// 9ddb8b95 and was copied verbatim onto Segments in 544b4f1a) that threw away a perfect bfcache restore
// on every Back: the browser hands the page back exactly as it was, scroll position included, and the
// reload replaced that with a flash and a jump.
//
// A bfcached page is a SNAPSHOT — that is the bargain bfcache makes, and it is why Back is instant. The
// cost is that an activity renamed or deleted on the Details page shows its old card until the next
// real navigation: cosmetic, self-healing, and the way the rest of the web behaves. If that ever needs
// fixing, the answer is to revalidate the DATA on pageshow against the server, never to reload the
// document — a client-side "did I change anything" flag cannot see uploads from the phone or a
// background analysis worker, so it would buy far less than it looks like it buys.

if (document.readyState === "loading") {
	document.addEventListener("DOMContentLoaded", init);
} else {
	init();
}

// ─── Delete activity (feed cards) ──────────────────────────────────────────────
// Cards are <div> wrappers with an overlay <a> (stretched-link, FUX-02). The delete button
// carries data-track-id / data-track-name and is wired via a delegated listener (no inline onclick).
document.addEventListener("click", (e: MouseEvent) => {
	const btn = (e.target as HTMLElement).closest<HTMLElement>(".activity-delete-btn");
	if (!btn) return;
	e.preventDefault();
	e.stopPropagation();
	const card = btn.closest<HTMLElement>(".activity-card");
	void deleteActivity(Number(readData(btn, 'trackId')), readData(btn, 'trackName') ?? "", {
		onSuccess: () => markCardDeleted(card),
	});
});

export {};
