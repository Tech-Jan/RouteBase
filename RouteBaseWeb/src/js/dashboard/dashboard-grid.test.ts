import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { initDashboardGrid } from './dashboard-grid';

/**
 * The feed's saved scroll position lives in `history.state` (core/scroll-restore.ts), and this grid is
 * the one thing on the dashboard that writes that state: GridView's constructor refreshes, and the
 * refresh ends in `onQueryChange` — so this fires one microtask after initDashboardGrid() returns,
 * before any restore can run. It used to pass `null`, which wiped the anchor on every single load.
 */

const BOOT = {
	rows: [
		{ id: 1, name: 'Morning Run', activityType: 'Run', dateIso: '2026-08-01T07:00:00', distanceMeters: 10000, ascentMeters: 100, timeTotalSeconds: 3600, timeMovingSeconds: 3500, typeGroup: 'run', speedAvgMs: 2.8, heartRateAvg: 140, visibility: 'Private', owner: 'Me', isShared: false, wasShared: false },
		{ id: 2, name: 'Evening Ride', activityType: 'Bike', dateIso: '2026-08-02T18:00:00', distanceMeters: 40000, ascentMeters: 400, timeTotalSeconds: 5400, timeMovingSeconds: 5200, typeGroup: 'bike', speedAvgMs: 7.4, heartRateAvg: 130, visibility: 'Private', owner: 'Me', isShared: false, wasShared: false },
	],
	multiUser: false,
	typeOptions: ['Bike', 'Run'],
	ownerOptions: [],
};

const flush = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

function buildDom(): void {
	document.body.innerHTML = `
		<div class="grid-table-panel">
			<div id="dashboardGridToolbar" class="grid-toolbar"></div>
			<div id="dashboardGrid"></div>
		</div>
		<div class="feed-list">
			<div class="activity-card" data-track-id="1"></div>
			<div class="activity-card" data-track-id="2"></div>
		</div>
		<div id="dashFeedEmpty" hidden></div>
		<script type="application/json" id="dashboardGridBootstrap">${JSON.stringify(BOOT)}</script>`;
}

describe('initDashboardGrid history-state handling', () => {
	beforeEach(() => {
		buildDom();
		history.replaceState(null, '', '/');
	});
	afterEach(() => {
		document.body.innerHTML = '';
		history.replaceState(null, '', '/');
	});

	it('does not wipe the feed scroll anchor another module put in history.state', async () => {
		const feedScroll = { id: 2, dy: 40, top: 8000 };
		history.replaceState({ feedScroll }, '');

		initDashboardGrid(() => {});
		await flush();

		expect(history.state?.feedScroll).toEqual(feedScroll);
	});

	it('still mirrors the query into the URL', async () => {
		initDashboardGrid(() => {});
		await flush();

		expect(location.search).toContain('Sort=date%3Adesc');
	});
});
