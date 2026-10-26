/**
 * replay-transport.ts — the shared replay TRANSPORT (play/pause, scrubber, speed stepper, elapsed clock).
 *
 * The markup (_ReplayDock.cshtml), the CSS skin (replay-controls.css), the frame clock
 * (replay-playback-shell.ts), the follow camera and the drawer were already single-sourced and stable.
 * What each of the three replay pages still hand-rolled — and quietly drifted on — was the ~40 lines that
 * wire those dock ids to a running replay: the seek slider, the play button, the speed −/+ stepper and the
 * elapsed readout. This is that wiring, once, so a fix lands on all three:
 *
 *   a. Slider write-back GUARD. render() writes slider.value every frame so the thumb tracks the playhead —
 *      but NOT while the user is dragging it, or the playhead fights the thumb. The drag flag is an explicit
 *      pointerdown/up on the range input, NOT document.activeElement: iOS Safari does not reliably focus a
 *      range input on touch, so the activeElement guard the race used was dead on a phone.
 *   b. Seek COALESCING. A drag fires far more `input` events than there are frames, and every seek is
 *      expensive (a full rescan of the field / every effort's cursor). One rAF-coalesced listener applies
 *      only the latest value, once per frame — so all three pages get the fix the activity replay never had.
 *   d. ONE elapsed formatter — core/format.formatElapsed for every replay clock (see render()).
 *
 * Domain differences stay in the pages, behind hooks: what a scrub MEANS (seek by track index / by
 * pct-of-maxTime / by pct-of-endSeconds), how play/pause toggles, and how long the timeline is for the
 * speed ladder. This controller knows only the dock.
 */

import { setupSpeedStepper } from './map-utils-shared';
import { buildSpeedLadder, populateSpeedSelect, type SpeedLadder } from './speed-ladder';
import { formatElapsed } from './format';
import { devicePrefKeys, getNumber, setNumber } from './device-prefs';

/** The _ReplayDock element ids for one page's transport. Passed whole so each greps (repo rule). */
export interface ReplayTransportIds {
	playPauseId: string;
	seekId: string;
	currentTimeId: string;
	speedSelectId: string;
	speedSlowerId: string;
	speedFasterId: string;
	speedLabelId: string;
	/**
	 * Names this replay KIND in the device-pref registry ('activity' | 'segment' | 'race'). Per kind and
	 * not per ride: what a viewer wants remembered is how they like to watch a race, not how fast they
	 * once watched one particular one.
	 */
	speedPrefKey: string;
}

export interface ReplayTransportHooks {
	/**
	 * Apply a scrub to `percent` (0..100). Called at most ONCE per animation frame (coalesced) — the page's
	 * seek is free to be as expensive as it likes.
	 */
	seekToPercent(percent: number): void;
	/** Toggle play/pause and return the NEW isPlaying, so the transport can flip the icon to match. */
	togglePlayPause(): boolean;
	/** The speed changed (via the −/+ stepper or the hidden <select>). */
	setSpeed(mult: number): void;
}

export interface ReplayTransport {
	/**
	 * Push the live playhead to the scrubber + elapsed clock. Call every progress frame.
	 * @param percent         0..100, the scrubber position. Written back unless the user is dragging (a).
	 * @param elapsedSeconds  the clock reading, formatted with the ONE replay-clock formatter (d).
	 * @param labelOverride   replaces the formatted time verbatim (the segment's % sync mode shows "42%").
	 */
	render(percent: number, elapsedSeconds: number, labelOverride?: string): void;
	/**
	 * Build the speed ladder scaled to `ladderSeconds`, fill the <select>, resync the stepper, and hand the
	 * default speed to setSpeed(). One ladder for every replay (core/speed-ladder), so the −/+ label can no
	 * longer read a step the segment replay would never offer. Returns the default speed.
	 */
	buildLadder(ladderSeconds: number): number;
	/** Flip the play/pause icon (completion, the photo-lightbox playback hold, exit). */
	setPlayIcon(playing: boolean): void;
	/** Remove the transport's listeners and cancel any pending coalesced seek. */
	destroy(): void;
}

export function createReplayTransport(ids: ReplayTransportIds, hooks: ReplayTransportHooks): ReplayTransport {
	const playEl = document.getElementById(ids.playPauseId);
	const seekEl = document.getElementById(ids.seekId) as HTMLInputElement | null;
	const currentEl = document.getElementById(ids.currentTimeId);
	const selectEl = document.getElementById(ids.speedSelectId) as HTMLSelectElement | null;

	const stepper = setupSpeedStepper({
		selectEl,
		slowerBtn: document.getElementById(ids.speedSlowerId),
		fasterBtn: document.getElementById(ids.speedFasterId),
		labelEl: document.getElementById(ids.speedLabelId),
	});

	// ── play / pause ────────────────────────────────────────────────────────────────────────────────
	function setPlayIcon(playing: boolean): void {
		if (!playEl) return;
		// A node, not an innerHTML string: collapsing the icon write into one expression makes it an
		// unsafe-sink lint error, and there is nothing to escape here. Shows the action a click performs.
		const icon = document.createElement('i');
		icon.className = playing ? 'bi bi-pause-fill' : 'bi bi-play-fill';
		playEl.replaceChildren(icon);
		// Keep the accessible name in step with the icon — a static "Play / Pause" never tells a
		// screen-reader user which state the replay is actually in (WCAG 4.1.2).
		playEl.setAttribute('aria-label', playing ? 'Pause' : 'Play');
	}
	const onPlayClick = (): void => setPlayIcon(hooks.togglePlayPause());
	playEl?.addEventListener('click', onPlayClick);

	// ── speed ───────────────────────────────────────────────────────────────────────────────────────
	//
	// The remembered speed is an OFFSET from the ladder's default step, never the absolute multiplier.
	// The ladder is SCALED to the replay's length (speed-ladder.ts): a 2.5-hour race and a 2-minute
	// segment effort get ladders two orders of magnitude apart, so a stored "60x" is a step that simply
	// does not exist on the next replay — it would snap to whatever happened to be nearest, which for a
	// short effort is the fastest step available. An offset says "one notch faster than default", which
	// is what the viewer actually meant and is meaningful on every ladder.
	const speedKey = devicePrefKeys.replaySpeed(ids.speedPrefKey);
	let ladder: SpeedLadder | null = null;

	/** The ladder step the stored offset points at, clamped into the ladder this replay actually has. */
	function speedForStoredOffset(l: SpeedLadder): number {
		const defaultIndex = l.speeds.indexOf(l.defaultSpeed);
		if (defaultIndex < 0) return l.defaultSpeed;
		const offset = getNumber(speedKey, 0);
		const index = Math.max(0, Math.min(l.speeds.length - 1, defaultIndex + Math.round(offset)));
		return l.speeds[index] ?? l.defaultSpeed;
	}

	function storeOffsetFor(speed: number): void {
		if (!ladder) return;
		const defaultIndex = ladder.speeds.indexOf(ladder.defaultSpeed);
		const chosenIndex = ladder.speeds.indexOf(speed);
		if (defaultIndex < 0 || chosenIndex < 0) return;
		setNumber(speedKey, chosenIndex - defaultIndex);
	}

	// Fires for the <select> AND for the −/+ stepper, which dispatches a synthetic 'change' — so both
	// paths persist without the stepper knowing anything about preferences.
	const onSpeedChange = (): void => {
		if (!selectEl) return;
		const speed = parseFloat(selectEl.value);
		storeOffsetFor(speed);
		hooks.setSpeed(speed);
	};
	selectEl?.addEventListener('change', onSpeedChange);

	// ── scrubber: drag flag (a) + rAF coalescing (b) ─────────────────────────────────────────────────
	let dragging = false;
	let pendingPct: number | null = null;
	let frameId: number | null = null;
	let lastClock: string | null = null;   // last text written to the elapsed clock (write-skip guard)

	const onSeekDown = (): void => { dragging = true; };
	// Release ANYWHERE clears the drag: a pointerup off the thumb still ends the gesture.
	const clearDrag = (): void => { dragging = false; };
	const onSeekInput = (): void => {
		if (!seekEl) return;
		pendingPct = parseFloat(seekEl.value);
		if (frameId !== null) return;
		frameId = requestAnimationFrame(() => {
			frameId = null;
			if (pendingPct !== null) hooks.seekToPercent(pendingPct);
			pendingPct = null;
		});
	};
	seekEl?.addEventListener('pointerdown', onSeekDown);
	seekEl?.addEventListener('input', onSeekInput);
	window.addEventListener('pointerup', clearDrag);
	window.addEventListener('pointercancel', clearDrag);

	return {
		render(percent: number, elapsedSeconds: number, labelOverride?: string): void {
			// (a) the write-back guard: never fight the user's thumb.
			if (seekEl && !dragging) seekEl.value = String(percent);
			// (d) one formatter for every replay clock; labelOverride is the segment's % mode.
			// formatElapsed rounds to whole seconds, so this string is identical ~59/60 frames at 60 Hz —
			// skip the redundant text-node write (and its style recalc) unless it actually changed.
			if (currentEl) {
				const clock = labelOverride ?? formatElapsed(elapsedSeconds);
				if (clock !== lastClock) { currentEl.textContent = clock; lastClock = clock; }
			}
		},
		buildLadder(ladderSeconds: number): number {
			ladder = buildSpeedLadder(ladderSeconds);
			if (selectEl) populateSpeedSelect(selectEl, ladder);
			// populateSpeedSelect selects the ladder default; the stored offset moves off it before the
			// stepper refreshes, so the −/+ label and the disabled-end states describe the restored step.
			const speed = speedForStoredOffset(ladder);
			if (selectEl) selectEl.value = String(speed);
			stepper.refresh();
			hooks.setSpeed(speed);
			return speed;
		},
		setPlayIcon,
		destroy(): void {
			if (frameId !== null) { cancelAnimationFrame(frameId); frameId = null; }
			pendingPct = null;
			stepper.destroy(); // the −/+ button listeners setupSpeedStepper created (else they leak past destroy)
			playEl?.removeEventListener('click', onPlayClick);
			selectEl?.removeEventListener('change', onSpeedChange);
			seekEl?.removeEventListener('pointerdown', onSeekDown);
			seekEl?.removeEventListener('input', onSeekInput);
			window.removeEventListener('pointerup', clearDrag);
			window.removeEventListener('pointercancel', clearDrag);
		},
	};
}
