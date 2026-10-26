// @vitest-environment node
import { describe, it, expect, vi } from 'vitest';
import { must } from '../core/invariant';
import { createPhotoIconRegistry, photoIconName, coverFitCrop } from './photo-icon-registry';
import type { PhotoMarker } from './photo-map-types';

// The canvas half (drawPhotoIcon / makePinImage) needs a real 2D context and createImageBitmap,
// neither of which jsdom has — so rendering is injected and what gets tested here is the part that
// actually carries risk: dedup, the LRU bound, style-swap invalidation and failure handling.

function marker(photoId: number): PhotoMarker {
	return { photoId, trackId: 1, lat: 47, lng: 7, locationSource: 'exif', thumbHashBase64: null, v: 'v1' };
}

function fakeMap() {
	const images = new Set<string>();
	const calls = { add: [] as string[], remove: [] as string[] };
	const map = {
		hasImage: (id: string) => images.has(id),
		addImage: (id: string) => { images.add(id); calls.add.push(id); },
		removeImage: (id: string) => { images.delete(id); calls.remove.push(id); },
	} as unknown as maplibregl.Map;
	return { map, images, calls };
}

const IMAGE = {} as ImageData;
const flush = () => new Promise((r) => setTimeout(r, 0));

describe('photoIconName', () => {
	it('is stable and unique per photo', () => {
		expect(photoIconName(7)).toBe(photoIconName(7));
		expect(photoIconName(7)).not.toBe(photoIconName(8));
	});
});

describe('coverFitCrop', () => {
	it('takes the largest centred square of a landscape source', () => {
		expect(coverFitCrop(200, 100)).toEqual({ sx: 50, sy: 0, size: 100 });
	});

	it('takes the largest centred square of a portrait source', () => {
		expect(coverFitCrop(100, 250)).toEqual({ sx: 0, sy: 75, size: 100 });
	});

	it('is a no-op crop for an already-square source', () => {
		expect(coverFitCrop(120, 120)).toEqual({ sx: 0, sy: 0, size: 120 });
	});
});

describe('createPhotoIconRegistry', () => {
	it('registers each photo once, however often it is asked for', async () => {
		const h = fakeMap();
		const render = vi.fn(async () => IMAGE);
		const reg = createPhotoIconRegistry({ render });

		reg.ensure(h.map, [marker(1), marker(2)]);
		await flush();
		reg.ensure(h.map, [marker(1), marker(2)]); // same set again — nothing new to do
		await flush();

		expect(render).toHaveBeenCalledTimes(2);
		expect(h.calls.add).toEqual([photoIconName(1), photoIconName(2)]);
	});

	it('does not re-request a photo whose render failed', async () => {
		const h = fakeMap();
		const render = vi.fn(async () => null); // e.g. the thumb 404'd
		const reg = createPhotoIconRegistry({ render });

		reg.ensure(h.map, [marker(1)]);
		await flush();
		reg.ensure(h.map, [marker(1)]);
		await flush();

		expect(render).toHaveBeenCalledTimes(1);
		expect(h.calls.add).toEqual([]); // stays unregistered → the layer falls back to the pin
	});

	it('evicts the least-recently-wanted icons past the cap', async () => {
		const h = fakeMap();
		const reg = createPhotoIconRegistry({ render: async () => IMAGE, maxIcons: 2 });

		reg.ensure(h.map, [marker(1)]); await flush();
		reg.ensure(h.map, [marker(2)]); await flush();
		reg.ensure(h.map, [marker(1)]); await flush(); // re-stamps 1 as most recent
		reg.ensure(h.map, [marker(3)]); await flush(); // over the cap → 2 is the oldest

		expect(h.calls.remove).toEqual([photoIconName(2)]);
		expect(h.images.has(photoIconName(1))).toBe(true);
		expect(h.images.has(photoIconName(3))).toBe(true);
	});

	it('re-registers after reset() — a style swap drops every image the map held', async () => {
		const h = fakeMap();
		const render = vi.fn(async () => IMAGE);
		const reg = createPhotoIconRegistry({ render });

		reg.ensure(h.map, [marker(1)]);
		await flush();
		expect(render).toHaveBeenCalledTimes(1);

		h.images.clear(); // what setStyle does to the style's images
		reg.reset();
		reg.ensure(h.map, [marker(1)]);
		await flush();

		expect(render).toHaveBeenCalledTimes(2);
		expect(h.images.has(photoIconName(1))).toBe(true);
	});

	it('discards a render that lands after a reset instead of writing it into the new style', async () => {
		const h = fakeMap();
		let release: (v: ImageData) => void = () => {};
		const reg = createPhotoIconRegistry({ render: () => new Promise<ImageData>((r) => { release = r; }) });

		reg.ensure(h.map, [marker(1)]);
		reg.reset();      // style swapped while the fetch was in flight
		release(IMAGE);   // …and only now does it resolve
		await flush();

		expect(h.calls.add).toEqual([]);
	});

	it('destroy() unregisters everything it added', async () => {
		const h = fakeMap();
		const reg = createPhotoIconRegistry({ render: async () => IMAGE });

		reg.ensure(h.map, [marker(1), marker(2)]);
		await flush();
		reg.destroy(h.map);

		expect(h.calls.remove.sort()).toEqual([photoIconName(1), photoIconName(2)].sort());
		expect(h.images.size).toBe(0);
	});

	it('holds concurrency at the configured limit', async () => {
		const h = fakeMap();
		let live = 0, peak = 0;
		const pending: ((v: ImageData) => void)[] = [];
		const reg = createPhotoIconRegistry({
			maxInFlight: 2,
			render: () => {
				live++; peak = Math.max(peak, live);
				return new Promise<ImageData>((r) => pending.push((v) => { live--; r(v); }));
			},
		});

		reg.ensure(h.map, [marker(1), marker(2), marker(3), marker(4)]);
		await flush();
		expect(peak).toBe(2);

		while (pending.length) { pending.shift()!(IMAGE); await flush(); }
		expect(h.calls.add).toHaveLength(4); // the queue still drains completely
	});

	it('an orphaned render re-pumps the queue, so work never stalls after a reset (FE-04)', async () => {
		const h = fakeMap();
		const resolvers: ((v: ImageData) => void)[] = [];
		const render = vi.fn((_p: PhotoMarker) => new Promise<ImageData>((r) => { resolvers.push(r); }));
		const reg = createPhotoIconRegistry({ render, maxInFlight: 2 });

		// gen 0: photos 1 & 2 fill both in-flight slots.
		reg.ensure(h.map, [marker(1), marker(2)]);
		await flush();
		expect(render).toHaveBeenCalledTimes(2);

		reg.reset(); // style swap — the gen-0 renders are still physically in flight

		// gen 1, same camera: photo 1 is re-wanted (its gen-0 render is still pending), plus 3 & 4.
		// Both slots fill with 1 & 3; photo 4 waits in the queue.
		reg.ensure(h.map, [marker(1), marker(3), marker(4)]);
		await flush();
		expect(render).toHaveBeenCalledTimes(4);

		// The ORPHANED gen-0 render for photo 1 resolves. Its `.then` deletes id 1 from the in-flight set
		// — which now tracks the gen-1 render for the same photo — freeing a phantom slot. The
		// unconditional pump must start the queued photo 4 right away, not leave it stalled until some
		// other render happens to complete (the "stall until the next camera move" the audit flagged).
		must(resolvers[0], 'gen-0 render captured')(IMAGE);
		await flush();

		expect(render).toHaveBeenCalledTimes(5);
		expect(render.mock.calls.map((c) => c[0].photoId)).toContain(4);
	});

	it('a deterministically-failing render drains the whole queue without spinning (FE-04)', async () => {
		const h = fakeMap();
		const render = vi.fn(async () => null); // every thumb fails (e.g. all 404)
		const reg = createPhotoIconRegistry({ render, maxInFlight: 1 });

		reg.ensure(h.map, [marker(1), marker(2), marker(3)]);
		await flush();

		// Each failure re-pumps the next queued item from the finally; all three are attempted exactly
		// once and none is retried — a deterministic failure settles the queue instead of looping.
		expect(render).toHaveBeenCalledTimes(3);
		expect(h.calls.add).toEqual([]); // nothing registered → the layer falls back to the pin
	});
});
