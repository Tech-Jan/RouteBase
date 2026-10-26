import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { CameraControls } from './camera-controls';

// LV-05 — the idle loop, which now does one thing: hand each frame's centre to the installed writer.
//
// The write itself (the gate, the change detection, the terrain re-clamp) belongs to the follow seam
// and is pinned in core/replay-follow-camera.test.ts. CameraControls used to carry a change-detected
// DEFAULT writer for "no follow seam installed"; that case became unreachable when activate() was
// made reachable only through engageFollow — which installs a writer first — so the default was
// deleted rather than left as a second, untriggerable code path.

class FakeMap {
	jumpTo = vi.fn<(cam: Record<string, unknown>) => void>();
	private _canvas = document.createElement('canvas');
	getCanvas(): HTMLCanvasElement { return this._canvas; }
}

let map: FakeMap;
let rafQueue: Map<number, FrameRequestCallback>;
let nextRafId: number;

function pumpFrames(n: number): void {
	for (let i = 0; i < n; i++) {
		const frames = [...rafQueue.values()];
		rafQueue.clear();
		frames.forEach((cb) => cb(0));
	}
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const asMap = (m: FakeMap): maplibregl.Map => m as any;

beforeEach(() => {
	map = new FakeMap();
	rafQueue = new Map();
	nextRafId = 1;
	// A real cancelAnimationFrame, so a cancelled frame does not still fire after a re-activate.
	vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => {
		const id = nextRafId++;
		rafQueue.set(id, cb);
		return id;
	});
	vi.stubGlobal('cancelAnimationFrame', (id: number) => { rafQueue.delete(id); });
});

afterEach(() => {
	vi.unstubAllGlobals();
});

describe('CameraControls idle loop', () => {
	it('writes nothing itself when no writer is installed', () => {
		const camera = new CameraControls(asMap(map));
		camera.activate();
		camera.startIdleLoop(() => [7, 8]);

		pumpFrames(3);

		// The loop runs, but the map is never touched: every write is the seam's to make.
		expect(map.jumpTo).not.toHaveBeenCalled();
	});

	it('feeds the installed writer one centre per frame', () => {
		const camera = new CameraControls(asMap(map));
		const writes: maplibregl.LngLatLike[] = [];
		camera.setFollowCenterWriter((center) => { writes.push(center); });
		camera.activate();
		camera.startIdleLoop(() => [7, 8]);

		pumpFrames(2);

		expect(map.jumpTo).not.toHaveBeenCalled();
		expect(writes).toHaveLength(3);
		expect(writes[0]).toEqual([7, 8]);
	});

	it('deactivate() drops the writer, so a stale one cannot outlive the follow it belonged to', () => {
		const camera = new CameraControls(asMap(map));
		const write = vi.fn();
		camera.setFollowCenterWriter(write);
		camera.activate();
		camera.startIdleLoop(() => [7, 8]);
		pumpFrames(1);
		expect(write).toHaveBeenCalledTimes(2);

		camera.deactivate();
		write.mockClear();
		camera.activate();
		camera.startIdleLoop(() => [7, 8]);
		pumpFrames(2);

		// The next engage installs a fresh writer (fresh change detector) — until then, nothing writes.
		expect(write).not.toHaveBeenCalled();
		expect(map.jumpTo).not.toHaveBeenCalled();
	});
});
