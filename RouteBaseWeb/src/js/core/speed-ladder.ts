/**
 * The replay speed ladder — the options behind the −/+ stepper.
 *
 * A fixed ladder cannot work: a 2-minute segment effort and a 2.5-hour race need speeds two orders of
 * magnitude apart. So the ladder is SCALED so that the default option finishes the replay in about
 * `targetSeconds`, and rounded to numbers a human would choose (5x, 120x — never 117x).
 *
 * Extracted from segment-stats-replay-ui so the race replay uses the same ladder rather than a
 * hand-picked one. Pure: no DOM.
 */

export interface SpeedLadder {
	/** Ascending, de-duplicated. */
	readonly speeds: number[];
	/** The option to start on — the one that finishes in roughly `targetSeconds`. */
	readonly defaultSpeed: number;
}

/** Round to a speed a human would pick, with finer granularity at the slow end. */
export function roundSpeed(speed: number): number {
	if (speed < 1) return Math.round(speed * 4) / 4;
	if (speed < 10) return Math.round(speed);
	if (speed < 100) return Math.round(speed / 5) * 5;
	return Math.round(speed / 10) * 10;
}

/**
 * @param maxTimeSeconds  How long the replay lasts at 1×.
 * @param targetSeconds   How long it SHOULD take to watch at the default speed.
 */
export function buildSpeedLadder(maxTimeSeconds: number, targetSeconds = 120): SpeedLadder {
	const base = Math.max(1, Math.round(maxTimeSeconds / targetSeconds));

	const speeds = [
		base * 0.0625, base * 0.125, base * 0.25, base * 0.5,
		base, base * 2, base * 5, base * 10,
		1,   // always offer real time...
		4,   // ...and a gentle 4x, however long the replay is
	]
		.map(roundSpeed)
		.filter((speed) => speed > 0);

	return {
		speeds: [...new Set(speeds)].sort((a, b) => a - b),
		// The ROUNDED base, so the selected option and the speed actually set are the same number.
		defaultSpeed: roundSpeed(base),
	};
}

/** Fill a <select> with the ladder and select the default. */
export function populateSpeedSelect(select: HTMLSelectElement, ladder: SpeedLadder): void {
	select.innerHTML = '';
	for (const speed of ladder.speeds) {
		const option = document.createElement('option');
		option.value = String(speed);
		option.textContent = `${speed}x`;
		if (speed === ladder.defaultSpeed) option.selected = true;
		select.appendChild(option);
	}
}
