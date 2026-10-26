// track-thumbnail.ts — 2D-canvas track-shape thumbnails + lazy loader.
// Leaflet-free replacement for the minimap factory (CM-02 D4); same consumer
// contract as the old lazy-minimap loader minus the {maps} registry — a canvas
// holds no GL/tile resources to tear down.

import type { GateLine, SegmentOutline, ShapeRect } from './core-types';
import {
	computeGeoBounds,
	computeTileLayout,
	fitTransform,
	type GeoBounds,
	type ThumbPoint,
} from './track-thumbnail-geometry';
import { getTileSource, loadLayoutTiles, paintTiles } from './track-thumbnail-tiles';
import { must } from './invariant';
import { observeWhenSettled } from './settled-observer';

const THUMB_PADDING = 12; // css px: marker r4 + white ring 2 + max track weight/2 (Leaflet used [10,10])
const FALLBACK_W = 240; // display:none guard — matches .seg-card-map
const FALLBACK_H = 200;

const DEFAULT_TRACK_COLOR = '#dc3545'; // factory parity
const START_COLOR = '#28a745'; // green start dot
const END_COLOR = '#dc3545'; // red end dot
const MARKER_RING = '#ffffff';
const ZONE_COLOR = '#2196F3';
const ANY_COLOR = '#ff9800';
const GATE_COLOR = '#22b8cf'; // cyan — matches the segment-stats gate/effort accents

/** A lat/lng-only track (one matched effort). Lighter than GpsPoint — the preview needs no channels. */
type EffortTrack = ReadonlyArray<{ lat: number; lng: number }>;

const DEFAULT_NO_DATA_HTML =
	'<div class="d-flex align-items-center justify-content-center h-100 text-muted"><small>No GPS data</small></div>';
const DEFAULT_ERROR_HTML =
	'<div class="d-flex align-items-center justify-content-center h-100 text-muted"><small>Map unavailable</small></div>';

/**
 * Minimal 2D-context surface the drawing code touches — lets tests pass a
 * recorder (jsdom's canvas has no real 2D context).
 */
export interface ThumbCtx {
	beginPath(): void;
	moveTo(x: number, y: number): void;
	lineTo(x: number, y: number): void;
	arc(x: number, y: number, r: number, startAngle: number, endAngle: number): void;
	rect(x: number, y: number, w: number, h: number): void;
	stroke(): void;
	fill(): void;
	setLineDash(segments: number[]): void;
	// The DOM unions, so a real CanvasRenderingContext2D assigns with no cast; the renderer only
	// ever WRITES plain color strings.
	strokeStyle: string | CanvasGradient | CanvasPattern;
	fillStyle: string | CanvasGradient | CanvasPattern;
	lineWidth: number;
	lineJoin: string;
	lineCap: string;
	globalAlpha: number;
}

export interface TrackThumbnailOpts {
	container: HTMLElement;
	gpsData: ReadonlyArray<{ lat: number; lng: number }>;
	shapes?: ReadonlyArray<ShapeRect>;
	/** Shape/gate segments: one polyline per matched effort, drawn over the outline (gpsData empty). */
	effortTracks?: ReadonlyArray<EffortTrack>;
	/** Shape/gate segments: the directed gate lines, drawn as part of the outline. */
	gateLines?: ReadonlyArray<GateLine>;
	trackColor?: string; // default '#dc3545'
	trackWeight?: number; // default 2
}

type DrawOpts = Omit<TrackThumbnailOpts, 'container'>;

/**
 * Draw the thumbnail onto a provided ctx at css-pixel coords (the caller has
 * already applied the DPR transform). Exported for tests. No-op when there is
 * nothing to project.
 */
export function drawThumbnail(ctx: ThumbCtx, cssW: number, cssH: number, opts: DrawOpts): void {
	const bounds = boundsFor(opts);
	if (!bounds) return;
	// Equirectangular fit — the offline / tiles-failed fallback projection.
	drawGeometry(ctx, opts, fitTransform(bounds, cssW, cssH, THUMB_PADDING));
}

/** Bounds over gps, or over the shape/gate outline + effort tracks when there's no track. */
function boundsFor(opts: DrawOpts): GeoBounds | null {
	const gps = opts.gpsData || [];
	if (gps.length > 0) return computeGeoBounds(gps);
	return computeGeoBounds([], opts.shapes || [], opts.effortTracks || [], opts.gateLines || []);
}

/** Draw the polyline track (with markers), or the shape/gate outline + matched effort traces. */
function drawGeometry(
	ctx: ThumbCtx,
	opts: DrawOpts,
	project: (lat: number, lng: number) => ThumbPoint,
): void {
	const gps = opts.gpsData || [];
	if (gps.length > 0) {
		drawTrack(ctx, gps, project, opts.trackColor || DEFAULT_TRACK_COLOR, opts.trackWeight || 2);
		return;
	}
	// Shape/gate segment: outline first (zones + gates), matched effort traces on top.
	drawShapes(ctx, opts.shapes || [], project);
	drawGateLines(ctx, opts.gateLines || [], project);
	drawEffortTracks(ctx, opts.effortTracks || [], project, opts.trackColor || DEFAULT_TRACK_COLOR);
}

/** Matched-effort traces: one thin line each. Many overlapping efforts → low alpha so density reads as
 *  heat rather than a solid blob. No S/E markers (they'd clutter at N lines). */
function drawEffortTracks(
	ctx: ThumbCtx,
	tracks: ReadonlyArray<EffortTrack>,
	project: (lat: number, lng: number) => ThumbPoint,
	color: string,
): void {
	if (tracks.length === 0) return;
	ctx.setLineDash([]);
	ctx.lineJoin = 'round';
	ctx.lineCap = 'round';
	ctx.strokeStyle = color;
	ctx.lineWidth = 1.5;
	ctx.globalAlpha = tracks.length > 1 ? 0.35 : 0.8;
	for (let t = 0; t < tracks.length; t++) {
		const tr = must(tracks[t], 't < length (loop-bounded)');
		if (tr.length === 0) continue;
		ctx.beginPath();
		const p0 = must(tr[0], 'non-empty (guarded above)');
		const first = project(p0.lat, p0.lng);
		ctx.moveTo(first.x, first.y);
		for (let i = 1; i < tr.length; i++) {
			const pi = must(tr[i], 'i < length (loop-bounded)');
			const p = project(pi.lat, pi.lng);
			ctx.lineTo(p.x, p.y);
		}
		ctx.stroke();
	}
	ctx.globalAlpha = 1;
}

/** Gate lines: a short stroked segment per gate, distinct from the dashed "any" zone rects. */
function drawGateLines(
	ctx: ThumbCtx,
	gates: ReadonlyArray<GateLine>,
	project: (lat: number, lng: number) => ThumbPoint,
): void {
	if (gates.length === 0) return;
	ctx.setLineDash([]);
	ctx.lineCap = 'round';
	ctx.strokeStyle = GATE_COLOR;
	ctx.lineWidth = 3;
	ctx.globalAlpha = 0.9;
	for (let i = 0; i < gates.length; i++) {
		const g = must(gates[i], 'i < length (loop-bounded)');
		const a = project(g.lat1, g.lng1);
		const b = project(g.lat2, g.lng2);
		ctx.beginPath();
		ctx.moveTo(a.x, a.y);
		ctx.lineTo(b.x, b.y);
		ctx.stroke();
	}
	ctx.globalAlpha = 1;
}

function drawShapes(
	ctx: ThumbCtx,
	shapes: ReadonlyArray<ShapeRect>,
	project: (lat: number, lng: number) => ThumbPoint,
): void {
	for (let i = 0; i < shapes.length; i++) {
		const s = must(shapes[i], 'i < length (loop-bounded)');
		const isAny = (s.role || '').toLowerCase() === 'any';
		const color = isAny ? ANY_COLOR : ZONE_COLOR;
		// Canvas y grows downward → maxLat maps to the top edge.
		const nw = project(s.maxLat, s.minLong);
		const se = project(s.minLat, s.maxLong);

		ctx.setLineDash(isAny ? [6, 4] : []);
		ctx.strokeStyle = color;
		ctx.lineWidth = 2;
		ctx.globalAlpha = 1;
		ctx.beginPath();
		ctx.rect(nw.x, nw.y, se.x - nw.x, se.y - nw.y);
		ctx.stroke();

		ctx.fillStyle = color;
		ctx.globalAlpha = 0.15;
		ctx.fill();
	}
	ctx.globalAlpha = 1;
	ctx.setLineDash([]);
}

function drawTrack(
	ctx: ThumbCtx,
	gps: ReadonlyArray<{ lat: number; lng: number }>,
	project: (lat: number, lng: number) => ThumbPoint,
	color: string,
	weight: number,
): void {
	ctx.setLineDash([]);
	ctx.lineJoin = 'round';
	ctx.lineCap = 'round';
	ctx.strokeStyle = color;
	ctx.lineWidth = weight;
	ctx.globalAlpha = 0.8;
	ctx.beginPath();
	const g0 = must(gps[0], 'non-empty (caller-guarded)');
	const first = project(g0.lat, g0.lng);
	ctx.moveTo(first.x, first.y);
	for (let i = 1; i < gps.length; i++) {
		const gi = must(gps[i], 'i < length (loop-bounded)');
		const p = project(gi.lat, gi.lng);
		ctx.lineTo(p.x, p.y);
	}
	ctx.stroke();

	// Endpoint markers, drawn opaque over the track.
	ctx.globalAlpha = 1;
	const gLast = must(gps.at(-1), 'non-empty (caller-guarded)');
	const last = project(gLast.lat, gLast.lng);
	drawMarker(ctx, first, START_COLOR);
	drawMarker(ctx, last, END_COLOR);
}

function drawMarker(ctx: ThumbCtx, p: ThumbPoint, color: string): void {
	ctx.beginPath();
	ctx.arc(p.x, p.y, 4, 0, Math.PI * 2);
	ctx.fillStyle = color;
	ctx.fill();
	ctx.lineWidth = 2;
	ctx.strokeStyle = MARKER_RING;
	ctx.stroke();
}

/**
 * DOM entry: clears the container, appends a DPR-sized <canvas> (role="img",
 * aria-label "Route shape"), and draws. Idempotent — safe to re-run on resize.
 */
export function renderTrackThumbnail(opts: TrackThumbnailOpts): void {
	const container = opts.container;
	const cssW = container.clientWidth || FALLBACK_W;
	const cssH = container.clientHeight || FALLBACK_H;
	const dpr = Math.min(window.devicePixelRatio || 1, 2); // DT-11: cap at 2× — 3× ≈ 1.2 GB canvas at feed scale

	const canvas = document.createElement('canvas');
	canvas.width = Math.round(cssW * dpr);
	canvas.height = Math.round(cssH * dpr);
	canvas.style.width = '100%';
	canvas.style.height = '100%';
	canvas.style.display = 'block';
	canvas.setAttribute('role', 'img');
	canvas.setAttribute('aria-label', 'Route shape');

	const ctx = canvas.getContext('2d');
	container.textContent = '';
	container.appendChild(canvas);
	container.classList.add('minimap--rendered');

	if (!ctx) return;
	ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
	// Paint the shape-only fallback immediately (no layout shift), then upgrade to
	// the raster basemap + Web-Mercator track once tiles load.
	drawThumbnail(ctx, cssW, cssH, opts);
	const bounds = boundsFor(opts);
	if (bounds) void enhanceWithTiles(ctx, cssW, cssH, dpr, opts, bounds);
}

/**
 * Progressive raster upgrade: fetch the tile source, load the tiles for this
 * viewport, and repaint tiles + scrim + Web-Mercator track over the fallback.
 * Any failure (offline, no source, all tiles failed) leaves the fallback intact.
 */
export async function enhanceWithTiles(
	ctx: CanvasRenderingContext2D,
	cssW: number,
	cssH: number,
	dpr: number,
	opts: DrawOpts,
	bounds: GeoBounds,
): Promise<void> {
	const source = await getTileSource();
	if (!source) return;
	const layout = computeTileLayout(bounds, cssW, cssH, THUMB_PADDING, source);
	if (!layout || layout.tiles.length === 0) return;

	const loaded = await loadLayoutTiles(layout, source);
	// DT-05: only upgrade on FULL coverage — a partial set would clearRect the complete
	// equirectangular fallback and paint holes where tiles failed (strictly worse).
	if (loaded.length !== layout.tiles.length) return;

	ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
	ctx.clearRect(0, 0, cssW, cssH);
	paintTiles(ctx, loaded, cssW, cssH);
	drawGeometry(ctx, opts, layout.project);
}

export interface LazyThumbnailOpts {
	containerSelector: string;
	idAttribute: string;
	/** Minimal lat/lng points — the renderer reads no other channel (full GpsPoint[] still assignable). */
	fetchGps: (id: string | number) => Promise<ReadonlyArray<{ lat: number; lng: number }> | null>;
	/** Shape/gate segments (fetchGps empty): matched effort tracks, drawn over the outline. */
	fetchEfforts?: (id: string | number) => Promise<ReadonlyArray<EffortTrack> | null>;
	/** Shape/gate segments (fetchGps empty): the zone + gate outline (drawn under the efforts, or alone). */
	fetchShapes?: (id: string | number) => Promise<SegmentOutline | null>;
	trackColor?: string;
	trackWeight?: number;
	noDataHtml?: string;
	errorHtml?: string;
	rootMargin?: string;
	/** Reports how many thumbnails have finished (incl. no-data/error) out of total. */
	onProgress?: (loaded: number, total: number) => void;
}

/**
 * Lazy-load canvas thumbnails using IntersectionObserver — same contract as the
 * old Leaflet lazy-minimap loader minus the {maps} registry (P17). Renders once
 * per card on first intersect; caches fetched data and re-renders on resize.
 */
export function setupLazyThumbnails(opts: LazyThumbnailOpts): void {
	const trackColor = opts.trackColor || DEFAULT_TRACK_COLOR;
	const trackWeight = opts.trackWeight || 2;
	const noDataHtml = opts.noDataHtml || DEFAULT_NO_DATA_HTML;
	const errorHtml = opts.errorHtml || DEFAULT_ERROR_HTML;
	const rootMargin = opts.rootMargin || '200px';
	const onProgress = opts.onProgress;

	// DT-07: cache the fetch OUTCOME by id (shared across duplicate ids) but drive rendering per
	// container, so two containers with the same id both render and both count toward progress.
	type Outcome = DrawOpts | 'nodata' | 'error';
	const dataCache = new Map<string, Outcome>();
	const inflight = new Map<string, Promise<void>>();
	const rendered = new Map<HTMLElement, DrawOpts>();
	const lastSize = new Map<HTMLElement, { w: number; h: number }>();

	const containers = document.querySelectorAll(opts.containerSelector);
	if (containers.length === 0) return;

	const total = containers.length;
	let finished = 0;
	// A thumbnail is "finished" once it has rendered, fallen back to no-data, or
	// errored — each outcome counts once toward the progress total.
	const markFinished = (): void => {
		finished++;
		if (onProgress) onProgress(finished, total);
	};

	// One shared ResizeObserver re-renders rendered cards from cache when their
	// size changes (fluid dashboard cards); rAF-coalesced (P15).
	const dirty = new Set<HTMLElement>();
	let rafId = 0;
	const resizeObserver =
		typeof ResizeObserver !== 'undefined'
			? new ResizeObserver((entries) => {
					let any = false;
					for (const entry of entries) {
						const el = entry.target as HTMLElement;
						const prev = lastSize.get(el);
						if (
							!prev ||
							Math.abs(prev.w - el.clientWidth) > 1 ||
							Math.abs(prev.h - el.clientHeight) > 1
						) {
							dirty.add(el);
							any = true;
						}
					}
					if (!any || rafId) return;
					rafId = requestAnimationFrame(() => {
						rafId = 0;
						const targets = Array.from(dirty);
						dirty.clear();
						for (const el of targets) {
							const cached = rendered.get(el);
							if (cached) renderAndCache(el, cached);
						}
					});
				})
			: null;

	function renderAndCache(container: HTMLElement, drawOpts: DrawOpts): void {
		renderTrackThumbnail({ container, ...drawOpts });
		rendered.set(container, drawOpts);
		lastSize.set(container, { w: container.clientWidth, h: container.clientHeight });
		if (resizeObserver) resizeObserver.observe(container);
	}

	// Apply a resolved outcome to one specific container (each container counts once).
	function applyOutcome(container: HTMLElement, outcome: Outcome): void {
		// eslint-disable-next-line no-unsanitized/property -- noDataHtml defaults to the module DEFAULT_* literal; no caller overrides it (S2 census grep)
		if (outcome === 'nodata') container.innerHTML = noDataHtml;
		// eslint-disable-next-line no-unsanitized/property -- errorHtml defaults to the module DEFAULT_* literal; no caller overrides it (S2 census grep)
		else if (outcome === 'error') container.innerHTML = errorHtml;
		else renderAndCache(container, outcome);
		markFinished();
	}

	async function loadThumbnail(container: HTMLElement): Promise<void> {
		// containerSelector does not require idAttribute, so markup drift is reachable here:
		// fail the tile instead of fetching a bogus "null" id.
		const id = container.getAttribute(opts.idAttribute);
		if (id === null) {
			console.warn(`Thumbnail container is missing ${opts.idAttribute}`);
			applyOutcome(container, 'error');
			return;
		}

		const cached = dataCache.get(id);
		if (cached !== undefined) { applyOutcome(container, cached); return; }

		let pending = inflight.get(id);
		if (!pending) {
			// Fetch once per id; store only the outcome, never a single container.
			pending = (async (): Promise<void> => {
				try {
					const gpsData = await opts.fetchGps(id);
					if (!gpsData || gpsData.length === 0) {
						// Shape-defined segments carry no track: trace the matched efforts over the
						// zone/gate outline. Fetched together but DECOUPLED — one half failing must not
						// discard the other (a 500 on efforts-summary used to paint the error tile even
						// though the outline was available). Both failing is still an error tile.
						const [effortRes, outlineRes] = await Promise.allSettled([
							opts.fetchEfforts ? opts.fetchEfforts(id) : Promise.resolve(null),
							opts.fetchShapes ? opts.fetchShapes(id) : Promise.resolve(null),
						]);
						if (effortRes.status === 'rejected' && outlineRes.status === 'rejected') throw effortRes.reason;
						const effortTracks = effortRes.status === 'fulfilled' ? effortRes.value : null;
						const outline = outlineRes.status === 'fulfilled' ? outlineRes.value : null;
						const shapes = outline?.zones ?? [];
						const gateLines = outline?.gates ?? [];
						const tracks = effortTracks ?? [];
						dataCache.set(id, (tracks.length > 0 || shapes.length > 0 || gateLines.length > 0)
							? { gpsData: [], effortTracks: tracks, shapes, gateLines, trackColor, trackWeight }
							: 'nodata');
					} else {
						dataCache.set(id, { gpsData, trackColor, trackWeight });
					}
				} catch (error) {
					console.warn('Thumbnail load failed for ' + id + ':', error);
					dataCache.set(id, 'error');
				}
			})();
			inflight.set(id, pending);
		}
		await pending;
		inflight.delete(id);
		applyOutcome(container, must(dataCache.get(id), 'inflight fetch stores an outcome for its id'));
	}

	// Fires only once the user settles on a card, not while flinging past it (P-scroll).
	observeWhenSettled({
		elements: containers,
		rootMargin,
		onActivate: (el) => void loadThumbnail(el as HTMLElement),
	});
}
