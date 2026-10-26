// playback-hold.ts — lets a modal pause whatever playback owns the page, without knowing what it is.
//
// The photo lightbox opens over a running replay. It must pause playback (the map flying on behind a
// full-screen photo is disorienting, and the frame cost keeps being paid for nothing) and resume on
// close. Importing the replay from the photo slice would invert the dependency — photos are a feature
// slice, replay is another — so the replay registers itself here and photos only acquire/release.
//
// Single-slot on purpose, mirroring core/map-dispatcher.ts: exactly one playback owns a page.

export interface PlaybackHoldTarget {
	pause(): void;
	resume(): void;
	isPlaying(): boolean;
	/**
	 * Optional: release any camera lock the playback holds, for as long as the modal is up.
	 *
	 * Separate from pause() because it must run even when the playback is ALREADY paused — a paused
	 * replay still owns the camera (the follow hook forces every update's centre back to the rider),
	 * so a modal that wants to move the map has to be able to say so independently of play state.
	 */
	suspendCamera?(): void;
	/** Optional: re-take the camera lock released by {@link PlaybackHoldTarget.suspendCamera}. */
	restoreCamera?(): void;
	/**
	 * Optional: move the playhead to a map position the modal is showing.
	 *
	 * Paging the photo viewer pans the map to each photo; without this the playback still sits where it
	 * was when the viewer opened, so closing snapped straight back there and the walk through the
	 * photos was undone. Resuming from the photo the user stopped on is the point of the whole gesture.
	 */
	seekToPosition?(lngLat: [number, number]): void;
}

let target: PlaybackHoldTarget | null = null;
let held = false;
let cameraSuspended = false;

/** Claim the slot for a live playback, or pass null on teardown. Clears any outstanding hold. */
export function registerPlaybackHold(next: PlaybackHoldTarget | null): void {
	target = next;
	held = false;
	cameraSuspended = false;
}

/**
 * Pause the registered playback if it is running.
 *
 * Records whether WE were the one that paused it — that flag is what makes {@link releasePlaybackHold}
 * correct: a replay the user had already paused before opening a photo must stay paused on close.
 */
export function acquirePlaybackHold(): void {
	if (!target) return;
	// Camera first, and unconditionally: an already-paused replay still owns the camera, so this must
	// not sit behind the isPlaying() gate that guards the pause itself.
	if (!cameraSuspended && target.suspendCamera) {
		cameraSuspended = true;
		target.suspendCamera();
	}
	if (held || !target.isPlaying()) return;
	held = true;
	target.pause();
}

/**
 * Move the registered playback's playhead to `lngLat` — the position the modal is showing.
 *
 * Deliberately NOT gated on `held`: a replay the user had already paused is still the thing the
 * eventual close leaves on screen, so it has to follow the photos too.
 */
export function seekPlaybackHoldTo(lngLat: [number, number]): void {
	target?.seekToPosition?.(lngLat);
}

/** Resume only if this module is what paused it. */
export function releasePlaybackHold(): void {
	if (!target) return;
	// Restore the camera before resuming so the first resumed frame already follows again.
	if (cameraSuspended && target.restoreCamera) {
		cameraSuspended = false;
		target.restoreCamera();
	}
	if (!held) return;
	held = false;
	target.resume();
}
