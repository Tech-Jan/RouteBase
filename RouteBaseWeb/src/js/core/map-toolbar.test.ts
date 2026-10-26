import { must } from './invariant';
import { afterEach, describe, expect, it } from 'vitest';
import { setupMapToolbar } from './map-toolbar';

let teardown: (() => void) | null = null;

afterEach(() => {
	teardown?.();
	teardown = null;
	document.body.innerHTML = '';
});

function toolbar(picker = true): HTMLDetailsElement {
	document.body.innerHTML = `
		<div class="race-map-toolbar">
			<details class="map-dd" ${picker ? 'data-picker="true"' : ''} open>
				<summary class="map-dd-summary">
					<span class="map-dd-label">Style</span>
					<span class="map-dd-value">Map</span>
				</summary>
				<div class="map-dd-menu" id="switcher"></div>
			</details>
		</div>
		<div id="outside">elsewhere</div>`;
	return document.querySelector<HTMLDetailsElement>('.map-dd')!;
}

/** MapShell renders the basemap buttons into the menu AFTER setup — that ordering is the point. */
function renderSwitcherButtons(active: string, ...labels: string[]): void {
	const menu = document.getElementById('switcher')!;
	menu.innerHTML = labels
		.map((l) => `<button class="map-dd-item${l === active ? ' active' : ''}">${l}</button>`)
		.join('');
}

describe('setupMapToolbar', () => {
	it('shows the active option in the summary', async () => {
		const dd = toolbar();
		teardown = setupMapToolbar();

		renderSwitcherButtons('Satellite', 'Map', 'Satellite', 'Terrain');
		await new Promise((r) => setTimeout(r, 0));   // MutationObserver is async

		expect(dd.querySelector('.map-dd-value')!.textContent).toBe('Satellite');
	});

	it('picks up buttons MapShell renders AFTER setup — the whole reason it is delegated', async () => {
		// Binding per button at setup time would silently miss every basemap button, because the shell
		// renders them later. The click handler is on the <details>, so it cannot miss them.
		const dd = toolbar();
		teardown = setupMapToolbar();
		renderSwitcherButtons('Map', 'Map', 'Satellite');

		must(dd.querySelectorAll<HTMLButtonElement>('.map-dd-item')[1], 'second switcher button').click();
		await new Promise((r) => requestAnimationFrame(() => r(null)));

		expect(dd.open).toBe(false);   // choosing an option closes the menu
	});

	it('does not feed its own observer — syncing the value must not retrigger sync', async () => {
		// The value span lives in the summary, and a textContent write is a childList mutation. An
		// observer watching an ancestor of it therefore sees its own write and re-fires for ever —
		// an infinite microtask loop that hard-freezes the tab. Watch the menu only.
		const dd = toolbar();
		teardown = setupMapToolbar();
		renderSwitcherButtons('Satellite', 'Map', 'Satellite', 'Terrain');
		await new Promise((r) => setTimeout(r, 0));

		let churn = 0;
		const spy = new MutationObserver((records) => { churn += records.length; });
		spy.observe(dd, { subtree: true, childList: true, attributes: true });
		await new Promise((r) => setTimeout(r, 0));
		spy.disconnect();

		expect(churn).toBe(0);   // settled: nothing is still rewriting the summary
	});

	it('an outside click closes an open dropdown', () => {
		const dd = toolbar();
		teardown = setupMapToolbar();

		document.getElementById('outside')!.click();

		expect(dd.open).toBe(false);
	});

	it('a non-picker menu (Size) keeps no value and stays open across repeated clicks', async () => {
		// You click Taller three times in a row; closing after each would be maddening.
		const dd = toolbar(false);
		teardown = setupMapToolbar();
		renderSwitcherButtons('', 'Taller', 'Smaller');

		dd.querySelector<HTMLButtonElement>('.map-dd-item')!.click();
		await new Promise((r) => requestAnimationFrame(() => r(null)));

		expect(dd.open).toBe(true);
	});

	it('teardown removes the document listener', () => {
		const dd = toolbar();
		const stop = setupMapToolbar();
		stop();

		document.getElementById('outside')!.click();

		expect(dd.open).toBe(true);   // no longer listening
	});
});
