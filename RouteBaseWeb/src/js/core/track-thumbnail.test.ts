import type { ShapeRect } from './core-types';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { drawThumbnail, renderTrackThumbnail, setupLazyThumbnails, type ThumbCtx } from './track-thumbnail';
import { must } from './invariant';

// ── Recording fake 2D context (jsdom has no real canvas ctx) ──────────────────
interface DrawEvent {
	m: string;
	x?: number;
	y?: number;
	r?: number;
	w?: number;
	h?: number;
	strokeStyle?: string;
	fillStyle?: string;
	lineWidth?: number;
	lineCap?: string;
	globalAlpha?: number;
	lineDash?: number[];
}

class Recorder {
	events: DrawEvent[] = [];
	private dash: number[] = [];
	strokeStyle = '';
	fillStyle = '';
	lineWidth = 0;
	lineJoin = '';
	lineCap = '';
	globalAlpha = 1;

	beginPath(): void {
		this.events.push({ m: 'beginPath' });
	}
	moveTo(x: number, y: number): void {
		this.events.push({ m: 'moveTo', x, y });
	}
	lineTo(x: number, y: number): void {
		this.events.push({ m: 'lineTo', x, y });
	}
	arc(x: number, y: number, r: number): void {
		this.events.push({ m: 'arc', x, y, r });
	}
	rect(x: number, y: number, w: number, h: number): void {
		this.events.push({ m: 'rect', x, y, w, h });
	}
	setLineDash(d: number[]): void {
		this.dash = d.slice();
	}
	stroke(): void {
		this.events.push(this.snap('stroke'));
	}
	fill(): void {
		this.events.push(this.snap('fill'));
	}
	private snap(m: string): DrawEvent {
		return {
			m,
			strokeStyle: this.strokeStyle,
			fillStyle: this.fillStyle,
			lineWidth: this.lineWidth,
			lineCap: this.lineCap,
			globalAlpha: this.globalAlpha,
			lineDash: this.dash.slice(),
		};
	}
}

function asCtx(rec: Recorder): ThumbCtx {
	return rec as unknown as ThumbCtx;
}

describe('track-thumbnail — drawThumbnail', () => {
	it('strokes the track with round caps at 0.8 alpha then plots green start and red end markers', () => {
		const rec = new Recorder();
		const gpsData = [
			{ lat: 47.0, lng: 7.0 },
			{ lat: 47.5, lng: 7.5 },
			{ lat: 48.0, lng: 8.0 },
		];
		drawThumbnail(asCtx(rec), 240, 200, { gpsData });

		const lineTos = rec.events.filter((e) => e.m === 'lineTo');
		expect(lineTos).toHaveLength(gpsData.length - 1);

		const strokes = rec.events.filter((e) => e.m === 'stroke');
		const trackStroke = must(strokes[0], 'track stroke');
		expect(trackStroke.strokeStyle).toBe('#dc3545'); // default trackColor
		expect(trackStroke.globalAlpha).toBe(0.8);
		expect(trackStroke.lineCap).toBe('round');
		expect(trackStroke.lineDash).toEqual([]);

		// Markers drawn after the track, opaque: green start then red end.
		const fills = rec.events.filter((e) => e.m === 'fill');
		const startFill = must(fills[0], 'start marker fill');
		expect(startFill.fillStyle).toBe('#28a745');
		expect(startFill.globalAlpha).toBe(1);
		expect(must(fills[1], 'end marker fill').fillStyle).toBe('#dc3545');
		const markerArcs = rec.events.filter((e) => e.m === 'arc');
		expect(markerArcs).toHaveLength(2);
		expect(must(markerArcs[0], 'first marker arc').r).toBe(4);
		// Two white rings at lineWidth 2.
		expect(strokes.slice(1).every((s) => s.strokeStyle === '#ffffff' && s.lineWidth === 2)).toBe(true);
	});

	it('draws dashed orange "any" rects and solid blue zone rects with 0.15 fill', () => {
		const rec = new Recorder();
		const shapes: ShapeRect[] = [
			{ minLat: 47.0, maxLat: 47.5, minLong: 7.0, maxLong: 7.5, role: 'any' },
			{ minLat: 47.2, maxLat: 47.8, minLong: 7.3, maxLong: 8.0, role: 'zone' },
		];
		drawThumbnail(asCtx(rec), 240, 200, { gpsData: [], shapes });

		const strokes = rec.events.filter((e) => e.m === 'stroke');
		const anyStroke = must(strokes[0], 'any-rect stroke');
		expect(anyStroke.strokeStyle).toBe('#ff9800');
		expect(anyStroke.lineDash).toEqual([6, 4]);
		const zoneStroke = must(strokes[1], 'zone-rect stroke');
		expect(zoneStroke.strokeStyle).toBe('#2196F3');
		expect(zoneStroke.lineDash).toEqual([]);

		const fills = rec.events.filter((e) => e.m === 'fill');
		expect(fills.every((f) => f.globalAlpha === 0.15)).toBe(true);
		expect(must(fills[0], 'any-rect fill').fillStyle).toBe('#ff9800');
		expect(must(fills[1], 'zone-rect fill').fillStyle).toBe('#2196F3');
	});

	it('strokes each matched-effort track thin at low alpha with no start/end markers', () => {
		const rec = new Recorder();
		const effortTracks = [
			[{ lat: 47.0, lng: 7.0 }, { lat: 47.5, lng: 7.5 }],
			[{ lat: 47.1, lng: 7.1 }, { lat: 47.6, lng: 7.6 }],
		];
		drawThumbnail(asCtx(rec), 240, 200, { gpsData: [], effortTracks, gateLines: [], shapes: [] });

		const strokes = rec.events.filter((e) => e.m === 'stroke');
		expect(strokes).toHaveLength(2); // one line per effort, no marker rings
		expect(strokes.every((s) => s.lineWidth === 1.5 && s.globalAlpha === 0.35 && s.lineCap === 'round')).toBe(true);
		// Effort traces carry no endpoint markers (would clutter at N lines).
		expect(rec.events.some((e) => e.m === 'arc')).toBe(false);
	});

	it('strokes gate lines in cyan at lineWidth 3 (zero-effort outline fallback)', () => {
		const rec = new Recorder();
		const gateLines = [{ lat1: 47.0, lng1: 7.0, lat2: 47.0, lng2: 7.5, role: 'gate' }];
		drawThumbnail(asCtx(rec), 240, 200, { gpsData: [], gateLines, shapes: [], effortTracks: [] });

		const strokes = rec.events.filter((e) => e.m === 'stroke');
		expect(strokes).toHaveLength(1);
		const gateStroke = must(strokes[0], 'gate stroke');
		expect(gateStroke.strokeStyle).toBe('#22b8cf');
		expect(gateStroke.lineWidth).toBe(3);
	});
});

// ── Lazy loader (stubbed IntersectionObserver) ────────────────────────────────
type IoCb = (entries: Array<{ target: Element; isIntersecting: boolean }>, obs: { unobserve: (el: Element) => void }) => void;

let ioCallback: IoCb;
let observed: Element[];
let unobserved: Element[];

class MockIntersectionObserver {
	constructor(cb: IoCb) {
		ioCallback = cb;
	}
	observe(el: Element): void {
		observed.push(el);
	}
	unobserve(el: Element): void {
		unobserved.push(el);
	}
	disconnect(): void {}
}

function fireIntersect(): void {
	ioCallback(
		observed.map((target) => ({ target, isIntersecting: true })),
		{ unobserve: (el) => unobserved.push(el) },
	);
}

const flush = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

describe('track-thumbnail — setupLazyThumbnails', () => {
	beforeEach(() => {
		observed = [];
		unobserved = [];
		vi.stubGlobal('IntersectionObserver', MockIntersectionObserver);
		document.body.innerHTML = '';
	});
	afterEach(() => {
		vi.unstubAllGlobals();
	});

	it('lazily renders gps→efforts→outline→no-data outcomes once each', async () => {
		document.body.innerHTML = `
			<div class="t" data-id="1"></div>
			<div class="t" data-id="2"></div>
			<div class="t" data-id="3"></div>
			<div class="t" data-id="4"></div>`;

		const fetchGps = vi.fn(async (id: string | number) =>
			id === '1' ? [{ lat: 47, lng: 7, altitude: 0, speed: 0, distance: 0 }, { lat: 48, lng: 8, altitude: 0, speed: 0, distance: 0 }] : [],
		);
		// id 2 → efforts (traced); id 3 → outline fallback (gate line); id 4 → nothing → no-data.
		const fetchEfforts = vi.fn(async (id: string | number) =>
			id === '2' ? [[{ lat: 47, lng: 7 }, { lat: 47.5, lng: 7.5 }]] : [],
		);
		const fetchShapes = vi.fn(async (id: string | number) =>
			id === '3'
				? { zones: [], gates: [{ lat1: 47, lng1: 7, lat2: 47, lng2: 7.5, role: 'gate' }] }
				: { zones: [], gates: [] },
		);
		const onProgress = vi.fn();

		setupLazyThumbnails({
			containerSelector: '.t',
			idAttribute: 'data-id',
			fetchGps,
			fetchEfforts,
			fetchShapes,
			onProgress,
		});

		expect(observed).toHaveLength(4);
		fireIntersect();
		await flush();

		const cards = document.querySelectorAll('.t');
		expect(must(cards[0], 'card 0').querySelector('canvas')).not.toBeNull(); // gps → canvas
		expect(must(cards[1], 'card 1').querySelector('canvas')).not.toBeNull(); // efforts → canvas
		expect(must(cards[2], 'card 2').querySelector('canvas')).not.toBeNull(); // gate outline → canvas
		const noDataCard = must(cards[3], 'card 3');
		expect(noDataCard.querySelector('canvas')).toBeNull(); // all empty → no-data
		expect(noDataCard.innerHTML).toContain('No GPS data');

		expect(unobserved).toHaveLength(4);
		expect(onProgress.mock.calls.at(-1)).toEqual([4, 4]);

		// Second intersect must not re-fetch (loaded guard).
		fetchGps.mockClear();
		fireIntersect();
		await flush();
		expect(fetchGps).not.toHaveBeenCalled();
	});

	it('renders the outline when the efforts fetch fails (decoupled fallback halves)', async () => {
		// One half failing must not discard the other: a 500 on efforts-summary used to reject the pair
		// and paint the error tile even though the zone/gate outline was available.
		document.body.innerHTML = `<div class="t" data-id="7"></div>`;
		const fetchGps = vi.fn(async () => []);
		const fetchEfforts = vi.fn(async () => { throw new Error('efforts 500'); });
		const fetchShapes = vi.fn(async () => ({
			zones: [], gates: [{ lat1: 47, lng1: 7, lat2: 47, lng2: 7.5, role: 'gate' }],
		}));

		setupLazyThumbnails({
			containerSelector: '.t',
			idAttribute: 'data-id',
			fetchGps,
			fetchEfforts,
			fetchShapes,
		});
		fireIntersect();
		await flush();

		const card = document.querySelector('.t')!;
		expect(card.querySelector('canvas')).not.toBeNull(); // gate outline still drawn
		expect(card.innerHTML).not.toContain('No GPS data');
	});

	it('paints errorHtml on fetch rejection and still counts progress', async () => {
		document.body.innerHTML = `<div class="t" data-id="9"></div>`;
		const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
		const fetchGps = vi.fn(async () => {
			throw new Error('boom');
		});
		const onProgress = vi.fn();

		setupLazyThumbnails({ containerSelector: '.t', idAttribute: 'data-id', fetchGps, onProgress });
		fireIntersect();
		await flush();

		const card = document.querySelector('.t')!;
		expect(card.innerHTML).toContain('Map unavailable');
		expect(onProgress.mock.calls.at(-1)).toEqual([1, 1]);
		expect(warn).toHaveBeenCalled();
		warn.mockRestore();
	});

	it('renders both containers sharing an id and counts each once (DT-07)', async () => {
		document.body.innerHTML = `<div class="t" data-id="7"></div><div class="t" data-id="7"></div>`;
		const fetchGps = vi.fn(async () => [
			{ lat: 47, lng: 7, altitude: 0, speed: 0, distance: 0 },
			{ lat: 48, lng: 8, altitude: 0, speed: 0, distance: 0 },
		]);
		const onProgress = vi.fn();

		setupLazyThumbnails({ containerSelector: '.t', idAttribute: 'data-id', fetchGps, onProgress });
		fireIntersect();
		await flush();

		const cards = document.querySelectorAll('.t');
		expect(must(cards[0], 'card 0').querySelector('canvas')).not.toBeNull(); // both render...
		expect(must(cards[1], 'card 1').querySelector('canvas')).not.toBeNull();
		expect(onProgress.mock.calls.at(-1)).toEqual([2, 2]); // ...and both count toward progress
		expect(fetchGps).toHaveBeenCalledTimes(1); // shared data fetched once
	});
});

describe('track-thumbnail — renderTrackThumbnail DPR cap (DT-11)', () => {
	it('caps the canvas backing store at 2× DPR', () => {
		const container = document.createElement('div');
		document.body.appendChild(container);
		const orig = Object.getOwnPropertyDescriptor(window, 'devicePixelRatio');
		Object.defineProperty(window, 'devicePixelRatio', { value: 3, configurable: true });

		renderTrackThumbnail({ container, gpsData: [{ lat: 47, lng: 7 }, { lat: 48, lng: 8 }] });

		const canvas = container.querySelector('canvas')!;
		expect(canvas.width).toBe(240 * 2); // FALLBACK_W(240) × capped DPR 2, never 3

		if (orig) Object.defineProperty(window, 'devicePixelRatio', orig);
		container.remove();
	});
});
