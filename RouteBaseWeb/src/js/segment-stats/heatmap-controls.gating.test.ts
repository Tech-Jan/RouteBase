import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

/**
 * Pins that a route offering no heatmap starts NO polling.
 *
 * heatmap-layer.ts polls every 2s up to 300 times, so a share view that accidentally kept the heatmap
 * wiring would give every anonymous visitor a 10-minute background poll loop per tab. Two independent
 * things prevent it — the server omitting hasHeatmap/heatmapStatus, and the showHeatmap surface — and
 * this pins the second, which is the one that survives someone re-adding the config keys.
 */

const configRef: { current: Record<string, unknown> } = { current: {} };

vi.mock('./segment-statistics', () => ({
	get ctx() {
		return { config: configRef.current };
	},
}));

describe('createHeatmapControls — showHeatmap surface', () => {
	let fetchSpy: ReturnType<typeof vi.fn>;

	beforeEach(() => {
		fetchSpy = vi.fn(() => Promise.reject(new Error('no fetch expected')));
		vi.stubGlobal('fetch', fetchSpy);
		vi.useFakeTimers();
	});

	afterEach(() => {
		vi.useRealTimers();
		vi.unstubAllGlobals();
		vi.resetModules();
	});

	it('makes no request and sets no timer when the surface is off', async () => {
		// The share shape: surface off, and the server omitted hasHeatmap/heatmapStatus entirely.
		configRef.current = { segmentId: 7, showHeatmap: false };
		const { createHeatmapControls } = await import('./heatmap-controls');

		const onStatusChange = vi.fn();
		const controls = createHeatmapControls({ onStatusChange, redraw: () => {} } as never);
		await vi.runAllTimersAsync();

		expect(fetchSpy).not.toHaveBeenCalled();
		expect(vi.getTimerCount()).toBe(0);
		expect(controls.status()).toBe('none');
		expect(controls.tilesReady()).toBe(false);
		expect(onStatusChange).toHaveBeenCalledWith('none');
	});

	it('still makes no request when the surface is off but heatmap config leaked in', async () => {
		// The regression this exists for: someone serializes heatmapStatus onto a share island. The
		// data-level suppression would be defeated; the surface gate must still hold.
		configRef.current = { segmentId: 7, showHeatmap: false, hasHeatmap: true, heatmapStatus: 'running' };
		const { createHeatmapControls } = await import('./heatmap-controls');

		createHeatmapControls({ onStatusChange: vi.fn(), redraw: () => {} } as never);
		await vi.runAllTimersAsync();

		expect(fetchSpy).not.toHaveBeenCalled();
		expect(vi.getTimerCount()).toBe(0);
	});

	it('DOES poll when the surface is on and a render exists (anti-vacuity)', async () => {
		// Without this, the two tests above would pass even if createHeatmapControls became a no-op for
		// everyone — i.e. if the heatmap silently died on the signed-in page too.
		configRef.current = { segmentId: 7, showHeatmap: true, hasHeatmap: true, heatmapStatus: 'done' };
		const { createHeatmapControls } = await import('./heatmap-controls');

		createHeatmapControls({ onStatusChange: vi.fn(), redraw: () => {} } as never);
		await vi.runAllTimersAsync();

		expect(fetchSpy).toHaveBeenCalled();
	});
});
