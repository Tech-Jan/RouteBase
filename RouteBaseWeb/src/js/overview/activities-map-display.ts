/**
 * Overview-map display controller — how activities filtered out by the table appear on the map,
 * plus the 3-mode toolbar toggle driving it. Split out of activities-grid.ts (file-size cap);
 * behaviour unchanged.
 */

import { readData } from '../core/data-attrs';
import { devicePrefKeys, getEnum, setEnum } from '../core/device-prefs';
import type { GridDataResponse } from '../core/grid/grid-types';
import type { ActivityGridResponse as ActivityGridResponseContract } from '../generated/contracts';
import { setActivitiesFilter, setOverviewHighlight } from './activities-overview';

// How activities filtered out by the table appear on the map. Default: highlight.
//   all       — draw every track at full opacity (table filter ignored on the map)
//   highlight — draw every track; matching stay bright, filtered-out fade back
//   hide      — draw only the matching tracks (filtered-out disappear)
export type MapDisplayMode = 'all' | 'highlight' | 'hide';
const MAP_DISPLAY_MODES: readonly MapDisplayMode[] = ['all', 'highlight', 'hide'];

/**
 * A DEVICE preference: it is how this screen draws, not what the user is looking at (that is the grid
 * filter, and it stays in the URL). 'highlight' remains the fallback for a browser that has never
 * chosen — the map keeps every owner-scoped track drawn and dims the filtered-out ones.
 */
const MAP_DISPLAY_KEY = devicePrefKeys.mapDisplayMode('activities');

export function storedMapDisplayMode(): MapDisplayMode {
	return getEnum(MAP_DISPLAY_KEY, MAP_DISPLAY_MODES, 'highlight');
}

/** The grid response through the generic grid-pipeline lens (GridDataResponse), plus the
 *  activities-specific `ownerIds` (owner-scoped map universe; see controller) tied to the generated C#
 *  ActivityGridResponse. `ownerIds` stays optional — a stale cached response predating the field omits it. */
type ActivityGridResponse = GridDataResponse<unknown> & Partial<Pick<ActivityGridResponseContract, 'ownerIds'>>;

export interface MapDisplayController {
	/** `hasColumnFilters` = any non-owner filter is active (owner defines the universe, not dimming). */
	onGridData(res: GridDataResponse<unknown>, hasColumnFilters: boolean): void;
	setMode(mode: MapDisplayMode): void;
}

// Wires the 3-mode segmented control in the map toolbar (data-mode = all|highlight|hide) to the
// display controller, keeping the .active class in sync. No-op if the markup isn't on the page.
export function setupMapDisplayModeToggle(controller: MapDisplayController): void {
	const group = document.getElementById('actMapDisplayMode');
	if (!group) return;
	const btns = Array.from(group.querySelectorAll<HTMLButtonElement>('.act-map-mode-btn'));

	// The markup hard-codes `active` on Highlight, so a restored 'hide' would draw a hidden-filtered map
	// under a control claiming Highlight. The controller already opened in the stored mode; this makes
	// the button agree with it.
	const restored = storedMapDisplayMode();
	btns.forEach((b) => b.classList.toggle('active', readData(b, 'mode') === restored));

	group.addEventListener('click', (e) => {
		const btn = (e.target as HTMLElement).closest<HTMLButtonElement>('.act-map-mode-btn');
		if (!btn) return;
		btns.forEach((b) => b.classList.toggle('active', b === btn));
		controller.setMode(readData(btn, 'mode') as MapDisplayMode);
	});
}

/**
 * Drives the map from live grid filtering.
 *
 * The map's "universe" is the owner-scoped set of tracks — server-declared as `ownerIds` on every grid
 * response (and in the bootstrap), so an owner change always redraws the map to the new scope; nothing
 * is guessed from filtered results. The column filters (date/name/distance/…) then pick the bright
 * subset within it: `highlight` dims to `filteredIds` exactly when any column filter is active.
 *
 * In `hide` mode the map is redrawn to the matching subset (from the GPS cache — no reload); in
 * `all`/`highlight` the universe stays drawn and only per-track opacity changes, so a column-filter
 * edit never re-fetches. A short coalesce window collapses rapid edits; the camera is kept.
 *
 * Highlight goes through the 'grid' source of the map's highlight compositor (activities-overview.ts),
 * so the paint tool's 'paint' source can layer over it without either clobbering the other.
 */
export function createMapDisplayController(initialUniverse: number[], initialFilteredIds: number[]): MapDisplayController {
	const idsKey = (ids: number[]): string => `${ids.length}:${ids.join(',')}`;
	const COALESCE_MS = 150; // matches the segment-stats effort-refresh debounce

	// Restored at construction, not after the first draw: the map is built from drawIds() straight away,
	// so a later flip to 'hide' would be a visible redraw of tracks the viewer already asked to hide.
	let mode: MapDisplayMode = storedMapDisplayMode();
	let universe = initialUniverse;
	let filteredIds = initialFilteredIds;
	let dimming = false; // any non-owner column filter active (constructor onData corrects URL-filter loads)
	// activities-page-init boots the map with the universe; that's what's drawn until a `hide` narrows it.
	let appliedDrawKey = idsKey(initialUniverse);
	let timer: ReturnType<typeof setTimeout> | undefined;

	const drawIds = (): number[] => (mode === 'hide' ? filteredIds : universe);
	// null = source inactive → the map shows every drawn track at base opacity for that source.
	const highlightIds = (): number[] | null => (mode === 'highlight' && dimming ? filteredIds : null);

	function apply(): void {
		const drawKey = idsKey(drawIds());
		if (drawKey !== appliedDrawKey) {
			appliedDrawKey = drawKey;
			setActivitiesFilter(drawIds());
		}
		// One cheap paint-property set; the compositor holds the state, so no local diffing needed.
		setOverviewHighlight('grid', highlightIds());
	}

	// A RESTORED 'hide' has to take effect now. appliedDrawKey is seeded to the universe because
	// activities-page-init really does boot the map with it — so without this the map opens showing every
	// track and only narrows when the first grid response lands a moment later. That flash is precisely
	// what restoring the mode at construction is supposed to avoid, and nothing would have reported it.
	if (mode === 'hide') apply();

	return {
		onGridData(res, hasColumnFilters) {
			const ownerIds = (res as ActivityGridResponse).ownerIds;
			if (ownerIds) universe = ownerIds; // absent only on a stale cached response shape
			filteredIds = res.filteredIds;
			dimming = hasColumnFilters;
			// Unconditional schedule: apply() diffs the draw set itself and the highlight push is one
			// cheap paint-property set, so sort/page onData spam costs nothing.
			if (timer) clearTimeout(timer);
			timer = setTimeout(() => { timer = undefined; apply(); }, COALESCE_MS);
		},
		setMode(next) {
			if (next === mode) return;
			mode = next;
			setEnum(MAP_DISPLAY_KEY, next);
			if (timer) { clearTimeout(timer); timer = undefined; }
			apply(); // mode toggles apply immediately, no coalesce
		},
	};
}
