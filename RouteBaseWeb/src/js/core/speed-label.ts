/**
 * Speed Label — deciding when a live km/h number is allowed to change.
 *
 * Rounding a moving value to a whole number chatters: a true speed sitting near 24.5 flips
 * 24 → 25 → 24 → 25 forever, and the eye reads that as noise rather than as a speed. The fix is a
 * deadband (a Schmitt trigger), not a timer — the displayed integer only moves once the value has
 * genuinely left it. A steady rider then shows a number that simply sits still.
 *
 * ## No clock in here, on purpose
 *
 * The obvious companion — "hold the number for N ms so fast playback cannot strobe it" — is a second,
 * weaker copy of core/trailing-throttle.ts, and it has a real bug: seeking while paused delivers
 * exactly ONE update, which a hold interval would swallow with no later frame to correct it. That is
 * precisely the case the trailing throttle exists to handle. Rate limiting belongs to the caller,
 * which already owns a repaint budget; this module owns only the question "has it moved enough".
 *
 * A large jump needs no special case either: it clears the deadband on its own and lands immediately
 * on the rounded value, however many whole numbers away that is.
 */

/** Distance past the rounding boundary the value must travel before the label follows it. The
 *  resulting 0.6 km/h deadband is wider than the ~0.4 km/h residual noise of a 5 s window mean, so
 *  boundary chatter cannot occur; the cost is a worst-case display error of 0.8 km/h. */
const DEFAULT_HYSTERESIS_KMH = 0.3;

export interface SpeedLabelOptions {
	hysteresisKmh?: number;
}

export interface SpeedLabel {
	/** The whole km/h to show for this value — the previous one unless the value has left it. */
	read(valueKmh: number): number;
	/** The label has changed SUBJECT (new session, rebuilt effort list). Not needed on a seek. */
	reset(): void;
}

export function createSpeedLabel(options: SpeedLabelOptions = {}): SpeedLabel {
	const band = 0.5 + (options.hysteresisKmh ?? DEFAULT_HYSTERESIS_KMH);
	let shown: number | null = null;

	return {
		read(valueKmh: number): number {
			// Garbage reads as a standstill rather than poisoning the label with NaN.
			const value = Number.isFinite(valueKmh) && valueKmh > 0 ? valueKmh : 0;
			if (shown === null || Math.abs(value - shown) >= band) shown = Math.round(value);
			return shown;
		},
		reset(): void {
			shown = null;
		},
	};
}
