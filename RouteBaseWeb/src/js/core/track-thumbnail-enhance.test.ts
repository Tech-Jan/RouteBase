// @vitest-environment node
import { vi, describe, it, expect, beforeEach } from 'vitest';
import type { GeoBounds, TileLayout, TileRef } from './track-thumbnail-geometry';

// DT-05: enhanceWithTiles must only upgrade the fallback on FULL tile coverage — a partial
// set would clearRect the complete equirectangular fallback and paint holes.
vi.mock('./track-thumbnail-tiles', () => ({
	getTileSource: vi.fn(),
	loadLayoutTiles: vi.fn(),
	paintTiles: vi.fn(),
}));
vi.mock('./track-thumbnail-geometry', async (importOriginal) => {
	const actual = await importOriginal<typeof import('./track-thumbnail-geometry')>();
	return { ...actual, computeTileLayout: vi.fn() };
});

import { enhanceWithTiles } from './track-thumbnail';
import { getTileSource, loadLayoutTiles, paintTiles } from './track-thumbnail-tiles';
import { computeTileLayout } from './track-thumbnail-geometry';

function tile(x: number): TileRef {
	return { z: 12, x, y: 1, dx: 0, dy: 0, size: 256 };
}
function fakeLayout(n: number): TileLayout {
	return { z: 12, tiles: Array.from({ length: n }, (_, i) => tile(i)), project: () => ({ x: 0, y: 0 }) };
}
function recorder(): { ctx: unknown; calls: string[] } {
	const calls: string[] = [];
	const ctx = {
		setTransform: () => calls.push('setTransform'),
		clearRect: () => calls.push('clearRect'),
		drawImage: () => calls.push('drawImage'),
		beginPath() {}, moveTo() {}, lineTo() {}, arc() {}, rect() {}, stroke() {}, fill() {}, setLineDash() {},
		strokeStyle: '', fillStyle: '', lineWidth: 0, lineJoin: '', lineCap: '', globalAlpha: 1,
	};
	return { ctx, calls };
}

const source = { urlTemplates: ['x'], tileSize: 256, minZoom: 0, maxZoom: 19 };
const bounds = { minLat: 47, maxLat: 48, minLng: 7, maxLng: 8 } as unknown as GeoBounds;
const opts = { gpsData: [{ lat: 47, lng: 7 }, { lat: 48, lng: 8 }] };

describe('enhanceWithTiles full-coverage guard (DT-05)', () => {
	beforeEach(() => vi.clearAllMocks());

	it('keeps the fallback (no clearRect / paintTiles) when any layout tile fails', async () => {
		vi.mocked(getTileSource).mockResolvedValue(source);
		vi.mocked(computeTileLayout).mockReturnValue(fakeLayout(3));
		vi.mocked(loadLayoutTiles).mockResolvedValue([{ ref: tile(0), img: {} as CanvasImageSource }]); // 1 of 3

		const { ctx, calls } = recorder();
		await enhanceWithTiles(ctx as never, 240, 200, 2, opts, bounds);

		expect(calls).not.toContain('clearRect');
		expect(paintTiles).not.toHaveBeenCalled();
	});

	it('upgrades (clearRect + paintTiles) when all tiles load', async () => {
		vi.mocked(getTileSource).mockResolvedValue(source);
		vi.mocked(computeTileLayout).mockReturnValue(fakeLayout(2));
		vi.mocked(loadLayoutTiles).mockResolvedValue([
			{ ref: tile(0), img: {} as CanvasImageSource },
			{ ref: tile(1), img: {} as CanvasImageSource },
		]); // 2 of 2

		const { ctx, calls } = recorder();
		await enhanceWithTiles(ctx as never, 240, 200, 2, opts, bounds);

		expect(calls).toContain('clearRect');
		expect(paintTiles).toHaveBeenCalledTimes(1);
	});
});
