// @vitest-environment node
import { describe, it, expect, vi, beforeEach } from 'vitest';
import {
	registerPlaybackHold, acquirePlaybackHold, releasePlaybackHold, seekPlaybackHoldTo,
} from './playback-hold';

function fakePlayback(playing: boolean) {
	const state = { playing };
	return {
		state,
		target: {
			isPlaying: () => state.playing,
			pause: vi.fn(() => { state.playing = false; }),
			resume: vi.fn(() => { state.playing = true; }),
		},
	};
}

describe('playback-hold', () => {
	beforeEach(() => registerPlaybackHold(null)); // module state — reset the slot between cases

	it('does nothing when no playback is registered', () => {
		expect(() => { acquirePlaybackHold(); releasePlaybackHold(); }).not.toThrow();
	});

	it('pauses a running playback and resumes it on release', () => {
		const p = fakePlayback(true);
		registerPlaybackHold(p.target);

		acquirePlaybackHold();
		expect(p.target.pause).toHaveBeenCalledTimes(1);
		expect(p.state.playing).toBe(false);

		releasePlaybackHold();
		expect(p.target.resume).toHaveBeenCalledTimes(1);
		expect(p.state.playing).toBe(true);
	});

	// The whole reason the module tracks a `held` flag: auto-resume must not start something the
	// user had deliberately paused before opening the photo.
	it('leaves an already-paused playback paused', () => {
		const p = fakePlayback(false);
		registerPlaybackHold(p.target);

		acquirePlaybackHold();
		expect(p.target.pause).not.toHaveBeenCalled();

		releasePlaybackHold();
		expect(p.target.resume).not.toHaveBeenCalled();
		expect(p.state.playing).toBe(false);
	});

	it('is idempotent — a second acquire does not stack, and a second release does not re-resume', () => {
		const p = fakePlayback(true);
		registerPlaybackHold(p.target);

		acquirePlaybackHold();
		acquirePlaybackHold(); // e.g. an open→open lightbox replace
		expect(p.target.pause).toHaveBeenCalledTimes(1);

		releasePlaybackHold();
		releasePlaybackHold();
		expect(p.target.resume).toHaveBeenCalledTimes(1);
	});

	it('drops an outstanding hold when the slot is cleared on teardown', () => {
		const p = fakePlayback(true);
		registerPlaybackHold(p.target);
		acquirePlaybackHold();

		registerPlaybackHold(null); // replay exited while the photo was still open
		releasePlaybackHold();

		expect(p.target.resume).not.toHaveBeenCalled(); // the engine is gone — don't poke it
	});

	it('a newly registered playback is not held by a previous session', () => {
		const first = fakePlayback(true);
		registerPlaybackHold(first.target);
		acquirePlaybackHold();

		const second = fakePlayback(true);
		registerPlaybackHold(second.target);
		releasePlaybackHold();

		expect(second.target.resume).not.toHaveBeenCalled();
	});
});

// The camera half is deliberately NOT tied to play state: a paused replay still owns the camera through
// the follow hook, so the photo viewer's easeTo was overridden every frame and the map never panned to
// the photo. suspendCamera/restoreCamera therefore run either side of the isPlaying() gate.
describe('playback-hold — camera suspension', () => {
	beforeEach(() => registerPlaybackHold(null));

	function fakeWithCamera(playing: boolean) {
		const p = fakePlayback(playing);
		const calls: string[] = [];
		return {
			...p,
			calls,
			target: {
				...p.target,
				pause: vi.fn(() => { p.state.playing = false; calls.push('pause'); }),
				resume: vi.fn(() => { p.state.playing = true; calls.push('resume'); }),
				suspendCamera: vi.fn(() => calls.push('suspendCamera')),
				restoreCamera: vi.fn(() => calls.push('restoreCamera')),
			},
		};
	}

	it('suspends the camera even when the playback is ALREADY paused', () => {
		const p = fakeWithCamera(false);
		registerPlaybackHold(p.target);

		acquirePlaybackHold();

		expect(p.target.suspendCamera).toHaveBeenCalledTimes(1);
		expect(p.target.pause).not.toHaveBeenCalled(); // still must not un-pause the user's pause
	});

	it('restores the camera on release without resuming a playback it never paused', () => {
		const p = fakeWithCamera(false);
		registerPlaybackHold(p.target);

		acquirePlaybackHold();
		releasePlaybackHold();

		expect(p.target.restoreCamera).toHaveBeenCalledTimes(1);
		expect(p.target.resume).not.toHaveBeenCalled();
		expect(p.state.playing).toBe(false);
	});

	it('orders the camera outside the transport: suspend→pause, restore→resume', () => {
		const p = fakeWithCamera(true);
		registerPlaybackHold(p.target);

		acquirePlaybackHold();
		releasePlaybackHold();

		// Restore before resume so the first resumed frame already follows again.
		expect(p.calls).toEqual(['suspendCamera', 'pause', 'restoreCamera', 'resume']);
	});

	it('is idempotent — an open→open viewer replace does not re-suspend', () => {
		const p = fakeWithCamera(true);
		registerPlaybackHold(p.target);

		acquirePlaybackHold();
		acquirePlaybackHold();
		expect(p.target.suspendCamera).toHaveBeenCalledTimes(1);

		releasePlaybackHold();
		releasePlaybackHold();
		expect(p.target.restoreCamera).toHaveBeenCalledTimes(1);
	});

	it('never restores a camera it did not suspend', () => {
		const p = fakeWithCamera(true);
		registerPlaybackHold(p.target);

		releasePlaybackHold();

		expect(p.target.restoreCamera).not.toHaveBeenCalled();
	});

	it('drops an outstanding camera suspension when the slot is cleared', () => {
		const first = fakeWithCamera(true);
		registerPlaybackHold(first.target);
		acquirePlaybackHold();

		registerPlaybackHold(null); // replay torn down while the photo was open
		releasePlaybackHold();

		expect(first.target.restoreCamera).not.toHaveBeenCalled();
	});

	it('still works for a target that offers no camera hooks (they are optional)', () => {
		const p = fakePlayback(true); // no suspendCamera/restoreCamera at all
		registerPlaybackHold(p.target);

		expect(() => { acquirePlaybackHold(); releasePlaybackHold(); }).not.toThrow();
		expect(p.target.pause).toHaveBeenCalledTimes(1);
		expect(p.target.resume).toHaveBeenCalledTimes(1);
	});
});

// The photo viewer pages through a ride's photos and pans the map to each one. Without carrying the
// playhead along, closing it dropped the user back where the rider stood when the viewer opened —
// undoing the walk they just made through the photos.
describe('playback-hold — playhead seek', () => {
	beforeEach(() => registerPlaybackHold(null));

	function fakeWithSeek(playing: boolean) {
		const p = fakePlayback(playing);
		const seekToPosition = vi.fn<(ll: [number, number]) => void>();
		return { ...p, target: { ...p.target, seekToPosition } };
	}

	it('forwards the shown position to the registered playback', () => {
		const p = fakeWithSeek(true);
		registerPlaybackHold(p.target);
		acquirePlaybackHold();

		seekPlaybackHoldTo([7.1, 46.9]);

		expect(p.target.seekToPosition).toHaveBeenCalledWith([7.1, 46.9]);
	});

	// Not gated on `held`: a replay the user paused themselves is still what stays on screen at close.
	it('seeks a playback that was already paused', () => {
		const p = fakeWithSeek(false);
		registerPlaybackHold(p.target);
		acquirePlaybackHold();

		seekPlaybackHoldTo([7.1, 46.9]);

		expect(p.target.seekToPosition).toHaveBeenCalledTimes(1);
		expect(p.target.resume).not.toHaveBeenCalled();
	});

	it('is a no-op with nothing registered, or a playback that cannot seek', () => {
		expect(() => seekPlaybackHoldTo([7.1, 46.9])).not.toThrow();

		registerPlaybackHold(fakePlayback(true).target); // no seekToPosition at all
		expect(() => seekPlaybackHoldTo([7.1, 46.9])).not.toThrow();
	});

	it('does not reach a playback whose slot was cleared on teardown', () => {
		const p = fakeWithSeek(true);
		registerPlaybackHold(p.target);
		registerPlaybackHold(null); // replay exited while the photo was still open

		seekPlaybackHoldTo([7.1, 46.9]);

		expect(p.target.seekToPosition).not.toHaveBeenCalled();
	});
});
