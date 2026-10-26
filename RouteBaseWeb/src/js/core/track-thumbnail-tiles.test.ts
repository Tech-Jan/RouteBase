// @vitest-environment node
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { TileLayout, TileRef } from './track-thumbnail-geometry';
import { must } from './invariant';
import {
	getTileSource,
	loadLayoutTiles,
	paintTiles,
	_resetTileCache,
	_resetTileSource,
	type LoadedTile,
	type TilePaintCtx,
	type TileSource,
} from './track-thumbnail-tiles';

const STYLE_JSON = {
	version: 8,
	sources: {
		osm: {
			type: 'raster',
			tiles: [
				'https://a.tile.openstreetmap.org/{z}/{x}/{y}.png',
				'https://b.tile.openstreetmap.org/{z}/{x}/{y}.png',
				'https://c.tile.openstreetmap.org/{z}/{x}/{y}.png',
			],
			tileSize: 256,
			maxzoom: 19,
		},
	},
};

function fakeSource(): TileSource {
	return {
		urlTemplates: ['https://s/{z}/{x}/{y}.png'],
		tileSize: 256,
		minZoom: 0,
		maxZoom: 19,
	};
}

function layoutOf(tiles: TileRef[]): TileLayout {
	return { z: tiles[0]?.z ?? 12, tiles, project: () => ({ x: 0, y: 0 }) };
}

function ref(z: number, x: number, y: number): TileRef {
	return { z, x, y, dx: 0, dy: 0, size: 256 };
}

describe('track-thumbnail-tiles — getTileSource', () => {
	beforeEach(() => _resetTileSource());
	afterEach(() => vi.unstubAllGlobals());

	it('parses the raster tile template and zoom range from the style JSON', async () => {
		vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => STYLE_JSON })));
		const src = await getTileSource();
		expect(src).not.toBeNull();
		expect(src!.urlTemplates).toHaveLength(3);
		expect(src!.maxZoom).toBe(19);
		expect(src!.tileSize).toBe(256);
		expect(src!.minZoom).toBe(0);
	});

	it('resolves null when the style fetch fails (offline fallback)', async () => {
		vi.stubGlobal('fetch', vi.fn(async () => { throw new Error('offline'); }));
		expect(await getTileSource()).toBeNull();
	});

	it('retries the style fetch after a failure — no negative caching (DT-06)', async () => {
		let call = 0;
		vi.stubGlobal('fetch', vi.fn(async () => {
			if (++call === 1) throw new Error('offline');
			return { ok: true, json: async () => STYLE_JSON };
		}));
		expect(await getTileSource()).toBeNull();   // first attempt fails → memo cleared
		const src = await getTileSource();           // next card retries
		expect(src).not.toBeNull();
		expect(call).toBe(2);
	});
});

describe('track-thumbnail-tiles — loadLayoutTiles', () => {
	beforeEach(() => _resetTileCache());

	it('loads each distinct tile once and reuses the page cache across calls', async () => {
		const loader = vi.fn(async (url: string) => ({ url } as unknown as CanvasImageSource));
		const layout = layoutOf([ref(12, 1, 1), ref(12, 2, 1)]);
		const src = fakeSource();

		const first = await loadLayoutTiles(layout, src, loader);
		expect(first).toHaveLength(2);
		expect(loader).toHaveBeenCalledTimes(2);

		// Same tiles again → served from cache, loader not re-invoked.
		const again = await loadLayoutTiles(layout, src, loader);
		expect(again).toHaveLength(2);
		expect(loader).toHaveBeenCalledTimes(2);
	});

	it('drops tiles whose image fails to load, keeping the rest', async () => {
		const loader = vi.fn(async (url: string) =>
			url.includes('/9/') ? null : ({ url } as unknown as CanvasImageSource),
		);
		const layout = layoutOf([ref(5, 9, 1), ref(5, 3, 1)]);
		const loaded = await loadLayoutTiles(layout, fakeSource(), loader);
		expect(loaded).toHaveLength(1);
		expect(must(loaded[0], 'surviving tile').ref.x).toBe(3);
	});

	it('does not negatively cache a failed tile — retries on the next call (DT-06)', async () => {
		let call = 0;
		const loader = vi.fn(async () => (++call === 1 ? null : ({ ok: true } as unknown as CanvasImageSource)));
		const layout = layoutOf([ref(12, 5, 5)]);
		const src = fakeSource();

		expect(await loadLayoutTiles(layout, src, loader)).toHaveLength(0); // failed
		expect(await loadLayoutTiles(layout, src, loader)).toHaveLength(1); // retried, succeeded
		expect(loader).toHaveBeenCalledTimes(2);
	});
});

describe('track-thumbnail-tiles — paintTiles', () => {
	it('draws every tile then a translucent scrim across the whole canvas', () => {
		const events: string[] = [];
		let fillStyleSeen = '';
		const ctx: TilePaintCtx = {
			drawImage: (_img, dx, dy) => events.push('draw:' + dx + ',' + dy),
			set fillStyle(v: string) {
				fillStyleSeen = v;
			},
			get fillStyle() {
				return fillStyleSeen;
			},
			fillRect: (x, y, w, h) => events.push('scrim:' + x + ',' + y + ',' + w + ',' + h),
		};
		const loaded: LoadedTile[] = [
			{ ref: { z: 12, x: 1, y: 1, dx: -10, dy: -20, size: 256 }, img: {} as CanvasImageSource },
			{ ref: { z: 12, x: 2, y: 1, dx: 246, dy: -20, size: 256 }, img: {} as CanvasImageSource },
		];
		paintTiles(ctx, loaded, 240, 200);

		// Tiles first, scrim last (so the track drawn after it reads on top).
		expect(events).toEqual(['draw:-10,-20', 'draw:246,-20', 'scrim:0,0,240,200']);
		expect(fillStyleSeen).toContain('rgba(255, 255, 255');
	});
});
