/**
 * activities-overview-progressive.ts
 *
 * Progressive quality engine for the Activities overview map.
 * Tracks per-track quality tiers and upgrades visible tracks when the
 * user zooms in or pans to reveal new areas.
 *
 * Exports:
 *   init(activityIds, opts)
 *   onViewChanged()           — debounced; call from zoomend/moveend
 *   reset()                   — clear all state (before reload)
 *   mergeUpgradedTracks(cachedTracks, newTracks, tier) → merged array
 *
 */
import type { OverviewTrack, ActivitiesOverviewProgressiveApi } from './overview-types';
import { must } from '../core/invariant';
import type { QualityTier } from '../core/core-types';
import { LatestRun } from '../core/latest-run';
import { loadActivitiesChunked, OVERVIEW_TIERS, overviewTierForZoom, isUsableLatLng } from './activities-gps-loader';

// Pad the upgrade-visibility box by ~1 viewport on each side so tracks just off-screen are
// upgraded before they're panned into view (preloads the surrounding area). Symmetric — no
// dependence on pan direction, so it's robust against direction reversals.
const PREFETCH_PAD = 1.0;

interface TrackBBox { s: number; w: number; n: number; e: number; }

let trackQualityMap: Record<string, number> = {};
// Tier currently being fetched per track (only set while a request is in flight). Guards against
// the debounced view-change re-issuing a tier already on the wire for the same ids — the source of
// the repeated identical chunk seen in the server log.
let inFlightTier: Record<string, number> = {};
// Per-run ownership of in-flight marks. An aborted run's finally must only clear marks it set —
// a newer overlapping run re-marks the same id at the same tier, and without this token the old
// run's abort would delete the new run's mark → the id gets re-requested while already on the wire.
let _upgradeSeq = 0;
let inFlightOwner: Record<string, number> = {};
let cachedActivityIds: number[] = [];
let viewChangeTimer: ReturnType<typeof setTimeout> | null = null;
// Aborts the in-flight upgrade fetch when a newer view change supersedes it (newest viewport wins).
const upgradeRun = new LatestRun();
// Per-track bounding box, computed once and reused — a track's extent is stable across tier
// upgrades, so the visibility scan stays O(tracks) instead of O(tracks × points) each pan.
const trackBBoxCache = new Map<number, TrackBBox>();

// Callbacks set by the orchestrator
let _onTracksUpgraded: ((validTracks: OverviewTrack[], tier: QualityTier) => void) | null = null;
let _getZoom: (() => number) | null = null;
let _getBounds:
	| (() => {
			south: number;
			west: number;
			north: number;
			east: number;
	  } | null)
	| null = null;
let _getCachedTracks: (() => OverviewTrack[] | null) | null = null;


/** Compute (and memoize) a track's lat/lng bounding box. Null when it has no usable points. */
function getTrackBBox(track: OverviewTrack): TrackBBox | null {
	const cached = trackBBoxCache.get(track.id);
	if (cached) return cached;
	if (!track.points || track.points.length === 0) return null;

	let s = Infinity, w = Infinity, n = -Infinity, e = -Infinity;
	for (let j = 0; j < track.points.length; j++) {
		const pt = must(track.points[j], 'points[j] (loop-bounded)');
		if (!isUsableLatLng(pt)) continue;
		const lat = pt[0], lng = pt[1];
		if (lat < s) s = lat;
		if (lat > n) n = lat;
		if (lng < w) w = lng;
		if (lng > e) e = lng;
	}
	if (s === Infinity) return null;
	const box: TrackBBox = { s, w, n, e };
	trackBBoxCache.set(track.id, box);
	return box;
}

function getVisibleTrackIds(): number[] {
	const bounds = _getBounds ? _getBounds() : null;
	const cachedTracks = _getCachedTracks ? _getCachedTracks() : null;
	if (!bounds || !cachedTracks || cachedTracks.length === 0) {
		return cachedActivityIds;
	}

	// Expand the viewport by ~PREFETCH_PAD viewports on each side so neighbours are upgraded ahead
	// of being panned into view.
	const latPad = (bounds.north - bounds.south) * PREFETCH_PAD;
	const lngPad = (bounds.east - bounds.west) * PREFETCH_PAD;
	const south = bounds.south - latPad;
	const north = bounds.north + latPad;
	const west = bounds.west - lngPad;
	const east = bounds.east + lngPad;

	const visible: number[] = [];
	for (const track of cachedTracks) {
		const box = getTrackBBox(track);
		if (!box) continue;
		// Box-overlap test (cheap, O(tracks)) — the padded viewport intersects the track's extent.
		if (box.s <= north && box.n >= south && box.w <= east && box.e >= west) {
			visible.push(track.id);
		}
	}

	// An empty `visible` means "tested, nothing in view" — return it. (The "cannot test visibility"
	// case fell through to cachedActivityIds above.) Never fall back to every id here: at zoom ≥ 11
	// the full tier is uncapped, so upgrading the whole set would fetch tens of MB of GPS (MC-03).
	return visible;
}

function getTracksNeedingUpgrade(visibleIds: number[], neededTier: number): number[] {
	const needsUpgrade: number[] = [];
	for (const id of visibleIds) {
		const currentTier = trackQualityMap[id];
		const requestedTier = inFlightTier[id];
		// Skip if we already have — or are already fetching — this tier (or better) for this track.
		if ((currentTier === undefined || currentTier < neededTier)
			&& (requestedTier === undefined || requestedTier < neededTier)) {
			needsUpgrade.push(id);
		}
	}
	return needsUpgrade;
}

function upgradeVisibleTracks(): void {
	if (!cachedActivityIds || cachedActivityIds.length === 0) return;

	const currentZoom = _getZoom ? _getZoom() : 0;
	const neededTier = overviewTierForZoom(currentZoom);
	const tier = OVERVIEW_TIERS[neededTier];
	if (!tier) return;

	const visibleIds = getVisibleTrackIds();
	const idsToUpgrade = getTracksNeedingUpgrade(visibleIds, neededTier);

	if (idsToUpgrade.length === 0) return;

	// Supersede any in-flight upgrade — the newest viewport wins.
	const signal = upgradeRun.begin();
	const runToken = ++_upgradeSeq;

	// Mark this tier as in-flight for the batch so a debounced re-trigger doesn't re-request it.
	idsToUpgrade.forEach((id: number) => { inFlightTier[id] = neededTier; inFlightOwner[id] = runToken; });

	// Quality upgrades only swap point density on existing tracks — the track count is unchanged,
	// so we deliberately leave the #mapStatus count alone. Loading the higher tier through the
	// chunked loader streams detail in per chunk (each chunk applies incrementally via
	// _onTracksUpgraded), spreading the work instead of one big request + one big redraw.
	loadActivitiesChunked({
		ids: idsToUpgrade,
		maxPointsPerTrack: tier.maxPoints,
		onChunk: (tracks: OverviewTrack[]) => {
			if (signal.aborted) return;
			const validTracks = tracks.filter((t: OverviewTrack) => t.points && t.points.length > 0);
			if (validTracks.length === 0) return;
			validTracks.forEach((t: OverviewTrack) => {
				trackQualityMap[t.id] = neededTier;
			});
			if (_onTracksUpgraded) _onTracksUpgraded(validTracks, tier);
		},
		signal,
	}).catch((err: unknown) => {
		if ((err as DOMException)?.name === "AbortError") return; // superseded — stay silent
		console.warn("Track quality upgrade failed:", err);
	}).finally(() => {
		// Clear our in-flight marks. Delivered tracks are now guarded by trackQualityMap; undelivered
		// (aborted/failed) ones become re-requestable. Only clear marks still ours — a newer run that
		// raised the tier for an id owns that marker now.
		idsToUpgrade.forEach((id: number) => {
			if (inFlightOwner[id] === runToken) {
				delete inFlightTier[id];
				delete inFlightOwner[id];
			}
		});
	});
}

export function onViewChanged(): void {
	if (viewChangeTimer) clearTimeout(viewChangeTimer);
	viewChangeTimer = setTimeout(() => {
		upgradeVisibleTracks();
	}, 600);
}

export function mergeUpgradedTracks(cachedTracks: OverviewTrack[], newTracks: OverviewTrack[], _tier: QualityTier): OverviewTrack[] {
	const upgradedIds: Record<string, OverviewTrack> = {};
	newTracks.forEach((t: OverviewTrack) => {
		upgradedIds[t.id] = t;
	});
	return cachedTracks.map((t: OverviewTrack) => upgradedIds[t.id] || t);
}

export function init(activityIds: number[], opts: Parameters<ActivitiesOverviewProgressiveApi["init"]>[1]): void {
	// Re-scoping to a new target set: a still-running upgrade belongs to the old set — cancel it,
	// or its chunks keep streaming and repopulate the maps we're about to clear.
	upgradeRun.cancel();
	if (viewChangeTimer) {
		clearTimeout(viewChangeTimer);
		viewChangeTimer = null;
	}
	cachedActivityIds = activityIds || [];
	trackQualityMap = {};
	inFlightTier = {};
	inFlightOwner = {};
	cachedActivityIds.forEach((id: number) => {
		trackQualityMap[id] = 0;
	});
	_onTracksUpgraded = opts.onTracksUpgraded || null;
	_getZoom = opts.getZoom || null;
	_getBounds = opts.getBounds || null;
	_getCachedTracks = opts.getCachedTracks || null;
}

export function reset(): void {
	trackQualityMap = {};
	inFlightTier = {};
	inFlightOwner = {};
	cachedActivityIds = [];
	trackBBoxCache.clear();
	upgradeRun.cancel();
	if (viewChangeTimer) {
		clearTimeout(viewChangeTimer);
		viewChangeTimer = null;
	}
}

