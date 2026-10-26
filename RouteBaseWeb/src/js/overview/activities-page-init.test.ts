import { describe, it, expect, vi, beforeEach } from 'vitest';

/**
 * The Activities page boot, pinned at the seam it was just moved onto.
 *
 * This entry had NO test while it read `window.activitiesPageConfig` — the app's last window global — and
 * the global carried four values that also existed elsewhere. It now reads the SAME
 * #activitiesGridBootstrap island the grid reads, plus the #appSettings seam for the basemap. Two things
 * can rot silently and neither raises an error:
 *
 *   1. the map's id universe. The grid picks `ownerIds ?? filteredIds`; if this file ever picks something
 *      else, the map opens on a different set of rides than the grid thinks it drew, and nothing throws.
 *   2. the basemap. A wrong id resolves to Liberty through the registry's fallback, so a broken read looks
 *      exactly like a keyless user.
 */

const startActivitiesMap = vi.fn();
const setupPhotosOverlay = vi.fn();

vi.mock('./activities-overview', () => ({
	startActivitiesMap: (ids: number[], defaultId?: string) => startActivitiesMap(ids, defaultId),
}));
vi.mock('./activities-photos-overlay', () => ({ setupPhotosOverlay: () => setupPhotosOverlay() }));

// core/map-api-key and core/basemap-registry are deliberately REAL. The registry reads the key seam to
// decide whether a keyRequired basemap resolves, so stubbing the seam would stub out the exact fallback
// the second describe exists to pin. No #mapTilerKey island here → '' → keyless, like a share page.

/** The island as Razor writes it — only the fields this entry reads. */
function mountIsland(initialData: Record<string, unknown>): void {
	document.body.innerHTML =
		'<script type="application/json" id="activitiesGridBootstrap">'
		+ JSON.stringify({ initialData })
		+ '</script>';
}

/**
 * The entry boots on import (module side effect), so every case needs a fresh module graph — which also
 * resets the account-settings singleton to its defaults. `savedStyle` is applied AFTER resetModules on
 * purpose: set before, it would land on the previous graph's copy of the seam and the entry would read an
 * untouched one.
 */
async function boot(savedStyle?: string): Promise<void> {
	vi.resetModules();
	if (savedStyle !== undefined) {
		const { setAccountSettings } = await import('../core/account-settings');
		setAccountSettings({ map: { defaultStyle: savedStyle } });
	}
	await import('./activities-page-init');
}

beforeEach(() => {
	vi.clearAllMocks();
	document.body.innerHTML = '';
});

describe('activities-page-init — the map universe', () => {
	it('boots the map on ownerIds, the same set the grid adopts', async () => {
		mountIsland({ ownerIds: [1, 2, 3], filteredIds: [1], total: 3 });

		await boot();

		expect(startActivitiesMap).toHaveBeenCalledTimes(1);
		expect(startActivitiesMap.mock.calls[0]?.[0]).toEqual([1, 2, 3]);
	});

	it('falls back to filteredIds when the response declares no owner scope', async () => {
		mountIsland({ filteredIds: [7, 8], total: 2 });

		await boot();

		expect(startActivitiesMap.mock.calls[0]?.[0]).toEqual([7, 8]);
	});

	it('does nothing when the page has no activities — there is no map markup to wire', async () => {
		mountIsland({ ownerIds: [], filteredIds: [], total: 0 });

		await boot();

		expect(startActivitiesMap).not.toHaveBeenCalled();
		expect(setupPhotosOverlay).not.toHaveBeenCalled();
	});

	it('does nothing when the island is absent entirely', async () => {
		await boot();

		expect(startActivitiesMap).not.toHaveBeenCalled();
	});
});

describe('activities-page-init — the opening basemap', () => {
	it('takes the basemap from the account-settings seam, not from any page config', async () => {
		mountIsland({ ownerIds: [1], total: 1 });

		await boot('positron');

		expect(startActivitiesMap.mock.calls[0]?.[1]).toBe('positron');
	});

	it('degrades to the keyless entry when the saved default needs a MapTiler key', async () => {
		mountIsland({ ownerIds: [1], total: 1 });

		await boot('streets');   // keyRequired, and no key is configured here

		expect(startActivitiesMap.mock.calls[0]?.[1]).toBe('liberty');
	});
});
