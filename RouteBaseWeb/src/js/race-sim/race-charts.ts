/**
 * The chart strip under the map — the elevation profile, with the field-speed aggregate traced over it —
 * and the hover that ties it to the map.
 *
 * The field speed used to be a SECOND canvas stacked underneath, sharing this file's x-axis by
 * construction; it is a trace on the profile now (`core/profile-chart`'s ProfileTrace), so the two can no
 * longer disagree about a position and there is no cross-chart hover to keep in step. What is left here
 * is the map link, which is the part that was never mechanical: hovering the chart drops a marker on the
 * course; hovering the MAP snaps to the nearest course node and drives the chart. That link is deliberately
 * chart-authoritative: this course passes the same place up to seven times, so a map point is an ambiguous
 * chainage (that is why checkpoints are placed on the profile, not the map). Map→chart is therefore
 * best-effort — the nearest node, disambiguated toward the pass the playhead is on.
 */

import { createRaceProfile, type RaceProfileField } from './race-profile';
import { createTickThrottle } from '../core/tick-throttle';
import type { FieldSpeedProfile } from './race-field-speed';
import type { RaceCheckpointDto, RaceCourseDto } from './race-api';

export interface RaceChartsOptions {
	readonly profileCanvas: HTMLCanvasElement;
	readonly course: RaceCourseDto;
	readonly editable: boolean;
	/** The live map — re-created on a style switch, so it is read fresh, never captured. */
	readonly map: () => maplibregl.Map | null;
	readonly onPlace?: ((chainageMeters: number) => void) | undefined;
	readonly onMove?: ((checkpointId: number, chainageMeters: number) => void) | undefined;
	readonly onSelect?: ((checkpointId: number) => void) | undefined;
}

export interface RaceCharts {
	setCheckpoints(checkpoints: readonly RaceCheckpointDto[]): void;
	setField(field: RaceProfileField | null): void;
	setColors(colors: ReadonlyMap<number, string>): void;
	setFilter(filter: { bright?: ReadonlySet<number> | null; visible?: ReadonlySet<number> | null }): void;
	/** Which riders are emphasised — bigger dots, drawn on top. Mirrors RaceMarkers.setEmphasis. */
	setEmphasis(indices: ReadonlySet<number> | null): void;
	setSelected(checkpointId: number | null): void;
	/** Arm/disarm checkpoint placement on the elevation profile (the Sections editor's Add button). */
	setPlaceable(armed: boolean): void;
	setSpeedProfile(profile: FieldSpeedProfile | null): void;
	/**
	 * The WATCHED rider's chainage — drives the playhead. The leader unless the viewer is following
	 * someone, in which case it is the followed rider (which is also the pass the map hover disambiguates
	 * toward — see nearestChainage).
	 */
	setPlayhead(chainageMeters: number | null): void;
	/** The leader's golden rule — set only while the playhead is a followed rider. */
	setLeadLine(chainageMeters: number | null): void;
	/** Redraw the moving half. `force` bypasses the 30 Hz cap (a seek must not lag). */
	tick(force?: boolean): void;
	/**
	 * Hover the course from OUTSIDE the strip, in metres — drops the dot on the map and draws the strip's
	 * hover line. Null clears.
	 *
	 * The speed chart under the map is what calls this. The strip owns the map link because a chainage is
	 * what the map needs and the strip's axis has always been chainage — but the strip is fullscreen-only
	 * now, so without this door the inline view lost the dot entirely. The map-marker half of syncHover
	 * does not touch the strip's canvas, so it works perfectly well while the strip is display:none.
	 */
	hoverAt(chainageMeters: number | null): void;
	/** (Re)bind the map hover listener — call after the map is created and after every style switch. */
	attachMap(): void;
	/** Hide the hover dot ON THE MAP (e.g. while following a rider); the chart hover lines are unaffected. */
	setMapMarkerSuppressed(suppressed: boolean): void;
	destroy(): void;
}

function makeHoverDot(): HTMLDivElement {
	const el = document.createElement('div');
	el.style.cssText =
		'width:12px;height:12px;border-radius:50%;background:#22b8cf;' +
		'box-shadow:0 0 0 2px #fff,0 0 6px rgba(34,184,207,0.7);pointer-events:none;';
	return el;
}

export function createRaceCharts(options: RaceChartsOptions): RaceCharts {
	const { course } = options;

	const profile = createRaceProfile({
		canvas: options.profileCanvas,
		course,
		editable: options.editable,
		onPlace: options.onPlace,
		onMove: options.onMove,
		onSelect: options.onSelect,
		onHover: (m) => syncHover(m),
	});

	let playhead: number | null = null;
	let hoverMarker: maplibregl.Marker | null = null;
	let markerMap: maplibregl.Map | null = null;
	let boundMap: maplibregl.Map | null = null;
	// While following a rider the map dot is unwanted clutter over the locked-on camera; the chart
	// hover lines still fire (map→chart stays live) — only the on-map dot is withheld.
	let mapMarkerSuppressed = false;
	const mapHoverThrottle = createTickThrottle(1000 / 30);

	/** Draw the hover line on the chart and drop the course marker. Idempotent for a chart-fired hover. */
	function syncHover(metres: number | null): void {
		profile.setHover(metres);
		updateMapMarker(metres);
	}

	function updateMapMarker(metres: number | null): void {
		const map = options.map();
		if (metres == null || mapMarkerSuppressed || !map) {
			// Detach but keep the instance; markerMap = null re-attaches it on the next hover.
			hoverMarker?.remove();
			markerMap = null;
			return;
		}
		const i = Math.min(course.lat.length - 1, Math.max(0, Math.round(metres / course.stepMeters)));
		if (!hoverMarker) hoverMarker = new maplibregl.Marker({ element: makeHoverDot() });
		// setLngLat BEFORE addTo: adding a marker that has no position throws in Marker._update (reads
		// _lngLat.lng), and that throw inside the mousemove→render path is what wedged the map's run loop.
		hoverMarker.setLngLat([course.lng[i] ?? NaN, course.lat[i] ?? NaN]);
		if (markerMap !== map) {
			hoverMarker.addTo(map);
			markerMap = map;
		}
	}

	/**
	 * The chainage nearest a map point. Multi-pass courses make this ambiguous, so among the nodes close to
	 * the nearest, pick the one whose chainage is nearest the playhead — the pass the viewer is watching.
	 */
	function nearestChainage(lng: number, lat: number): number | null {
		const { lat: lats, lng: lngs, stepMeters } = course;
		const n = lats.length;
		if (n === 0) return null;

		let minD2 = Infinity;
		for (let i = 0; i < n; i++) {
			const dx = (lngs[i] ?? NaN) - lng;
			const dy = (lats[i] ?? NaN) - lat;
			const d2 = dx * dx + dy * dy;
			if (d2 < minD2) minD2 = d2;
		}

		const threshold = minD2 * 2.5 + 1e-12;
		let bestChainage = -1;
		let bestScore = Infinity;
		for (let i = 0; i < n; i++) {
			const dx = (lngs[i] ?? NaN) - lng;
			const dy = (lats[i] ?? NaN) - lat;
			const d2 = dx * dx + dy * dy;
			if (d2 > threshold) continue;
			const chainage = i * stepMeters;
			const score = playhead != null ? Math.abs(chainage - playhead) : d2;
			if (score < bestScore) {
				bestScore = score;
				bestChainage = chainage;
			}
		}
		return bestChainage >= 0 ? bestChainage : null;
	}

	const onMapMove = (e: maplibregl.MapMouseEvent): void => {
		if (!mapHoverThrottle.shouldRun(false)) return;
		const chainage = nearestChainage(e.lngLat.lng, e.lngLat.lat);
		if (chainage != null) syncHover(chainage);
	};
	const onMapOut = (): void => syncHover(null);

	function attachMap(): void {
		const map = options.map();
		if (!map || map === boundMap) return;
		boundMap = map;
		map.on('mousemove', onMapMove);
		map.on('mouseout', onMapOut);
	}

	return {
		setCheckpoints: (checkpoints) => profile.setCheckpoints(checkpoints),
		setField: (field) => profile.setField(field),
		setColors: (colors) => profile.setColors(colors),
		setFilter: (filter) => profile.setFilter(filter),
		setEmphasis: (indices) => profile.setEmphasis(indices),
		setSelected: (checkpointId) => profile.setSelected(checkpointId),
		setPlaceable: (armed) => profile.setPlaceable(armed),
		setSpeedProfile: (p) => profile.setSpeedProfile(p),
		setPlayhead(metres) {
			playhead = metres;
			profile.setPlayhead(metres);
		},
		setLeadLine: (metres) => profile.setLeadLine(metres),
		hoverAt: (metres) => syncHover(metres),
		tick: (force = false) => profile.tick(force),
		attachMap,
		setMapMarkerSuppressed(suppressed) {
			mapMarkerSuppressed = suppressed;
			// Drop any dot already on the map the instant we start following — don't wait for the next hover.
			if (suppressed) updateMapMarker(null);
		},
		destroy() {
			if (boundMap) {
				boundMap.off('mousemove', onMapMove);
				boundMap.off('mouseout', onMapOut);
				boundMap = null;
			}
			hoverMarker?.remove();
			hoverMarker = null;
			profile.destroy();
		},
	};
}
