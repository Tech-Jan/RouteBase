/**
 * Ambient declarations for CDN-loaded third-party libraries.
 *
 * These libraries are loaded as <script> tags from CDN (or wwwroot/lib/), NOT
 * bundled by Vite. They inject globals onto window. We declare them here so
 * TypeScript knows their types without any runtime import.
 *
 * maplibre-gl and chart.js ship their own .d.ts files.
 *
 * The `typeof import(...)` pattern gives us the full type from the package's
 * type declarations without emitting any runtime import statement.
 */

// ---------------------------------------------------------------------------
// MapLibre GL JS — loaded as window.maplibregl via <script> tag (unpkg CDN).
//
// maplibre-gl is installed as a devDependency for its type definitions only.
// Keep the devDep version in sync with the CDN version in Constants/CdnLibraries.cs.
// The runtime library is never bundled — Vite marks it as external.
//
// The `typeof import(...)` pattern gives us the full constructor namespace
// (maplibregl.Map, maplibregl.Marker, etc.) without emitting any runtime code.
// The companion `declare namespace` re-exports key types so they can be used
// as type annotations (e.g., `map: maplibregl.Map`) in ambient IIFE code.
// ---------------------------------------------------------------------------
declare const maplibregl: typeof import("maplibre-gl");

declare namespace maplibregl {
	type Map = import("maplibre-gl").Map;
	type Marker = import("maplibre-gl").Marker;
	type Popup = import("maplibre-gl").Popup;
	type NavigationControl = import("maplibre-gl").NavigationControl;
	type TerrainControl = import("maplibre-gl").TerrainControl;
	type MapMouseEvent = import("maplibre-gl").MapMouseEvent;
	type MapGeoJSONFeature = import("maplibre-gl").MapGeoJSONFeature;
	type GeoJSONSource = import("maplibre-gl").GeoJSONSource;
	type LngLatLike = import("maplibre-gl").LngLatLike;
	// The concrete class, not just the -Like union: the follow camera's transformCameraUpdate hook has
	// to hand MapLibre a real LngLat instance (core/replay-follow-camera.ts).
	type LngLat = import("maplibre-gl").LngLat;
	type LngLatBoundsLike = import("maplibre-gl").LngLatBoundsLike;
	type MapOptions = import("maplibre-gl").MapOptions;
	type IControl = import("maplibre-gl").IControl;
	type AddLayerObject = import("maplibre-gl").AddLayerObject;
	// Narrowable per layer type (Extract<…, {type:"symbol"}>), unlike AddLayerObject — which unions in
	// CustomLayerInterface and so has no `layout`/`filter` in common.
	type LayerSpecification = import("maplibre-gl").LayerSpecification;
	type MapLayerMouseEvent = import("maplibre-gl").MapLayerMouseEvent;
	type MapLayerTouchEvent = import("maplibre-gl").MapLayerTouchEvent;
	type StyleSpecification = import("@maplibre/maplibre-gl-style-spec").StyleSpecification;
	type Listener = import("maplibre-gl").Listener;
	type MapLayerEventType = import("maplibre-gl").MapLayerEventType;
}

// ---------------------------------------------------------------------------
// Chart.js — ships its own types (chart.js package)
// Loaded as window.Chart via <script> tag (jsdelivr CDN)
// ChartZoom: chartjs-plugin-zoom (no official @types package; registered as side-effect)
// ---------------------------------------------------------------------------
declare const Chart: typeof import("chart.js").Chart & {
	register: (...items: any[]) => void;
};

// DC-13: the Bootstrap ambient namespace had zero TS consumers (Bootstrap 5 is
// used only via data-attributes / CSS) and the Replay* ambient interfaces
// duplicated createMarkerManager's inline return type — both removed. The marker
// manager's public shape now lives in core/replay-engine-base.ts (ReplayMarkerManager).

// ---------------------------------------------------------------------------
// @microsoft/signalR — loaded as window.signalR via CDN script tag.
// Minimal ambient declarations covering the subset used by live-recording.ts.
// ---------------------------------------------------------------------------

declare namespace signalR {
	interface HubConnection {
		start(): Promise<void>;
		stop(): Promise<void>;
		invoke(methodName: string, ...args: unknown[]): Promise<unknown>;
		invoke<T>(methodName: string, ...args: unknown[]): Promise<T>;
		on(methodName: string, newMethod: (...args: any[]) => void): void;
		onreconnected(callback: (connectionId?: string) => void): void;
		onclose(callback: (error?: Error) => void): void;
	}

	class HubConnectionBuilder {
		withUrl(url: string): HubConnectionBuilder;
		withAutomaticReconnect(): HubConnectionBuilder;
		build(): HubConnection;
	}
}
