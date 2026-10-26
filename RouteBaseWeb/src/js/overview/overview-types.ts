/**
 * Overview slice contracts — the activities/segments listing page DTOs, server
 * bootstraps, and the overview map/progressive module APIs.
 *
 * Formerly ambient globals (GodFileSplit).
 */

import type { QualityTier } from '../core/core-types';
import type {
	ActivitiesGridBootstrap as ActivitiesGridBootstrapContract,
	GridSort,
	OverviewTrackDto,
	SegmentsGridRow,
} from '../generated/contracts';

// ---------------------------------------------------------------------------
// Overview track — API response from POST /api/activities/gps-data.
// Generated from C# OverviewTrackDto (Models/ActivityContracts.cs). The wire ships `points` as
// `number[][]`; we refine it to `[lat, lng]` tuples client-side — the server always emits 2-element
// pairs (gps-data projection), and consumers index p[0]/p[1]. Everything else ties to the contract.
// ---------------------------------------------------------------------------

export type OverviewTrack = Omit<OverviewTrackDto, 'points'> & { points: Array<[number, number]> };

// ---------------------------------------------------------------------------
// Photos overlay — API response row from GET /api/photos/map-markers.
// Generated from C# PhotoMarkerDto (Models/Photos/PhotoContracts.cs); the overlay core aliases it as
// PhotoMarker (photos/photo-map-types.ts), re-exported here under its wire name for discoverability.
// ---------------------------------------------------------------------------

export type { PhotoMarker as PhotoMarkerDto } from "../photos/photo-map-types";

// Bootstrap payload for the client-rendered activities grid (Razor JSON island on Activities.cshtml,
// consumed by overview/activities-grid.ts). Generated from C# ActivitiesGridBootstrap (island component
// schema, D27); initialData is the same ActivityGridResponse the grid API serves.
//
// sort.direction refined to 'asc'|'desc': activities-grid.ts assigns boot.sort into GridView's GridSort
// (grid-types), which narrows direction; the generated schema widens it to string.
export type ActivitiesGridBootstrap =
	Omit<ActivitiesGridBootstrapContract, 'sort'>
	& { sort: Omit<GridSort, 'direction'> & { direction: 'asc' | 'desc' } };

/** One row of the segments sort/filter controls grid (Segments/Index).
 *  Generated from C# SegmentsGridRow (island component schema, D27). */
export type { SegmentsGridRow };

/** Bootstrap for the segments controls grid — all visible segment rows + filter options. */
export interface SegmentsGridBootstrap {
	rows: SegmentsGridRow[];
	/** Multiple visible users → show the Owner column + filter. */
	multiUser: boolean;
	/** Distinct activity types for the Type combo filter. */
	typeOptions: string[];
	/** Distinct owner labels for the Owner combo filter (present only in multi-user mode). */
	ownerOptions: string[];
	/** Distinct visibility values present, for the Visibility combo filter. */
	visibilityOptions: string[];
}

// ---------------------------------------------------------------------------
// Activities Overview Progressive (activities-overview-progressive.ts)
// ---------------------------------------------------------------------------

export interface ActivitiesOverviewProgressiveApi {
	init(
		activityIds: number[],
		opts: {
			onTracksUpgraded?: (validTracks: OverviewTrack[], tier: QualityTier) => void;
			getZoom?: () => number;
			getBounds?: () => { south: number; west: number; north: number; east: number } | null;
			getCachedTracks?: () => OverviewTrack[] | null;
		},
	): void;
	onViewChanged(): void;
	reset(): void;
	mergeUpgradedTracks(cachedTracks: OverviewTrack[], newTracks: OverviewTrack[], tier: QualityTier): OverviewTrack[];
}
