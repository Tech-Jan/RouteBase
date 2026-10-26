/**
 * map-utils-maplibre.ts — MapLibreAdapter for mapDispatcher
 *
 * Implements the IMapAdapter duck-typed interface for MapLibre GL JS maps.
 * Exports the MapLibreAdapter class; maplibre-terrain.ts imports it and
 * registers an instance with the mapDispatcher after map init.
 */
import type { ProximityHoverOptions, IMapAdapter, GpsPoint } from './core-types';
import { findNearestPointIndex } from './nearest-point';
import { getGpsData } from './gps-data';
import { must } from './invariant';
import { mapDispatcher } from './map-dispatcher';
import { MAP_THROTTLE_MS } from './constants';
import { getLockedSplit } from './split-state';
import { isReplayActive, isBlueMarkerVisible, setCurrentMarker } from './map-state';

	/**
	 * CM-06: seams for page-owned state the core adapter used to read straight off `window`.
	 * All optional — when absent the adapter falls back to the legacy window globals, so it
	 * behaves bit-identically until maplibre-terrain.ts (Frontend-ActivityPagesMaps) passes them.
	 */
	interface MapLibreAdapterOptions {
		onUpdateTrackColors?: (mode: string, data: GpsPoint[]) => void;
		isBlueMarkerVisible?: () => boolean;
		isReplayActive?: () => boolean;
		getLockedSplit?: () => unknown;
	}

	/**
	 * Adapter for MapLibre GL JS maps.
	 * Created and registered by maplibre-terrain.ts after map init.
	 */
	export class MapLibreAdapter implements IMapAdapter {
		engineName: string;
		map: maplibregl.Map;
		highlightMarker: maplibregl.Marker | null;
		splitStartMarker: maplibregl.Marker | null;
		splitEndMarker: maplibregl.Marker | null;

		// CM-03: guards against unbounded rebuild recursion when marker creation keeps throwing.
		private _rebuildingMarker = false;
		// CM-03: proximity-hover listener refs so destroy() can detach them (no leak on re-register).
		private _hoverMove: ((e: maplibregl.MapMouseEvent) => void) | null = null;
		private _hoverLeave: (() => void) | null = null;
		private _hoverCanvas: HTMLElement | null = null;
		// CM-06: optional injected seams; default behavior reads window globals (see options doc).
		private _opts: MapLibreAdapterOptions;

		constructor(map: maplibregl.Map, opts?: MapLibreAdapterOptions) {
			this.engineName = "maplibre";
			this.map = map;
			this.highlightMarker = null; // Created lazily
			this.splitStartMarker = null;
			this.splitEndMarker = null;
			this._opts = opts || {};
		}

		showHighlightMarker(lat: number, lng: number): void {
			if (!this.map) return;
			try {
				if (!this.highlightMarker) {
					const el = document.createElement("div");
					el.className = "current-marker maplibre-current-marker";
					// MS-09: honor the blue-marker visibility even on first creation — a chart
					// hover during replay (which hid it) must not spawn a visible marker.
					const visible = this._opts.isBlueMarkerVisible
						? this._opts.isBlueMarkerVisible()
						: isBlueMarkerVisible();
					Object.assign(el.style, {
						backgroundColor: "#007bff",
						width: "16px",
						height: "16px",
						borderRadius: "50%",
						border: "3px solid white",
						boxShadow: "0 0 4px rgba(0,0,0,0.5)",
						display: visible ? "block" : "none",
						transform: "translate(-50%, -50%)",
						// The marker sits ON the hovered point, above the canvas. Interactive it would take the
						// pointer off the canvas → mouseleave → hide → pointer back on canvas → show: flicker.
						pointerEvents: "none",
					});
					this.highlightMarker = new maplibregl.Marker({ element: el })
						.setLngLat([lng, lat])
						.addTo(this.map);
					// Publish for replay's blue-marker visibility toggle
					setCurrentMarker(this.highlightMarker);
				} else {
						const cur = this.highlightMarker.getLngLat();
						if (cur.lng !== lng || cur.lat !== lat) {
							this.highlightMarker.setLngLat([lng, lat]);
						}
						const el2 = this.highlightMarker.getElement();
					if (el2) {
						const visible = this._opts.isBlueMarkerVisible
							? this._opts.isBlueMarkerVisible()
							: isBlueMarkerVisible();
						el2.style.display = visible ? "block" : "none";
					}
				}
			} catch (e) {
				// Marker became stale (map was destroyed/recreated) — rebuild once.
				// CM-03: give up after one rebuild attempt; a disposed map throws on every
				// create, so unguarded recursion would blow the stack in a hot mousemove path.
				if (this._rebuildingMarker) return;
				console.warn("MapLibre marker update failed, recreating", e);
				this._rebuildingMarker = true;
				try {
					try {
						this.highlightMarker?.remove();
					} catch (_e) { /* teardown: stale marker cleanup */ }
					this.highlightMarker = null;
					this.showHighlightMarker(lat, lng);
				} finally {
					this._rebuildingMarker = false;
				}
			}
		}

		hideHighlightMarker(): void {
			if (!this.highlightMarker) return;
			try {
				const el = this.highlightMarker.getElement();
				if (el) el.style.display = "none";
			} catch (_e) { /* teardown: may already be removed */ }
		}

		destroyHighlightMarker(): void {
			if (this.highlightMarker) {
				try {
					this.highlightMarker?.remove();
				} catch (_e) { /* teardown: stale marker cleanup */ }
			}
			this.highlightMarker = null;
			setCurrentMarker(null);
		}

		showSplitHighlight(startIndex: number, endIndex: number, data: GpsPoint[]): void {
			try {
				// must(): a caller-supplied index off the track used to TypeError into this same
				// try/catch — the named throw lands in the identical catch, now diagnosable.
				const coords: [number, number][] = [];
				for (let i = startIndex; i <= endIndex; i++) {
					const p = must(data[i], 'startIndex..endIndex within data');
					coords.push([p.lng, p.lat]);
				}

				const geojsonData = {
					type: "Feature" as const,
					properties: {},
					geometry: { type: "LineString" as const, coordinates: coords },
				};

				// Reuse existing source/layer — just update the GeoJSON data.
				const existingSource = this.map.getSource("split-track") as maplibregl.GeoJSONSource | undefined;
				if (existingSource) {
					existingSource.setData(geojsonData);
				} else {
					this.map.addSource("split-track", {
						type: "geojson",
						data: geojsonData,
					});
					this.map.addLayer({
						id: "split-line",
						type: "line",
						source: "split-track",
						layout: { "line-join": "round", "line-cap": "round" },
						paint: {
							"line-color": "#FFD700",
							"line-width": 6,
							"line-opacity": 0.8,
						},
					});
				}

				const mkEl = (color: string): HTMLElement => {
					const el = document.createElement("div");
					Object.assign(el.style, {
						backgroundColor: color,
						width: "18px",
						height: "18px",
						borderRadius: "50%",
						border: "3px solid white",
						boxShadow: "0 0 10px rgba(0,0,0,0.7)",
						pointerEvents: "none", // passive endpoints — same canvas-hover theft as the highlight marker
					});
					return el;
				};

				// Reuse existing markers — just move them.
				const start = must(data[startIndex], 'startIndex within data');
				const end = must(data[endIndex], 'endIndex within data');
				if (this.splitStartMarker) {
					this.splitStartMarker.setLngLat([start.lng, start.lat]);
				} else {
					this.splitStartMarker = new maplibregl.Marker({
						element: mkEl("#28a745"),
					})
						.setLngLat([start.lng, start.lat])
						.addTo(this.map);
				}
				if (this.splitEndMarker) {
					this.splitEndMarker.setLngLat([end.lng, end.lat]);
				} else {
					this.splitEndMarker = new maplibregl.Marker({
						element: mkEl("#dc3545"),
					})
						.setLngLat([end.lng, end.lat])
						.addTo(this.map);
				}
			} catch (e) {
				console.error("MapLibre split highlight error:", e);
			}
		}

		clearSplitHighlight(): void {
			try {
				if (this.splitStartMarker) {
					this.splitStartMarker.remove();
				}
				if (this.splitEndMarker) {
					this.splitEndMarker.remove();
				}
			} catch (_e) { /* teardown: may already be removed */ }
			this.splitStartMarker = null;
			this.splitEndMarker = null;

			try {
				if (this.map.getLayer?.("split-line")) this.map.removeLayer("split-line");
				if (this.map.getSource?.("split-track")) this.map.removeSource("split-track");
			} catch (_e) { /* teardown: may already be removed */ }
		}

		setupProximityHover(opts: ProximityHoverOptions): void {
			const onHighlight = opts?.onHighlight;
			const onHide = opts?.onHideHighlight;
			if (!onHighlight || !onHide) return;

			if (getGpsData().length === 0) return;

			let _lastIdx = -1;
			let _lastMoveTime = 0;
			const _THROTTLE_MS = MAP_THROTTLE_MS;

			const onMove = (e: maplibregl.MapMouseEvent): void => {
				const replaying = this._opts.isReplayActive
					? this._opts.isReplayActive()
					: isReplayActive();
				if (replaying) return;

				// Throttle: skip if called too recently
				const now = performance.now();
				if (now - _lastMoveTime < _THROTTLE_MS) return;
				_lastMoveTime = now;

				// Read live: edit mode's renderPreview swaps in a fresh array on every op/undo/slot switch,
				// and a snapshot taken at setup would keep indexing the pre-edit track.
				const idx = findNearestPointIndex(e.lngLat.lat, e.lngLat.lng, getGpsData());
				if (idx !== -1 && idx !== _lastIdx) {
					_lastIdx = idx;
					onHighlight(idx, "3dmap");
				}
			};
			const onLeave = (): void => {
				const locked = this._opts.getLockedSplit
					? this._opts.getLockedSplit()
					: getLockedSplit();
				if (!locked) {
					_lastIdx = -1;
					onHide();
				}
			};
			const canvas = this.map.getCanvas();
			// CM-03: keep refs so destroy() can detach these listeners.
			this._hoverMove = onMove;
			this._hoverLeave = onLeave;
			this._hoverCanvas = canvas;
			this.map.on("mousemove", onMove);
			canvas.addEventListener("mouseleave", onLeave);
		}

		updateTrackColors(colorMode: string, data: GpsPoint[]): void {
			// Track recoloring lives in the maplibre bundle; maplibre-terrain injects it as
			// onUpdateTrackColors (core can't import maplibre-track without a cycle).
			this._opts.onUpdateTrackColors?.(colorMode, data);
		}

		resize(): void {
			try {
				this.map?.resize();
			} catch (_e) { /* defensive: map may be disposed */ }
		}

		destroy(): void {
			// CM-03: detach proximity-hover listeners so a stale closure can't keep firing
			// after this adapter is replaced on the same live map (in-place style switch).
			if (this._hoverMove) {
				this.map.off("mousemove", this._hoverMove);
				this._hoverMove = null;
			}
			if (this._hoverCanvas && this._hoverLeave) {
				this._hoverCanvas.removeEventListener("mouseleave", this._hoverLeave);
			}
			this._hoverLeave = null;
			this._hoverCanvas = null;
			this.clearSplitHighlight();
			this.destroyHighlightMarker();
			mapDispatcher.unregister();
		}
	}
