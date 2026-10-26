/**
 * Segment Statistics — the single owner of every segment data URL.
 *
 * Exists so the page's modules never hard-code "/api/SegmentsApi". The base comes from the page config
 * (`apiBase`), which is "/api/SegmentsApi" on the signed-in page and "/s/{token}/api/segment" on an
 * anonymous share view — so ONE set of modules serves both routes, and a new fetch added anywhere else
 * is visible as a literal that does not belong.
 *
 * The two routes differ in more than the base, and normalising that difference is this module's job:
 *
 *   | | signed-in | share |
 *   |---|---|---|
 *   | tracks | POST bulk-tracks with an id list | GET tracks?skip&take — NO ids, server-derived set |
 *   | one track | GET activities/{id}/full-track → array | GET tracks/{id} → {trackId, points} |
 *   | performance | POST performance-data/bulk | GET performance?effortIds=… → {efforts} |
 *
 * The share surface is GET-only (ShareEndpointAllowlistTests enforces it), and `tracks` takes no ids at
 * all — it pages over a server-derived set, so it cannot return a track the grant does not permit. See
 * (Why `tracks` ignores the client's id list: the server scopes it.)
 *
 * Depends on: ctx (shared context from segment-statistics.ts)
 */
import type { TrackPoint } from '../core/core-types';
import type { EffortPerformanceDataWire } from './segment-stats-effort-types';
import type {
	ShareEffortPerformanceResponse, ShareSegmentTrackDto, ShareSegmentTracksResponse, TrackPointDto,
} from '../generated/contracts';
import { ctx } from './segment-statistics';

const DEFAULT_BASE = '/api/SegmentsApi';

/** The configured base, without a trailing slash. */
export function apiBase(): string {
	const base = ctx?.config?.apiBase || DEFAULT_BASE;
	return base.endsWith('/') ? base.slice(0, -1) : base;
}

/** True on an anonymous share view. Derived from the base rather than a second config flag, so the two
 *  can never disagree about which transport to use. */
export function isShareRoute(): boolean {
	return apiBase().startsWith('/s/');
}

async function asJson<T>(resp: Response): Promise<T> {
	if (!resp.ok) throw new Error('HTTP ' + resp.status);
	const body: T = await resp.json();
	return body;
}

// Share-route wire shapes come generated from Models/Sharing/ShareSegmentContracts.cs (their `points`
// are C# GpsCoordinate {lat,lng,gpsId} — structurally a TrackPoint).

/** One page of the share route's server-derived track set. Share-only; the signed-in route chunks by id. */
export async function fetchTracksPage(
	skip: number,
	take: number,
	maxPoints: number,
): Promise<{ tracks: Record<string, TrackPoint[]>; total: number }> {
	const url = `${apiBase()}/tracks?skip=${skip}&take=${take}&maxPoints=${maxPoints || 0}`;
	const body = await asJson<ShareSegmentTracksResponse>(await fetch(url));

	const tracks: Record<string, TrackPoint[]> = {};
	for (const t of body.tracks ?? []) tracks[String(t.trackId)] = t.points ?? [];
	return { tracks, total: body.total ?? 0 };
}

/** One chunk of effort tracks by id, as a { trackId: points[] } dictionary. Signed-in route only. */
export function fetchTrackChunk(
	trackIds: number[],
	maxPoints: number,
): Promise<Record<string, TrackPoint[]>> {
	return fetch(`${apiBase()}/activities/bulk-tracks`, {
		method: 'POST',
		headers: { 'Content-Type': 'application/json' },
		body: JSON.stringify({ trackIds: trackIds, maxPointsPerTrack: maxPoints || 0 }),
	}).then((resp) => asJson<Record<string, TrackPointDto[]>>(resp));
}

/** Full-resolution GPS for one track (the click-to-upgrade path), or null when it is not available. */
export async function fetchSingleTrack(
	trackId: number,
	maxPoints: number,
): Promise<TrackPoint[] | null> {
	if (isShareRoute()) {
		const url = `${apiBase()}/tracks/${trackId}${maxPoints > 0 ? `?maxPoints=${maxPoints}` : ''}`;
		const resp = await fetch(url);
		// 404 here is the grant refusing a track it does not cover — indistinguishable from an unknown
		// id by design, so there is nothing to report to the user.
		if (!resp.ok) return null;
		const body: ShareSegmentTrackDto = await resp.json();
		return body?.points ?? null;
	}

	const resp = await fetch(buildTrackUrl(trackId, maxPoints));
	if (!resp.ok) return null;
	const body: TrackPointDto[] = await resp.json();
	return body;
}

/** Signed-in full-track URL. Kept exported because the progressive loader's API surface exposes it. */
export function buildTrackUrl(trackId: number, maxPoints: number): string {
	if (isShareRoute()) {
		return `${apiBase()}/tracks/${trackId}${maxPoints > 0 ? `?maxPoints=${maxPoints}` : ''}`;
	}
	const url = `${apiBase()}/activities/${trackId}/full-track`;
	return maxPoints && maxPoints > 0 ? `${url}?maxPoints=${maxPoints}` : url;
}

/** One chunk of per-point effort performance data, in the columnar wire form. */
export async function fetchPerfChunk(
	segmentId: number | undefined,
	effortIds: number[],
	maxPointsPerEffort: number,
): Promise<EffortPerformanceDataWire[]> {
	if (isShareRoute()) {
		// GET with a query string: the share surface is GET-only, which is also why the server caps ids
		// per request (200). The chart/replay loaders chunk at 50, comfortably inside it.
		const url = `${apiBase()}/performance?effortIds=${effortIds.join(',')}`
			+ `&maxPoints=${maxPointsPerEffort || 0}`;
		const body = await asJson<ShareEffortPerformanceResponse>(await fetch(url));
		return body.efforts ?? [];
	}

	return fetch(`${apiBase()}/${segmentId}/performance-data/bulk`, {
		method: 'POST',
		headers: { 'Content-Type': 'application/json' },
		body: JSON.stringify({ effortIds, maxPointsPerEffort }),
	}).then((resp) => asJson<EffortPerformanceDataWire[]>(resp));
}
