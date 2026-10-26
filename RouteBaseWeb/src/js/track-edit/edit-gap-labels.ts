/**
 * edit-gap-labels.ts — human-readable gap/duration formatting for the cut popover (A3 §4.2). The junction
 * seconds for close/synthesize come STRAIGHT from `time-gap.ts` (invariant 9 — the gapSeconds formulas are
 * defined once and only called here); this module never re-derives them. The "real gap left behind" for the
 * default `keep` mode is the raw time span across the removed range, which can be negative for non-monotonic
 * truth (E21) — hence the signed formatter.
 */

import type { ChannelMap } from './apply-script';
import { deleteRangeCloseGap, deleteRangeSynthesizeGap } from './time-gap';

const MINUS = '−'; // typographic minus, matches the popover mock
const SECONDS_PER_HOUR = 3600;

/** Epoch seconds shifted by a tz offset into a Date whose UTC getters read the offset-shifted wall clock
 * (R-10: shared by edit-preview-render's formatTime and add-track-dialog's toLocalDate). */
export function shiftedUtcDate(epochSeconds: number, tzOffsetHours: number): Date {
	return new Date((epochSeconds + tzOffsetHours * SECONDS_PER_HOUR) * 1000);
}

/** Signed duration: "+42 min", "+3 s", "−5 s", "+1.2 h". */
export function formatGap(seconds: number): string {
	const sign = seconds < 0 ? MINUS : '+';
	return `${sign}${magnitude(Math.abs(seconds))}`;
}

/** Unsigned duration for the junction readouts: "4 s", "8 min", "1.2 h". */
export function formatDuration(seconds: number): string {
	return magnitude(Math.abs(seconds));
}

function magnitude(abs: number): string {
	if (abs < 60) return `${Math.round(abs)} s`;
	if (abs < 3600) return `${Math.round(abs / 60)} min`;
	return `${(abs / 3600).toFixed(1)} h`;
}

export interface CutGapLabels {
	/** Raw time span removed (keep mode leaves it behind); signed. */
	realGapSeconds: number;
	realGapLabel: string;
	/** Junction seconds recorded when the op uses timeMode 'close'. */
	closeGapSeconds: number;
	closeLabel: string;
	/** Junction seconds recorded when the op uses timeMode 'synthesize'. */
	synthesizeGapSeconds: number;
	synthesizeLabel: string;
}

/**
 * Build the three cut-popover labels for removing [from..to] from a slot's channels. `keep` shows the real
 * gap (`time[to+1] − time[from−1]`, or `time[to+1] − time[0]` for a head cut); `close`/`synthesize` show the
 * junction each would leave, taken verbatim from the time-gap formulas.
 */
export function cutGapLabels(channels: ChannelMap, from: number, to: number, count: number): CutGapLabels {
	const time = channels.time;
	if (!time || to + 1 >= count) {
		return {
			realGapSeconds: 0, realGapLabel: formatGap(0),
			closeGapSeconds: 0, closeLabel: formatDuration(0),
			synthesizeGapSeconds: 0, synthesizeLabel: formatDuration(0),
		};
	}
	const anchor = from === 0 ? 0 : from - 1;
	// ?? NaN: out-of-bounds typed-array reads are undefined; NaN flows to the same fallback as before.
	const realGapSeconds = (time[to + 1] ?? NaN) - (time[anchor] ?? NaN);
	const closeGapSeconds = deleteRangeCloseGap(time, from);
	const synthesizeGapSeconds = deleteRangeSynthesizeGap(time, channels.distance, from, to);
	return {
		realGapSeconds,
		realGapLabel: formatGap(realGapSeconds),
		closeGapSeconds,
		closeLabel: formatDuration(closeGapSeconds),
		synthesizeGapSeconds,
		synthesizeLabel: formatDuration(synthesizeGapSeconds),
	};
}
