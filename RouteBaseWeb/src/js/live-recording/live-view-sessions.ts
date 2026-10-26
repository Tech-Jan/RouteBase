// live-view-sessions.ts — pure state for the LiveView map: no DOM, no network, no MapLibre.
// Owns who is being watched, each person's colour + track, and the point-routing rules.

import type { PointPayload } from './live-recording-shared';
import { must } from '../core/invariant';
import type { LiveSessionDto } from '../generated/contracts';

/**
 * Rider identity colours — the dataviz categorical palette (light column), validated as a set:
 * worst adjacent CVD ΔE 24.2 (target ≥ 12). Assigned in fixed order and never cycled or
 * reassigned: the colour follows the person, so a rider dropping out never repaints the others.
 * The panel names every rider next to their swatch, so identity is never colour-alone.
 */
export const RIDER_COLORS = [
	'#2a78d6', '#1baf7a', '#eda100', '#008300', '#4a3aa7', '#e34948', '#e87ba4', '#eb6834',
] as const;

/** Bounds SignalR groups + map layers per viewer. Mirrors LiveSessionDirectory.MaxWatchable. */
export const MAX_WATCHABLE = RIDER_COLORS.length;

/** One watchable session, as the server describes it — generated from C# LiveSessionDto. The
 *  /sessions/live poll and the lv-sessions-data seed island both emit this exact shape (LiveSessionDto.From),
 *  so the seed parse and the poll cast now share one source of truth. TODO(openapi-review): D16 */
export type LiveSessionMeta = LiveSessionDto;

/** A session the viewer has on the map (or has hidden). */
export interface WatchedSession {
	meta: LiveSessionMeta;
	color: string;
	coords: [number, number][];
	count: number;
	/** Highest staging seq held. -1 = nothing yet. The catch-up cursor; never a timestamp (LV-01). */
	lastSeq: number;
	status: string;
	/** Drawn on the map? Hidden sessions still accumulate points, so re-showing is instant. */
	visible: boolean;
	/** Points that arrive before the catch-up settles are queued, then merged (LR-07). */
	catchupSettled: boolean;
	queued: PointPayload[][];
}

export interface SessionStore {
	/** Registers a session and assigns its colour. Returns null when the watch limit is reached. */
	add(meta: LiveSessionMeta): WatchedSession | null;
	get(sessionId: string): WatchedSession | undefined;
	all(): WatchedSession[];
	/** Routes a live ReceivePoints batch. Returns true when the person's track needs repainting. */
	receive(sessionId: string, points: PointPayload[]): boolean;
	/** Merges the catch-up response with anything queued behind it. Returns true when paintable. */
	settleCatchup(sessionId: string, catchup: PointPayload[]): boolean;
	/** Appends a post-reconnect gap fetch, dropping points already held. Returns true when paintable. */
	appendAfterReconnect(sessionId: string, points: PointPayload[]): boolean;
	setStatus(sessionId: string, status: string): void;
	setVisible(sessionId: string, visible: boolean): void;
}

export function createSessionStore(): SessionStore {
	const sessions = new Map<string, WatchedSession>();
	let colorsUsed = 0; // only ever grows — a freed slot is never handed out again

	/** Appends the points a session does not already hold; returns true if any landed. */
	function appendNew(s: WatchedSession, points: PointPayload[]): boolean {
		let appended = false;
		for (const p of points) {
			if (p.seq <= s.lastSeq) continue; // already held — never double-draw
			s.coords.push([p.lng, p.lat]);
			s.lastSeq = p.seq; // monotonic: the guard above means this never regresses
			appended = true;
		}
		if (appended) s.count = s.coords.length;
		return appended;
	}

	return {
		add(meta: LiveSessionMeta): WatchedSession | null {
			const existing = sessions.get(meta.sessionId);
			if (existing) return existing;
			if (colorsUsed >= MAX_WATCHABLE) return null;

			const session: WatchedSession = {
				meta,
				color: must(RIDER_COLORS[colorsUsed++], 'colorsUsed < MAX_WATCHABLE (guarded above)'),
				coords: [],
				count: 0,
				lastSeq: -1,
				status: meta.status,
				visible: true,
				catchupSettled: false,
				queued: [],
			};
			sessions.set(meta.sessionId, session);
			return session;
		},

		get: (sessionId) => sessions.get(sessionId),

		all: () => [...sessions.values()],

		receive(sessionId, points): boolean {
			const s = sessions.get(sessionId);
			if (!s || points.length === 0) return false;
			// Before the catch-up settles these same points are still in flight from the REST call —
			// painting now would double-draw and double-count (LR-07). Queue, then merge.
			if (!s.catchupSettled) {
				s.queued.push(points);
				return false;
			}
			return appendNew(s, points);
		},

		settleCatchup(sessionId, catchup): boolean {
			const s = sessions.get(sessionId);
			if (!s) return false;
			s.coords = [];
			s.lastSeq = -1;
			s.count = 0;
			appendNew(s, catchup);
			for (const batch of s.queued) appendNew(s, batch);
			s.queued = [];
			s.catchupSettled = true;
			return s.coords.length > 0;
		},

		appendAfterReconnect(sessionId, points): boolean {
			const s = sessions.get(sessionId);
			if (!s) return false;
			return appendNew(s, points);
		},

		setStatus(sessionId, status): void {
			const s = sessions.get(sessionId);
			if (s) s.status = status;
		},

		setVisible(sessionId, visible): void {
			const s = sessions.get(sessionId);
			if (s) s.visible = visible;
		},
	};
}

/**
 * Who the camera may actually centre right now. A rider the viewer *intends* to follow but who has
 * no fix yet (just hit start), or who is hidden, cannot be centred — the camera must stay free until
 * their first point lands, or it would sit pinned on the world view.
 */
export function cameraTarget(followId: string | null, store: SessionStore): string | null {
	if (!followId) return null;
	const session = store.get(followId);
	return session && session.visible && session.coords.length > 0 ? followId : null;
}

/**
 * Who the camera follows on load: yourself if you are in the selection, otherwise the rider who
 * started earliest. Null only when nothing is being watched.
 */
export function pickInitialFollow(metas: LiveSessionMeta[]): string | null {
	if (metas.length === 0) return null;
	const self = metas.find((m) => m.isSelf);
	if (self) return self.sessionId;
	return metas.reduce((a, b) => (a.startedAt <= b.startedAt ? a : b)).sessionId;
}
