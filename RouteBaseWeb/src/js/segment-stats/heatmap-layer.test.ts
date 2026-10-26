// @vitest-environment node
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { heatmapDisplay } from './heatmap-layer';

// SS-01 characterization pins for the surviving MapLibre heatmap overlay. The
// legacy attach/detach arms are deleted in SS-06; these lock the MapLibre raster
// source (bounds + cache-busted tile URL) and the detach→re-attach style-switch path.
// This module is ctx-free, so a plain recording fake map + a fetch stub suffice.

const GENERATED_AT = '2026-07-05T00:00:00Z';
const BOUNDS = { south: 1, west: 10, north: 3, east: 14 };

interface RasterSpec { tiles: string[]; bounds?: [number, number, number, number]; attribution?: string; }

class FakeMap {
	calls: Array<[string, string]> = [];   // add/removeSource + add/removeLayer, in order
	fitBoundsArgs: unknown[] = [];
	sourceSpecs = new Map<string, RasterSpec>();
	private _layers = new Set<string>();
	private _sources = new Set<string>();

	addSource(id: string, spec: RasterSpec): void { this.calls.push(['addSource', id]); this._sources.add(id); this.sourceSpecs.set(id, spec); }
	addLayer(spec: { id: string }): void { this.calls.push(['addLayer', spec.id]); this._layers.add(spec.id); }
	removeLayer(id: string): void { this.calls.push(['removeLayer', id]); this._layers.delete(id); }
	removeSource(id: string): void { this.calls.push(['removeSource', id]); this._sources.delete(id); }
	getLayer(id: string): { id: string } | undefined { return this._layers.has(id) ? { id } : undefined; }
	getSource(id: string): { id: string } | undefined { return this._sources.has(id) ? { id } : undefined; }
	fitBounds(...args: unknown[]): void { this.fitBoundsArgs.push(args); }
}

let fakeMap: FakeMap;

async function loadDoneMeta(): Promise<void> {
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	(globalThis as any).fetch = vi.fn().mockResolvedValue({
		ok: true,
		json: async () => ({
			segmentId: 42, status: 'done', layer: 'all', minZoom: 8, maxZoom: 14,
			countMax: 100, bounds: BOUNDS, generatedAt: GENERATED_AT,
		}),
	});
	await heatmapDisplay.load(42);
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	delete (globalThis as any).fetch;
}

beforeEach(() => {
	fakeMap = new FakeMap();
});

afterEach(() => {
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	delete (globalThis as any).fetch;
});

describe('heatmap-layer MapLibre overlay — SS-01 pins', () => {
	it('attachMapLibre adds a bounded raster source with a cache-busted tile url', async () => {
		await loadDoneMeta();
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		heatmapDisplay.attachMapLibre(fakeMap as any, true);

		const spec = fakeMap.sourceSpecs.get('heatmap-tiles')!;
		expect(spec.tiles[0]).toContain('?v=' + encodeURIComponent(GENERATED_AT));
		expect(spec.bounds).toEqual([BOUNDS.west, BOUNDS.south, BOUNDS.east, BOUNDS.north]);
		expect(spec.attribution).toContain('running-heatmap');
		expect(fakeMap.getLayer('heatmap-tiles')).toBeDefined();
		expect(fakeMap.fitBoundsArgs).toHaveLength(1);
	});

	it('detachMapLibre removes layer then source; a re-attach re-adds both', async () => {
		await loadDoneMeta();
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		const map = fakeMap as any;
		heatmapDisplay.attachMapLibre(map, false);

		fakeMap.calls.length = 0;
		heatmapDisplay.detachMapLibre(map);
		expect(fakeMap.calls).toEqual([['removeLayer', 'heatmap-tiles'], ['removeSource', 'heatmap-tiles']]);
		expect(fakeMap.getLayer('heatmap-tiles')).toBeUndefined();
		expect(fakeMap.getSource('heatmap-tiles')).toBeUndefined();

		heatmapDisplay.attachMapLibre(map, false); // style-switch re-attach
		expect(fakeMap.getLayer('heatmap-tiles')).toBeDefined();
		expect(fakeMap.getSource('heatmap-tiles')).toBeDefined();
	});
});
