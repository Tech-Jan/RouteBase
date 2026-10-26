import { describe, it, expect, beforeEach, vi } from 'vitest';
import { createMapDisplayController, setupMapDisplayModeToggle, storedMapDisplayMode } from './activities-map-display';

/**
 * The map's display mode as a DEVICE preference.
 *
 * Two halves, and the second is the one that rots silently: the controller must OPEN in the stored mode
 * (restoring after the first draw is a visible redraw of tracks the viewer asked to hide), and the
 * segmented control must agree with it — the markup hard-codes `active` on Highlight, so a restored
 * 'hide' would otherwise draw a hidden-filtered map under a button reading Highlight, with nothing
 * anywhere reporting a problem.
 */

const setActivitiesFilter = vi.fn();
const setOverviewHighlight = vi.fn();

vi.mock('./activities-overview', () => ({
	setActivitiesFilter: (ids: number[]) => setActivitiesFilter(ids),
	setOverviewHighlight: (ids: number[] | null) => setOverviewHighlight(ids),
}));

const UNIVERSE = [1, 2, 3, 4];
const FILTERED = [1, 2];

function mountToggle(): HTMLElement {
	document.body.innerHTML = `
		<div id="actMapDisplayMode">
			<button class="act-map-mode-btn" data-mode="all"></button>
			<button class="act-map-mode-btn active" data-mode="highlight"></button>
			<button class="act-map-mode-btn" data-mode="hide"></button>
		</div>`;
	return document.getElementById('actMapDisplayMode') as HTMLElement;
}

const activeMode = (): string | undefined =>
	document.querySelector<HTMLElement>('.act-map-mode-btn.active')?.dataset['mode'];

beforeEach(() => {
	localStorage.clear();
	vi.clearAllMocks();
	document.body.innerHTML = '';
});

describe('activities map display — the remembered mode', () => {
	it('defaults to highlight when this browser has never chosen', () => {
		expect(storedMapDisplayMode()).toBe('highlight');
	});

	it('persists the mode the user picks', () => {
		mountToggle();
		const controller = createMapDisplayController(UNIVERSE, FILTERED);
		setupMapDisplayModeToggle(controller);

		document.querySelector<HTMLButtonElement>('[data-mode="hide"]')!.click();

		expect(storedMapDisplayMode()).toBe('hide');
	});

	it('opens in the stored mode — hide draws only the filtered set, from the start', () => {
		mountToggle();
		const first = createMapDisplayController(UNIVERSE, FILTERED);
		setupMapDisplayModeToggle(first);
		document.querySelector<HTMLButtonElement>('[data-mode="hide"]')!.click();

		// A fresh page load: a NEW controller over the same data.
		setActivitiesFilter.mockClear();
		mountToggle();
		const reloaded = createMapDisplayController(UNIVERSE, FILTERED);
		setupMapDisplayModeToggle(reloaded);

		// The button agrees with the restored mode rather than the markup's hard-coded Highlight.
		expect(activeMode()).toBe('hide');

		// And the narrowing happens AT CONSTRUCTION, not on the first grid response. page-init boots the
		// map with the whole universe, so a controller that only applied later would open on every track
		// and visibly drop to the filtered set a moment afterwards.
		expect(setActivitiesFilter).toHaveBeenCalledWith(FILTERED);

		// Switching back to highlight restores the full universe.
		setActivitiesFilter.mockClear();
		document.querySelector<HTMLButtonElement>('[data-mode="highlight"]')!.click();
		expect(setActivitiesFilter).toHaveBeenCalledWith(UNIVERSE);
	});

	it('ignores a stored value that is not a mode', () => {
		localStorage.setItem('rb1.map.activities.display', 'invisible');

		expect(storedMapDisplayMode()).toBe('highlight');
	});
});
