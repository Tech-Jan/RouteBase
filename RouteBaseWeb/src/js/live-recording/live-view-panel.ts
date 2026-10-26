// live-view-panel.ts — LiveView DOM: the rider rows, the KPI strip and the status badges.
// Owns no state; every render is a pure read of a WatchedSession.

import { setData } from '../core/data-attrs';
import { must } from '../core/invariant';
import { ENDED_STATUSES, formatDuration } from './live-recording-shared';
import { cadenceBadge, type CadenceState } from './live-cadence-state';
import type { WatchedSession } from './live-view-sessions';

export function isEnded(status: string): boolean { return ENDED_STATUSES.includes(status); }

export interface PanelCallbacks {
	/** The rider's map checkbox was ticked / unticked. */
	onToggleVisible(sessionId: string, visible: boolean): void;
	/** The rider's name was clicked — follow them. */
	onFollow(sessionId: string): void;
	/**
	 * A recording rider's cadence state (Connecting / Warming up / Live / Waiting), so the badge reflects
	 * whether updates are actually flowing rather than showing "LIVE" the instant a stream exists. Optional:
	 * when absent the panel falls back to the plain status label.
	 */
	cadenceOf?(session: WatchedSession): CadenceState;
	/**
	 * A rider's ACTIVE riding seconds — ticks while recording, freezes while paused, holds a final value
	 * once ended — so the elapsed cell reflects riding time consistently with the public share view
	 * (FE-08/FE-12). Optional: when absent the panel falls back to wall-clock since start.
	 */
	elapsedOf?(session: WatchedSession): number;
}

export interface RiderPanel {
	/** Clones the row template for a rider. Idempotent. */
	addRow(session: WatchedSession): void;
	hasRow(sessionId: string): boolean;
	/** Repaints a rider's mutable cells (points, status, elapsed, activity link). */
	updateRow(session: WatchedSession): void;
	/** Marks one row as followed; pass null for free camera. */
	setFollowed(sessionId: string | null): void;
	/** Mirrors the followed rider into the KPI strip. */
	renderKpis(session: WatchedSession | null): void;
	/** LIVE / PAUSED / ENDED for the page header, plus the finished banner. */
	renderPageStatus(sessions: WatchedSession[]): void;
	markStale(): void;
	/** Drops every row listener. Idempotent — the page's dispose() may run twice (bfcache). */
	dispose(): void;
}

/** A rendered rider row plus the two listeners bound to it, so teardown can take them off again. */
interface PanelRow {
	el: HTMLElement;
	onFollow: () => void;
	onToggle: () => void;
}

export function createRiderPanel(
	listEl: HTMLElement,
	template: HTMLTemplateElement,
	cb: PanelCallbacks,
): RiderPanel {
	const rows = new Map<string, PanelRow>();

	// must(): LiveView.cshtml renders every one of these ids unconditionally — absence is a
	// template regression, and the constructor has nothing sane to return without them.
	const statusBadge = must(document.getElementById('lv-status-badge'), 'LiveView #lv-status-badge');
	const liveHint = document.getElementById('lv-live-hint');
	const stoppedBanner = must(document.getElementById('lv-stopped-banner'), 'LiveView #lv-stopped-banner');
	const activityLink = document.getElementById('lv-activity-link') as HTMLAnchorElement;
	const elapsedEl = must(document.getElementById('lv-elapsed'), 'LiveView #lv-elapsed');
	const pointsEl = must(document.getElementById('lv-points'), 'LiveView #lv-points');
	const typeEl = must(document.getElementById('lv-type'), 'LiveView #lv-type');
	const startedEl = must(document.getElementById('lv-started'), 'LiveView #lv-started');

	// #lv-status-badge is a role="status" live region (LiveView.cshtml). The page's 1 s tickClock re-runs
	// renderPageStatus every second, so writing identical text each tick would make a screen reader
	// re-announce the same state once a second. Only touch the DOM when the rendered state CHANGES — the
	// same change-guard the public share view proved (share-live-view.ts lastBadgeSig). markStale routes
	// through it too, so its STALE state updates the signature and a later reconnect repaints (no stuck STALE).
	let lastBadgeSig = '';
	function paintStatusBadge(cls: string, text: string, title: string, ramping: boolean): void {
		const sig = `${cls}|${text}|${title}|${ramping}`;
		if (sig === lastBadgeSig) return;
		lastBadgeSig = sig;
		statusBadge.className = 'lv-status-badge lv-status-badge--' + cls;
		statusBadge.textContent = text;
		statusBadge.title = title;
		if (liveHint) liveHint.hidden = !ramping;
	}

	function elapsedText(session: WatchedSession): string {
		// Active riding time: frozen while paused, final once ended — no longer blanked to --:-- (FE-08/FE-12).
		if (cb.elapsedOf) return formatDuration(cb.elapsedOf(session));
		const secs = Math.floor((Date.now() - new Date(session.meta.startedAt).getTime()) / 1000);
		return formatDuration(Math.max(0, secs));
	}

	function query<T extends HTMLElement>(row: HTMLElement, cls: string): T {
		return must(row.querySelector<T>(cls), `person-template ${cls}`);
	}

	return {
		addRow(session: WatchedSession): void {
			if (rows.has(session.meta.sessionId)) return;

			const row = must(template.content.firstElementChild, 'person-template root element').cloneNode(true) as HTMLElement;
			setData(row, 'sessionId', session.meta.sessionId);
			query(row, '.lv-person-swatch').style.backgroundColor = session.color;

			const name = query<HTMLButtonElement>(row, '.lv-person-name');
			name.textContent = session.meta.displayName;
			const onFollow = (): void => cb.onFollow(session.meta.sessionId);
			name.addEventListener('click', onFollow);

			query(row, '.lv-person-type').textContent = session.meta.activityType;

			const show = query<HTMLInputElement>(row, '.lv-person-show');
			show.checked = session.visible;
			const onToggle = (): void => cb.onToggleVisible(session.meta.sessionId, show.checked);
			show.addEventListener('change', onToggle);

			listEl.appendChild(row);
			rows.set(session.meta.sessionId, { el: row, onFollow, onToggle });
			this.updateRow(session);
		},

		hasRow: (sessionId) => rows.has(sessionId),

		updateRow(session: WatchedSession): void {
			const row = rows.get(session.meta.sessionId)?.el;
			if (!row) return;

			query(row, '.lv-person-points').textContent = String(session.count);
			query(row, '.lv-person-elapsed').textContent = elapsedText(session);

			const badge = query(row, '.lv-person-status');
			const ended = isEnded(session.status);
			// PI-08: a refused JoinSession is 'unavailable' — distinct from LIVE-with-zero-points (still
			// waiting for a GPS fix), which the viewer would otherwise read as the exact same "nothing yet".
			const unavailable = session.status === 'unavailable';
			if (unavailable) {
				badge.textContent = 'UNAVAILABLE';
				badge.className = 'lv-person-status lv-person-status--unavailable';
				badge.title = 'Live view unavailable for this rider';
			} else if (!ended && session.status !== 'paused' && cb.cadenceOf) {
				// A recording rider: honest cadence (Connecting → Warming up → Live) rather than an
				// instant "LIVE" that a coincidental idle upload would fake.
				const b = cadenceBadge(cb.cadenceOf(session));
				badge.textContent = b.text;
				badge.className = 'lv-person-status lv-person-status--' + b.cls;
				badge.title = b.title;
			} else {
				badge.textContent = ended ? 'ENDED' : session.status === 'paused' ? 'PAUSED' : 'LIVE';
				badge.className = 'lv-person-status lv-person-status--'
					+ (ended ? 'ended' : session.status === 'paused' ? 'paused' : 'live');
				badge.title = '';
			}

			const link = query<HTMLAnchorElement>(row, '.lv-person-activity');
			link.hidden = !ended || !session.meta.trackId;
			link.href = `/Activities/Details/${session.meta.trackId}`;
		},

		setFollowed(sessionId: string | null): void {
			for (const [id, row] of rows) row.el.classList.toggle('lv-person--followed', id === sessionId);
		},

		renderKpis(session: WatchedSession | null): void {
			if (!session) {
				elapsedEl.textContent = '--:--';
				pointsEl.textContent = '0';
				typeEl.textContent = '—';
				startedEl.textContent = '--:--';
				return;
			}
			elapsedEl.textContent = elapsedText(session);
			pointsEl.textContent = String(session.count);
			typeEl.textContent = session.meta.activityType;
			startedEl.textContent = new Date(session.meta.startedAt)
				.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
		},

		renderPageStatus(sessions: WatchedSession[]): void {
			const allEnded = sessions.length > 0 && sessions.every((s) => isEnded(s.status));

			const cadenceOf = cb.cadenceOf;
			if (cadenceOf) {
				// Aggregate the riders' cadence: LIVE only if at least one is confirmed-fast, else "Warming
				// up" while any is still ramping — so the header never claims LIVE before updates flow.
				// 'unavailable' rows (refused joins, PI-08) have no cadence to measure — through the tracker
				// they read as 'connecting', which left an all-refused page stuck on the pre-live badge forever
				// (FE-04). They don't vote; all-unavailable falls through to ENDED like the plain branch.
				const states = sessions
					.filter((s) => s.status !== 'unavailable')
					.map((s) => cadenceOf(s));
				const page: CadenceState =
					states.includes('live') ? 'live'
						: states.some((s) => s === 'connecting' || s === 'warming') ? 'warming'
							: states.includes('waiting') ? 'waiting'
								: states.includes('paused') ? 'paused'
									: 'ended';
				const b = cadenceBadge(page);
				// Plain-language "getting live" line, visible through the ramp only (tooltips don't show on phones).
				paintStatusBadge(b.cls, b.text, b.title, b.ramping);
			} else {
				const anyLive = sessions.some((s) => s.status === 'recording');
				const anyPaused = sessions.some((s) => s.status === 'paused');
				// title '' → a reconnect clears the STALE tooltip markStale left behind; ramping false → no
				// cadence signal here, nothing to ramp.
				paintStatusBadge(
					anyLive ? 'live' : anyPaused ? 'paused' : 'ended',
					anyLive ? 'LIVE' : anyPaused ? 'PAUSED' : 'ENDED',
					'',
					false,
				);
			}

			stoppedBanner.style.display = allEnded ? '' : 'none';
			if (allEnded) {
				// Per-rider rows carry their own activity links; the banner points at the first finished one.
				const first = sessions.find((s) => s.meta.trackId > 0);
				if (first) activityLink.href = `/Activities/Details/${first.meta.trackId}`;
			}
		},

		markStale(): void {
			paintStatusBadge('stale', 'STALE', 'Live updates interrupted — reload to resync.', false);
		},

		dispose(): void {
			for (const { el, onFollow, onToggle } of rows.values()) {
				el.querySelector('.lv-person-name')?.removeEventListener('click', onFollow);
				el.querySelector('.lv-person-show')?.removeEventListener('change', onToggle);
			}
			rows.clear();
		},
	};
}
