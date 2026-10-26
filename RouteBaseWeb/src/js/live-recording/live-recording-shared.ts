// live-recording-shared.ts — types + display helpers used by both the Record and LiveView pages.

import type { LivePointDto } from '../generated/contracts';

/**
 * GPS point as it crosses the wire — generated from C# LivePointDto. `seq` is the dense server-assigned
 * staging ordinal — the catch-up cursor AND the dedup key (the sole ordering key since `ts` was dropped
 * 2026-07-22, perf-11).
 */
export type PointPayload = LivePointDto;

/**
 * What RecordingHub.JoinSession told the caller (mirrors C# JoinSessionResult). Explicit, not a silent
 * no-op (PI-08): a refused join must not look identical, on the client, to "joined but no GPS yet".
 */
export interface JoinSessionResult {
	ok: boolean;
	reason?: 'not_found' | 'forbidden';
}

/** Terminal wire statuses of a recording session — the stream is over, nothing more arrives.
 *  Single source for the panel's ended-check and the cadence tracker's 'ended' state.
 *  Members typed from the generated vocabulary (D4), so a typo here or a server-side status
 *  rename fails tsc; exported widened to string[] because callers look up raw wire input. */
type SessionStatus = import('../generated/vocabularies').RecordingSessionStatus;
const ENDED: readonly SessionStatus[] = ['stopped', 'finalized', 'abandoned', 'finalize_failed'];
export const ENDED_STATUSES: readonly string[] = ENDED;

export function formatDuration(totalSeconds: number): string {
	const h = Math.floor(totalSeconds / 3600);
	const m = Math.floor((totalSeconds % 3600) / 60);
	const s = totalSeconds % 60;
	const mm = String(m).padStart(2, '0');
	const ss = String(s).padStart(2, '0');
	return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
}

export function startElapsedTimer(startedAt: Date, el: HTMLElement): number {
	function tick(): void {
		const secs = Math.floor((Date.now() - startedAt.getTime()) / 1000);
		el.textContent = formatDuration(secs);
	}
	tick();
	return window.setInterval(tick, 1000);
}
