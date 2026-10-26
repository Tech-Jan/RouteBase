// @vitest-environment node
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { must } from '../core/invariant';
import { swapStyleWithTerrain, cancelPendingSwap } from './maplibre-style-swap';

// Pins the shared style-swap contract every terrain map now shares: terrain off BEFORE the swap, a
// clean { diff:false } rebuild, camera/layers re-added on style.load, 3D re-applied on the first idle,
// and a generation guard that makes a superseded swap's callbacks inert.

interface Call { op: string; arg?: unknown }

class FakeMap {
	calls: Call[] = [];
	styleLoadCb: (() => void) | null = null;
	idleCb: (() => void) | null = null;
	private _layers = new Set<string>();
	private _sources = new Set<string>();

	seedTerrain(): void { this._layers.add('hillshade'); this._sources.add('terrainSource'); }

	setTerrain(v: unknown): void { this.calls.push({ op: 'setTerrain', arg: v === null ? 'null' : 'set' }); }
	getLayer(id: string): { id: string } | undefined { return this._layers.has(id) ? { id } : undefined; }
	removeLayer(id: string): void { this.calls.push({ op: 'removeLayer', arg: id }); this._layers.delete(id); }
	getSource(id: string): object | undefined { return this._sources.has(id) ? {} : undefined; }
	removeSource(id: string): void { this.calls.push({ op: 'removeSource', arg: id }); this._sources.delete(id); }
	setStyle(style: unknown, opts?: unknown): void { this.calls.push({ op: 'setStyle', arg: opts }); }
	once(ev: string, cb: () => void): void {
		if (ev === 'style.load') this.styleLoadCb = cb;
		else if (ev === 'idle') this.idleCb = cb;
	}
	off(ev: string): void {
		if (ev === 'style.load') this.styleLoadCb = null;
		else if (ev === 'idle') this.idleCb = null;
	}
	idx(op: string): number { return this.calls.findIndex((c) => c.op === op); }
}

let map: FakeMap;
const asMap = (m: FakeMap): maplibregl.Map => m as unknown as maplibregl.Map;

beforeEach(() => { map = new FakeMap(); map.seedTerrain(); });

describe('swapStyleWithTerrain', () => {
	it('disables terrain, strips terrain sources, then swaps with { diff:false }', () => {
		swapStyleWithTerrain(asMap(map), 'https://s/style.json', { generationOk: () => true });

		const off = map.idx('setTerrain');
		const stripLayer = map.calls.findIndex((c) => c.op === 'removeLayer' && c.arg === 'hillshade');
		const stripSrc = map.calls.findIndex((c) => c.op === 'removeSource' && c.arg === 'terrainSource');
		const swap = map.idx('setStyle');

		expect(off).toBe(0);                       // terrain off first
		expect(stripLayer).toBeGreaterThan(off);   // then strip
		expect(stripSrc).toBeGreaterThan(off);
		expect(swap).toBeGreaterThan(stripSrc);    // then the swap
		expect(must(map.calls[swap], 'swap call index').arg).toEqual({ diff: false });
	});

	it('re-adds on style.load, then re-applies terrain on the first idle', () => {
		const onStyleLoad = vi.fn();
		const onIdle = vi.fn();
		swapStyleWithTerrain(asMap(map), 'https://s/style.json', { generationOk: () => true, onStyleLoad, onIdle });

		expect(onStyleLoad).not.toHaveBeenCalled();
		map.styleLoadCb!();
		expect(onStyleLoad).toHaveBeenCalledTimes(1);
		expect(onIdle).not.toHaveBeenCalled(); // idle registered but not fired yet

		map.idleCb!();
		expect(onIdle).toHaveBeenCalledTimes(1);
	});

	it('a superseded generation makes both callbacks inert', () => {
		const onStyleLoad = vi.fn();
		const onIdle = vi.fn();
		let live = true;
		swapStyleWithTerrain(asMap(map), 'https://s/style.json', { generationOk: () => live, onStyleLoad, onIdle });

		live = false; // a newer switch supersedes this one
		map.styleLoadCb!();
		expect(onStyleLoad).not.toHaveBeenCalled();
		expect(map.idleCb).toBeNull(); // no idle listener registered once superseded
	});

	it('cancelPendingSwap detaches a pending style.load listener', () => {
		swapStyleWithTerrain(asMap(map), 'https://s/style.json', { generationOk: () => true, onStyleLoad: vi.fn() });
		expect(map.styleLoadCb).not.toBeNull();
		cancelPendingSwap(asMap(map));
		expect(map.styleLoadCb).toBeNull();
	});

	it('a second swap supersedes the first pending listener', () => {
		const firstLoad = vi.fn();
		swapStyleWithTerrain(asMap(map), 'a', { generationOk: () => true, onStyleLoad: firstLoad });
		const firstCb = map.styleLoadCb;
		swapStyleWithTerrain(asMap(map), 'b', { generationOk: () => true, onStyleLoad: vi.fn() });
		// The first listener was detached; the stored one is the second swap's.
		expect(map.styleLoadCb).not.toBe(firstCb);
	});
});
