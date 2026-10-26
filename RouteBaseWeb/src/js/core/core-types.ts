/**
 * Core domain types — the shared DTO/view shapes and map/chart infrastructure
 * contracts depended on across every slice.
 *
 * Formerly ambient globals (GodFileSplit). Now explicit ESM
 * exports; consumers import what they use. Core is the lowest layer — it never
 * imports from a feature slice (overview / segment-stats / activity / recording).
 */

// ---------------------------------------------------------------------------
// Chart.js type aliases — CDN-loaded, no runtime import
// ChartJsInstance extends the base Chart type with chartjs-plugin-zoom methods.
// ---------------------------------------------------------------------------

/** Chart.js instance type. zoom methods (resetZoom/zoomScale) are available via module augmentation in globals.d.ts. */
export type ChartJsInstance = InstanceType<typeof import("chart.js").Chart>;

// Generated API contracts re-surfaced under their established core names (type-only import — the
// generated folder is not a feature slice).
import type { EffortsSummaryDto, GateLineDto, GpsTrackWireDto, SegmentOutlineDto, ShapeRectDto } from '../generated/contracts';

/** Event accepted by chart hover handlers: either a Chart.js synthetic event or a native MouseEvent. */
export type ChartEventLike = import("chart.js").ChartEvent | MouseEvent;

/**
 * Options for setupProximityHover on map adapters.
 */
export interface ProximityHoverOptions {
	onHighlight: (index: number, source: string) => void;
	onHideHighlight: () => void;
}

/**
 * Duck-typed map adapter interface implemented by the engine adapters (MapLibreAdapter).
 */
export interface IMapAdapter {
	engineName: string;
	destroy(): void;
	resize?(): void;
	showHighlightMarker(lat: number, lng: number): void;
	hideHighlightMarker(): void;
	destroyHighlightMarker(): void;
	showSplitHighlight(startIndex: number, endIndex: number, data: GpsPoint[]): void;
	clearSplitHighlight(): void;
	setupProximityHover(opts: ProximityHoverOptions): void;
	updateTrackColors(colorMode: string, gpsArray: GpsPoint[]): void;
}

/**
 * GPS point as returned by the API and stored in window.gpsData.
 * Matches the GpsData model shape used throughout the JS codebase.
 */
export interface GpsPoint {
	gpsId?: number | undefined;
	trackId?: number | undefined;
	lat: number;
	lng: number;
	altitude: number;
	speed: number;
	distance: number;
	time?: string;
	heartRate?: number;
	cadence?: number;
	power?: number;
	gradeSmooth?: number;

	/** Alternate coordinate spellings some payloads carry; normalized by centerFromGpsData. */
	latitude?: number;
	longitude?: number;

	/** Elapsed seconds from track start. Derived from the wire's epoch column by `expandGpsTrack`, and
	 *  computed in-place by replay-engine-base for tracks that arrive by other routes. Absent when the
	 *  track has no timestamps at all — see `trackSeconds`. */
	_cumulativeTime?: number | undefined;

	[key: string]: unknown;
}

/**
 * Slim track point of the segment-stats track endpoints, which send lat/lng/gpsId ONLY:
 * signed-in `activities/{id}/full-track` + `activities/bulk-tracks` serve C# TrackPointDto
 * (generated; fetch sites cast to it), the share route's `tracks` serves C# GpsCoordinate — so this
 * stays a hand-written COMMON VIEW of two wires (TODO(openapi-review): D12; gpsId optional because
 * the share wire lacks it);
 * both generated wire types are structurally assignable into it.
 * These wires were formerly typed as {@link GpsPoint}, which lied:
 * altitude/speed/distance never arrive here. GpsPoint is structurally assignable to TrackPoint,
 * so full-data tracks still flow into TrackPoint consumers; the reverse is a compile error.
 */
export interface TrackPoint {
	lat: number;
	lng: number;
	gpsId?: number;
}

/**
 * GPS track as served by the Details page config island — parallel arrays (one entry per point)
 * instead of one JSON object per point; cuts wire bytes by not repeating 9 keys per row.
 * Generated from C# GpsTrackWireDto (island schema, D27). expandGpsTrack() (core/gps-data.ts)
 * reconstructs GpsPoint[]; its optional channels (gpsId/heartRate/cadence, `T[] | null` on the wire)
 * are read via `?.`, so an omitted-or-null column is a no-op there.
 */
export type GpsTrackWire = GpsTrackWireDto;

/**
 * One point of GET /api/activities/{id}/gps (legacy GpsData casing: gpsLat/gpsLong). Generated — the
 * ETagged endpoint now declares its schema via [ProducesResponseType] metadata (D14 closed, Phase 2).
 */
export type { ActivityGpsPointDto } from '../generated/contracts';

/**
 * Account settings live in core/account-settings.ts, typed by ALIASING the generated SettingsPayload.
 *
 * The four interfaces that used to be declared here (MapProfileSettings / ChartsProfileSettings /
 * UiProfileSettings / ProfileSettings) were a hand-written mirror of a generated contract — the exact thing
 * the conventions forbid. They existed because the payload's sections were optional and a second writer emitted
 * a superset, so `Partial<>` + `| null` + an `[key: string]: unknown` index signature papered over the
 * difference. That index signature is why `settings.chatrs` compiled: a typo was a valid read of `unknown`.
 * The payload is complete and non-nullable now, so the alias is the whole type.
 */

// ---------------------------------------------------------------------------
// MapShell — single-engine MapLibre map lifecycle orchestrator (core/map-shell.ts)
// ---------------------------------------------------------------------------

/**
 * Camera state saved before an engine switch so it can be restored after.
 * center is [lng, lat] (MapLibre convention).
 */
export interface SavedCamera {
    center: [number, number];
    zoom: number;
    pitch: number;
    bearing: number;
}

/**
 * Configuration for createMapShell().
 * Pages provide rendering callbacks; MapShell calls them at the right moment.
 */
export interface MapShellConfig {
    /** ID of the map container div. */
    containerId: string;

    /**
     * Registry-driven style switcher (ID-07). MapShell renders one button per
     * availableBasemaps() into an empty container div — adding a basemap becomes
     * one registry line.
     */
    styleSwitcher?: {
        /** ID of an empty div; shell appends the basemap buttons. */
        containerId: string;
        /** Button class; defaults to 'btn btn-sm btn-outline-secondary'. */
        btnClass?: string | undefined;
    } | undefined;

    /** Size control button IDs and defaults (optional — omit to skip wiring). */
    sizeControls?: {
        /** Override the element to resize; defaults to the map containerId. */
        mapDivId?: string | undefined;
        growBtnId?: string | undefined;
        shrinkBtnId?: string | undefined;
        resetBtnId?: string | undefined;
        fullscreenBtnId?: string | undefined;
        fullscreenCardId?: string | undefined;
        defaultHeight?: number | undefined;
        step?: number | undefined;
        maxHeight?: number | undefined;
    } | undefined;

    /**
     * If true, skip auto-init on construction. Caller must call
     * shell.switchToBasemap() or shell.switchToMapLibre() manually.
     * Needed when the page may restore a specific basemap on reload.
     */
    deferInit?: boolean | undefined;

    /**
     * PAGE creates the MapLibre map and returns it (or null on failure).
     * Called during switchToMapLibre after the container is cleared.
     * The page calls initMapLibreTerrain() or new maplibregl.Map() here.
     */
    createMapLibre: (containerId: string, styleUrl: string, savedCam: SavedCamera | null, basemapId?: string | null)
        => maplibregl.Map | null;

    /** Called once the MapLibre map is idle. Restore page-specific layers/state here. */
    onMapLibreReady: (map: maplibregl.Map, styleUrl: string, savedCam: SavedCamera | null) => void;

    /**
     * PAGE switches the style in-place on an already-active MapLibre map.
     * Called instead of createMapLibre when engine is already maplibre.
     * basemapId travels alongside the URL so pages can route DEM by id (BT-05).
     */
    switchMapLibreStyle?: ((map: maplibregl.Map, styleUrl: string, basemapId?: string | null) => void) | undefined;

    /**
     * PAGE tears down MapLibre. Called during bfcache / destroy.
     * Example: mlModule.destroy() or window.destroyMapLibre().
     */
    destroyMapLibre?: ((map: maplibregl.Map) => void) | undefined;

    /** Called at the start of a bfcache restore, before MapShell reinitialises the map. */
    onBfcacheRestore?: (() => void) | undefined;

    /** Set to false to skip the pageshow bfcache handler. Defaults to true. */
    handleBfcache?: boolean | undefined;
}

/**
 * Handle returned by createMapShell().
 */
export interface MapShellInstance {
    getActiveEngine(): 'maplibre' | null;
    getMapLibreMap(): maplibregl.Map | null;
    getStyleUrl(): string | null;
    captureCamera(): SavedCamera | null;
    switchToMapLibre(styleUrl: string): void;
    /** Switch by registry basemap id: resolve its style URL and (re)load MapLibre. */
    switchToBasemap(id: string): void;
    /** Mark a registry switcher button active by basemap id. */
    setActiveBasemap(id: string): void;
    /** Trigger a map resize (debounced 100 ms). */
    invalidate(): void;
    /** Tear down the active engine and all event listeners. */
    destroy(): void;
}

// ---------------------------------------------------------------------------
// Replay follow-camera contracts (moved down from maplibre/ so core/ owns them —
// pure duck-typed interfaces over ambient maplibregl types; no runtime, no maplibre symbol).
// ---------------------------------------------------------------------------

/** The camera a follow engaged with — where reset() (the R key) puts it back. */
export interface ResetFraming {
	zoom: number;
	bearing: number;
	pitch: number;
}

export interface CameraControlsInstance {
	isActive: boolean;

	activate(): void;
	deactivate(): void;
	startIdleLoop(getCameraCenter: () => maplibregl.LngLatLike): void;
	stopIdleLoop(): void;
	/** Per-frame centre writer; the follow seam installs a gated, change-detected one (LV-05). */
	setFollowCenterWriter(write: ((center: maplibregl.LngLatLike) => void) | null): void;
	/** The engage-time framing reset() returns to; null clears it. */
	setResetFraming(framing: ResetFraming | null): void;
	reset(): void;
}

// ---------------------------------------------------------------------------
// Shape rectangle — geometry primitive for shape-defined segments. Lives in core
// because core/track-thumbnail renders it (core must not depend on segment-stats).
// ---------------------------------------------------------------------------

/** An axis-aligned painted rectangle belonging to a shape-defined segment.
 *  Generated from C# ShapeRectDto (island schema, D27). `role` ("mandatory" — track must touch it — or
 *  "any", OR'd within its group) is required on the wire; consumers read `(role || '')`, comparing not
 *  narrowing, so the required string is a superset of the old optional one. */
export type ShapeRect = ShapeRectDto;

/** A directed gate line of a shape-defined segment — generated from C# GateLineDto. */
export type GateLine = GateLineDto;

/** The drawable outline of a shape-defined segment: zone rectangles + gate lines.
 *  Generated from C# SegmentOutlineDto (GET /api/SegmentsApi/{id}/shapes). */
export type SegmentOutline = SegmentOutlineDto;

/** Every visible matched effort's simplified track through a shape/gate segment, for the minimap.
 *  `truncated` = the effort set was capped server-side. Generated from C# EffortsSummaryDto. */
export type EffortsSummary = EffortsSummaryDto;

// ---------------------------------------------------------------------------
// Quality tier — used by progressive quality engines (overview + segment-stats)
// ---------------------------------------------------------------------------

export interface QualityTier {
	zoom: number;
	maxPoints: number;
	label: string;
}
