// live-cadence-state.ts — pure "how fresh is this live stream" logic, shared by the LiveView and the
// public share view. No DOM, unit-testable.
//
// Why this exists: the phone uploads every 5 s while a viewer is watching and every 60 s when nobody is,
// and it only LEARNS it is watched from the response to its NEXT upload. So for up to ~60 s after a viewer
// opens, the tip advances at the idle rate. A single recent update therefore CANNOT tell "fast cadence"
// apart from "you tuned in just as an idle upload happened to land" — you have to observe the GAP between
// two updates. This tracker stays 'warming' until it sees two updates close together, and only then 'live'.

import { ENDED_STATUSES } from './live-recording-shared';

export type CadenceState = 'connecting' | 'warming' | 'live' | 'waiting' | 'paused' | 'ended';

export interface CadenceOptions {
	/** A gap between two consecutive updates at or below this proves the fast (watched) cadence. */
	fastMs?: number;
	/** Once fast, this long with no update means the stream stalled (lost signal). */
	staleMs?: number;
}

export interface CadenceTracker {
	/** Record that fresh points for this stream arrived at wall-clock `nowMs`. */
	onUpdate(nowMs: number): void;
	/** The display state for the stream, given its session `status` and the current wall-clock `nowMs`. */
	state(status: string, nowMs: number): CadenceState;
}

export function createCadenceTracker(opts: CadenceOptions = {}): CadenceTracker {
	// 15 s: idle uploads are ~60 s apart and fast ones ~5 s, so 15 s cleanly separates them with margin for
	// poll jitter. 30 s: once fast (5 s), six missed cycles means the stream really stalled.
	const fastMs = opts.fastMs ?? 15_000;
	const staleMs = opts.staleMs ?? 30_000;

	let lastUpdateAt: number | null = null;
	let fastConfirmed = false;

	return {
		onUpdate(nowMs: number): void {
			// Two updates within fastMs prove the watched cadence — the whole reason this tracker exists.
			// Sticky once set: the phone's own 2-min hysteresis keeps it fast while a viewer stays.
			if (lastUpdateAt !== null && nowMs - lastUpdateAt <= fastMs) fastConfirmed = true;
			lastUpdateAt = nowMs;
		},

		state(status: string, nowMs: number): CadenceState {
			if (ENDED_STATUSES.includes(status)) return 'ended';
			if (status === 'paused') return 'paused';
			// recording (or any other non-terminal status):
			if (lastUpdateAt === null) return 'connecting';                 // no points yet at all
			if (!fastConfirmed) return 'warming';                           // one update seen — rate unknown/slow
			return nowMs - lastUpdateAt > staleMs ? 'waiting' : 'live';     // proven fast; 'waiting' if it stalls
		},
	};
}

export interface CadenceBadge {
	/** Uppercase label for the badge. */
	text: string;
	/** The `--{suffix}` modifier on the badge's class (both `lv-status-badge--` and `lv-person-status--`). */
	cls: string;
	/** True only for the confirmed-fast state — the one that earns the pulsing LIVE dot. */
	live: boolean;
	/** True during the pre-live ramp (connecting or warming) — drives the visible "getting live" helper line. */
	ramping: boolean;
	/** Tooltip explaining a non-live state; empty for live/paused/ended. */
	title: string;
}

/** Presentation for a cadence state. Kept beside the tracker so both views label states identically. */
export function cadenceBadge(state: CadenceState): CadenceBadge {
	switch (state) {
		case 'live':
			return { text: 'LIVE', cls: 'live', live: true, ramping: false, title: '' };
		case 'connecting':
			return {
				text: 'CONNECTING', cls: 'warming', live: false, ramping: true,
				title: 'Connecting to the live recording — waiting for the first points.',
			};
		case 'warming':
			// Same visible label as 'connecting': to a viewer both are just "getting ready". The distinct
			// internal state still drives the honest connecting→live promotion (see the tracker above).
			return {
				text: 'CONNECTING', cls: 'warming', live: false, ramping: true,
				title: 'This speeds up to live updates every few seconds — it can take up to a minute after you open the page.',
			};
		case 'waiting':
			return {
				text: 'WAITING', cls: 'waiting', live: false, ramping: false,
				title: 'No new points recently — the recorder may have lost signal.',
			};
		case 'paused':
			return { text: 'PAUSED', cls: 'paused', live: false, ramping: false, title: '' };
		case 'ended':
			return { text: 'ENDED', cls: 'ended', live: false, ramping: false, title: '' };
	}
}
