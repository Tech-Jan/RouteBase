/**
 * Activities listing page — page init / orchestration.
 *
 * Extracted from the former inline <script> in Pages/Activities.cshtml. Boots the overview map via
 * activities-overview.ts's startActivitiesMap export (a shared Rollup chunk).
 *
 * Server data comes from the SAME #activitiesGridBootstrap island the grid reads — there is no separate
 * page-config shim any more. It used to be `window.activitiesPageConfig`, the last window global in the
 * app; four of its five fields were duplicates of this island (or, for the basemap, of #appSettings) and
 * the fifth, pageActivityIds, was read by nothing.
 */

import { readData } from '../core/data-attrs';
import type { ActivitiesGridBootstrap } from './overview-types';
import { defaultBasemapId } from '../core/basemap-registry';
import { startActivitiesMap } from './activities-overview';
import { setupPhotosOverlay } from './activities-photos-overlay';
import { readIsland } from '../core/page-bootstrap';
import { setMapTilerApiKey } from '../core/map-api-key';
import { getAccountSettings } from '../core/account-settings';

// NOTE: column-visibility toggling is handled (without reload) by overview/activities-grid.ts, which owns
// the client-rendered grid and reads this same island.

function setupMap(boot: ActivitiesGridBootstrap): void {
	// Resolve the default basemap id (registry decides the URL + button set at runtime). Always honor
	// the profile default when it resolves, else liberty — consistent with Details/Segment pages.
	// Persist/pass ids, never resolved URLs.
	const defaultId = defaultBasemapId(getAccountSettings().map.defaultStyle);

	// Initial load draws the owner-scoped universe (the display-mode default is `highlight`, which
	// keeps every owner-scoped track drawn and dims the ones outside the column filters — the grid's
	// map-display controller pushes that dimming). Same precedence activities-grid.ts uses for its own
	// initial universe, so the two cannot start from different sets. The overview module fetches the
	// cheap up-front viewport, builds the map framed there on the default basemap, then streams the GPS.
	const universe = boot.initialData?.ownerIds ?? boot.initialData?.filteredIds ?? [];
	startActivitiesMap(universe, defaultId);
}

/**
 * Wires the map-toolbar dropdowns (.act-dd) — same shell behaviour as the activity-details and
 * segment-statistics pages. Style buttons are rendered into the switcher container by MapShell; the
 * size buttons keep their IDs and handlers. This only manages the <details> shell: keeping each
 * summary's value label in sync with the active option, closing single-select pickers after a
 * choice, and closing any open dropdown on an outside click (native <details> does neither on its own).
 */
function setupToolbarDropdowns(): void {
	const dropdowns = Array.from(document.querySelectorAll<HTMLDetailsElement>('.act-dd'));

	dropdowns.forEach((dd) => {
		const isPicker = readData(dd, 'picker') === 'true'; // single-select (style) vs action menu (size)
		const valueEl = dd.querySelector<HTMLElement>('.act-dd-value');
		const syncValue = (): void => {
			if (!valueEl) return;
			const active = dd.querySelector<HTMLElement>('.act-dd-item.active');
			if (active?.textContent) valueEl.textContent = active.textContent.trim();
		};
		if (isPicker) {
			syncValue();
			new MutationObserver(syncValue).observe(dd, {
				subtree: true, attributes: true, attributeFilter: ['class'],
			});
			// Close the picker once a choice is made. Delegated because the style switcher buttons are
			// rendered by MapShell *after* this runs (the async bounds fetch precedes shell creation),
			// so a per-button listener would never attach. Defer so the option's own handler sets
			// .active first.
			dd.addEventListener('click', (e) => {
				if ((e.target as HTMLElement).closest('.act-dd-item')) {
					requestAnimationFrame(() => { dd.open = false; });
				}
			});
		}
	});

	// Outside-click closes any open dropdown (size stays open across repeated grow/shrink clicks).
	document.addEventListener('click', (e: Event) => {
		dropdowns.forEach((dd) => {
			if (dd.open && !dd.contains(e.target as Node)) dd.open = false;
		});
	});
}

function init(): void {
	// Initialise the MapTiler key seam from the #mapTilerKey island (emitted by _ProfileSettingsBootstrap)
	// before the overview map is built. Absent island → '' → keyless (liberty) registry.
	setMapTilerApiKey(readIsland<string>('mapTilerKey', ''));

	const boot = readIsland<ActivitiesGridBootstrap | null>('activitiesGridBootstrap', null);
	// total === 0 (or no island) → no map markup on the page.
	if (boot && (boot.initialData?.total ?? 0) > 0) {
		setupToolbarDropdowns();
		setupMap(boot);
		setupPhotosOverlay(); // off until the user clicks the Photos toggle in the map toolbar
	}
}

if (document.readyState === 'loading') {
	document.addEventListener('DOMContentLoaded', init);
} else {
	init();
}
