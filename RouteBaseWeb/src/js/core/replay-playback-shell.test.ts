import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { createReplayPlaybackShell, type ReplayShellHooks } from './replay-playback-shell';
import type { CameraControlsInstance } from './core-types';

// P-NEW-4..10 (CR1-01): pins for the shared playback-transport shell — frame clock,
// pause→idle-loop (RP-02/RS2-02), resume atEnd-restart (RP-04/RS2-04), speed clamp,
// completion idle policy (CR1-07 flag), camera change-detector wiring.
// Manual rAF queue + frozen performance.now: frames are stepped explicitly.

let rafQueue: Map<number, FrameRequestCallback>;
let rafId: number;
let now: number;

/** Advance the clock and run every queued rAF callback (one frame). */
function stepFrame(dtMs: number): void {
	now += dtMs;
	const cbs = [...rafQueue.values()];
	rafQueue.clear();
	cbs.forEach((cb) => cb(now));
}

beforeEach(() => {
	rafQueue = new Map();
	rafId = 0;
	now = 1000;
	vi.stubGlobal('requestAnimationFrame', vi.fn((cb: FrameRequestCallback) => { rafQueue.set(++rafId, cb); return rafId; }));
	vi.stubGlobal('cancelAnimationFrame', vi.fn((id: number) => { rafQueue.delete(id); }));
	vi.spyOn(performance, 'now').mockImplementation(() => now);
});

afterEach(() => {
	vi.unstubAllGlobals();
	vi.restoreAllMocks();
});

function makeFakeMap(): { jumpTo: ReturnType<typeof vi.fn>; getPitch: () => number } {
	return { jumpTo: vi.fn(), getPitch: () => 0 };
}
const asMap = (m: unknown): maplibregl.Map => m as maplibregl.Map;

function makeHarness(overrides: Partial<ReplayShellHooks> = {}) {
	const controls = {
		startIdleLoop: vi.fn(),
		stopIdleLoop: vi.fn(),
	};
	const tick = vi.fn((_dtMs: number, _speed: number) => false);
	const isAtEnd = vi.fn(() => false);
	const restart = vi.fn();
	const onComplete = vi.fn();
	const hooks: ReplayShellHooks = {
		tick,
		isAtEnd,
		restart,
		wantsIdleLoop: vi.fn(() => true),
		idleCenter: vi.fn((): maplibregl.LngLatLike => [9, 45]),
		getCameraControls: vi.fn((): CameraControlsInstance | null => controls as unknown as CameraControlsInstance),
		onComplete,
		idleOnComplete: false,
		...overrides,
	};
	const shell = createReplayPlaybackShell(hooks);
	return { shell, controls, tick, isAtEnd, restart, onComplete };
}

// Centre only: zoom/bearing/pitch belong to MapLibre once a follow is engaged, so the shell no
// longer carries them (see core/replay-follow-camera.ts).
const CAM = { center: [9, 45] as [number, number] };

describe('replay-playback-shell — P-NEW-4..10', () => {
	it('beginPlayback schedules frames and passes dt + speed to tick', () => {
		const { shell, tick } = makeHarness();
		shell.beginPlayback();
		// First frame runs synchronously with dt 0 (lastFrameTime was just set to now).
		expect(tick).toHaveBeenCalledTimes(1);
		expect(tick).toHaveBeenLastCalledWith(0, 1);

		shell.setSpeed(4);
		stepFrame(16);
		expect(tick).toHaveBeenCalledTimes(2);
		expect(tick).toHaveBeenLastCalledWith(16, 4);
		shell.pause();
	});

	it('pause cancels the pending frame and starts the idle loop only when wantsIdleLoop()', () => {
		const { shell, controls, tick } = makeHarness();
		shell.beginPlayback();
		shell.pause();
		expect(shell.isPlaying()).toBe(false);
		expect(controls.startIdleLoop).toHaveBeenCalledTimes(1);
		stepFrame(16);
		expect(tick).toHaveBeenCalledTimes(1); // pending frame cancelled — no tick after pause

		const gated = makeHarness({ wantsIdleLoop: vi.fn(() => false) });
		gated.shell.beginPlayback();
		gated.shell.pause();
		expect(gated.controls.startIdleLoop).not.toHaveBeenCalled();
	});

	it('resume stops the idle loop', () => {
		const { shell, controls } = makeHarness();
		shell.beginPlayback();
		shell.pause();
		shell.resume();
		expect(controls.stopIdleLoop).toHaveBeenCalledTimes(1);
		expect(shell.isPlaying()).toBe(true);
		shell.pause();
	});

	it('resume at end calls restart, resets camera tracking, then plays', () => {
		const map = makeFakeMap();
		const { shell, isAtEnd, restart } = makeHarness();

		shell.applyCamera(asMap(map), CAM);
		shell.applyCamera(asMap(map), CAM);
		expect(map.jumpTo).toHaveBeenCalledTimes(1); // detector primed on the same camera

		isAtEnd.mockReturnValue(true);
		shell.resume();
		expect(restart).toHaveBeenCalledTimes(1);
		expect(shell.isPlaying()).toBe(true);

		// Camera tracking was reset — the unchanged camera fires jumpTo again.
		shell.applyCamera(asMap(map), CAM);
		expect(map.jumpTo).toHaveBeenCalledTimes(2);
		shell.pause();
	});

	it('resume while playing is a no-op (guard)', () => {
		const { shell, controls, tick } = makeHarness();
		shell.beginPlayback();
		expect(tick).toHaveBeenCalledTimes(1);

		shell.resume();
		expect(tick).toHaveBeenCalledTimes(1); // no second synchronous frame
		expect(controls.stopIdleLoop).not.toHaveBeenCalled();

		stepFrame(16);
		expect(tick).toHaveBeenCalledTimes(2); // exactly one rAF chain — no double loop
		shell.pause();
	});

	it('completion stops playback, fires onComplete once, idles the camera only when idleOnComplete', () => {
		// idleOnComplete: false (B's pinned behavior — CR1-07)
		const b = makeHarness();
		b.tick.mockReturnValue(true);
		b.shell.beginPlayback();
		expect(b.shell.isPlaying()).toBe(false);
		expect(b.onComplete).toHaveBeenCalledTimes(1);
		expect(b.controls.startIdleLoop).not.toHaveBeenCalled();
		stepFrame(16);
		expect(b.tick).toHaveBeenCalledTimes(1); // loop stopped — no further frames

		// idleOnComplete: true (A's pinned "look around at the finish")
		const a = makeHarness({ idleOnComplete: true });
		a.tick.mockReturnValue(true);
		a.shell.beginPlayback();
		expect(a.shell.isPlaying()).toBe(false);
		expect(a.onComplete).toHaveBeenCalledTimes(1);
		expect(a.controls.startIdleLoop).toHaveBeenCalledTimes(1);
	});

	it('destroy stops the frame and the idle loop WITHOUT starting one (the pause() teardown trap)', () => {
		// pause() re-arms the paused-camera idle loop; using it as teardown then nulls the map under a
		// live rAF (the RS2-class leak). destroy() must never call startIdleLoop, even with wantsIdleLoop.
		const { shell, controls, tick } = makeHarness(); // wantsIdleLoop defaults to () => true
		shell.beginPlayback();
		expect(tick).toHaveBeenCalledTimes(1);

		shell.destroy();

		expect(shell.isPlaying()).toBe(false);
		expect(controls.startIdleLoop).not.toHaveBeenCalled(); // the trap: pause() WOULD have started it here
		expect(controls.stopIdleLoop).toHaveBeenCalledTimes(1);
		stepFrame(16);
		expect(tick).toHaveBeenCalledTimes(1); // pending frame cancelled — the loop is dead
	});

	it('setSpeed clamps to [0.1, 1000]', () => {
		const { shell } = makeHarness();
		shell.setSpeed(0.001);
		expect(shell.getSpeed()).toBe(0.1);
		shell.setSpeed(5000);
		expect(shell.getSpeed()).toBe(1000);
		shell.setSpeed(4);
		expect(shell.getSpeed()).toBe(4);
	});

	it('applyCamera skips jumpTo for an unchanged camera (detector wired)', () => {
		const map = makeFakeMap();
		const { shell } = makeHarness();

		shell.applyCamera(asMap(map), CAM);
		shell.applyCamera(asMap(map), CAM);
		expect(map.jumpTo).toHaveBeenCalledTimes(1);

		shell.applyCamera(asMap(map), { center: [9.001, 45] });
		expect(map.jumpTo).toHaveBeenCalledTimes(2);
	});
});
